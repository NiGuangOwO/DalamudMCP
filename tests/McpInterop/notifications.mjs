// Independent SDK clients must receive server-initiated invalidations without polling.
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { SSEClientTransport } from "@modelcontextprotocol/sdk/client/sse.js";
import { ToolListChangedNotificationSchema } from "@modelcontextprotocol/sdk/types.js";

const host = spawn("dotnet", [fileURLToPath(new URL("./host/bin/Debug/net10.0/interophost.dll", import.meta.url)), "--notification-control"],
  { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
let diagnostics = "";
host.stderr.on("data", (chunk) => { diagnostics += chunk; });
const lines = createInterface({ input: host.stdout });
const port = await new Promise((resolve, reject) => {
  const deadline = setTimeout(() => reject(new Error("fixture did not report a port")), 10000);
  lines.once("line", (line) => {
    clearTimeout(deadline);
    const match = /^PORT (\d+)$/.exec(line);
    if (!match) reject(new Error(`invalid port line: ${line}`)); else resolve(Number(match[1]));
  });
  host.once("error", reject);
});
let checks = 0;
function check(ok, label) { if (!ok) throw new Error(label); checks++; console.log(`PASS ${label}`); }
function nextChange(client) {
  return new Promise((resolve, reject) => {
    const deadline = setTimeout(() => reject(new Error("SDK did not receive list_changed")), 5000);
    client.setNotificationHandler(ToolListChangedNotificationSchema, (notification) => { clearTimeout(deadline); resolve(notification); });
  });
}
const http = new Client({ name: "catalog-change-http", version: "1.0" });
const legacy = new Client({ name: "catalog-change-sse", version: "1.0" });
try {
  await http.connect(new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${port}/mcp`)));
  await legacy.connect(new SSEClientTransport(new URL(`http://127.0.0.1:${port}/sse`)));
  check(http.getServerCapabilities()?.tools?.listChanged === true, "official HTTP SDK sees listChanged=true");
  check(legacy.getServerCapabilities()?.tools?.listChanged === true, "official legacy SSE SDK sees listChanged=true");
  await Promise.all([http.listTools(), legacy.listTools()]);
  const first = nextChange(http); const second = nextChange(legacy);
  host.stdin.write("change\n");
  const notifications = await Promise.all([first, second]);
  check(notifications.every((n) => n.method === "notifications/tools/list_changed" && n.id === undefined), "both independent SDK transports receive unsolicited notifications");
  const catalogs = await Promise.all([http.listTools(), legacy.listTools()]);
  check(catalogs.every((c) => c.tools.find((t) => t.name === "ping")?.description === "Metadata update 1"), "both SDK clients refresh current metadata after notification");
  console.log(`${checks}/${checks} official notification checks passed`);
} finally {
  await Promise.allSettled([http.close(), legacy.close()]);
  host.stdin.end();
  await new Promise((resolve) => { if (host.exitCode !== null) resolve(); else host.once("exit", resolve); setTimeout(() => { host.kill(); resolve(); }, 3000).unref(); });
  lines.close();
  if (host.exitCode && diagnostics) console.error(diagnostics);
}
