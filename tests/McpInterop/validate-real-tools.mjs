// Validates the complete REAL tools/list payload with the OFFICIAL @modelcontextprotocol/sdk
// parser.
//
// Why this exists alongside tests\PluginLoadTest's own schema checks: that suite validates the
// schemas using THIS project's reading of JSON Schema. A schema that only satisfies my own
// parser - a type spelling I invented, a shape the spec does not actually permit - would pass
// there and still break a real agent. Here the same bytes are fed to somebody else's
// implementation of the spec, which is the whole point.
//
// Input: real-tools.json, dumped by tests\PluginLoadTest with DALAMUD_MCP_DUMP_TOOLS set.
// It is a raw JSON-RPC tools/list response.
//
// Usage: node validate-real-tools.mjs [path-to-tools-list.json]

import { readFileSync } from "node:fs";
import { ListToolsResultSchema, CallToolResultSchema } from "@modelcontextprotocol/sdk/types.js";
import { AjvJsonSchemaValidator } from "@modelcontextprotocol/sdk/validation/ajv-provider.js";

const path = process.argv[2] ?? new URL("./real-tools.json", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1");

let checks = 0;
let failures = 0;

function check(label, ok, detail = "") {
  checks++;
  if (ok) {
    console.log(`  [PASS] ${label}`);
  } else {
    failures++;
    console.log(`  [FAIL] ${label} -> ${detail}`);
  }
}

function firstLine(text, limit = 400) {
  if (text === null || text === undefined) return "(empty)";
  const s = String(text).replace(/\r?\n/g, " ⏎ ");
  return s.length > limit ? s.slice(0, limit) + "…" : s;
}

const raw = readFileSync(path, "utf8");
const response = JSON.parse(raw);

check("the dumped payload is a JSON-RPC response", response?.jsonrpc === "2.0", firstLine(raw.slice(0, 120)));
check("the dumped payload carries a result, not an error", response?.error === undefined, firstLine(JSON.stringify(response?.error)));

// THE assertion: the official SDK's own schema for tools/list must accept the payload. If any
// of the shipped tools has a malformed name, description, or inputSchema, this parse fails and the
// issues list names the exact path.
//
// NOTE ON SCOPE, measured rather than assumed: the SDK's ToolSchema checks that inputSchema is
// an object with type:"object" and that each property is an object - it does NOT validate that a
// property's "type" is one of JSON Schema's legal names (its AssertObjectSchema is
// `z.custom(v => v !== null && typeof v === 'object')`). Verified by re-injecting the original
// `"type":"array of integer"` defect into this payload: this check still PASSED. So it is a
// useful shape check but NOT a JSON Schema validity check, and the ajv meta-schema pass below is
// what actually catches an illegal type name.
const parsed = ListToolsResultSchema.safeParse(response?.result);
check(
  "the official SDK accepts the complete shipped tool catalog as tools/list output",
  parsed.success,
  parsed.success ? "" : firstLine(JSON.stringify(parsed.error?.issues ?? parsed.error)),
);

if (parsed.success) {
  const tools = parsed.data.tools;
  console.log(`  [note] the official schema validated ${tools.length} tool definitions`);

  check("every tool survived with a name", tools.every((t) => typeof t.name === "string" && t.name.length > 0), "");
  check(
    "every tool survived with an object inputSchema",
    tools.every((t) => t.inputSchema !== null && typeof t.inputSchema === "object"),
    firstLine(JSON.stringify(tools.find((t) => t.inputSchema === null)?.name)),
  );

  // The union that had the real defect: it must still be a union after the SDK's own
  // normalisation, because that is what an agent's binding generator will see.
  const walk = tools.find((t) => t.name === "read_pointer_chain");
  const offsets = walk?.inputSchema?.properties?.offsets;
  check(
    "read_pointer_chain.offsets is an array with a declared items type",
    offsets?.type === "array" && offsets?.items !== undefined,
    firstLine(JSON.stringify(offsets)),
  );
  check(
    "read_pointer_chain.offsets accepts both integers and hex strings",
    Array.isArray(offsets?.items?.type) &&
      offsets.items.type.includes("integer") &&
      offsets.items.type.includes("string"),
    firstLine(JSON.stringify(offsets?.items)),
  );

  // Every property the tools advertise - the SDK parsed them, so this is counting what an
  // agent would actually receive rather than what the source says.
  const propertyCount = tools.reduce((n, t) => n + Object.keys(t.inputSchema?.properties ?? {}).length, 0);
  const requiredCount = tools.reduce((n, t) => n + (t.inputSchema?.required?.length ?? 0), 0);
  check("the SDK saw a non-trivial number of parameters", propertyCount > 30, `only ${propertyCount} propert(ies)`);
  console.log(`  [note] ${propertyCount} parameters, ${requiredCount} of them required`);

  // A tool with no parameters must still be a valid object schema (the "empty properties" case
  // the synthetic controls covered, now on the real set).
  const noArg = tools.filter((t) => Object.keys(t.inputSchema?.properties ?? {}).length === 0);
  console.log(`  [note] ${noArg.length} tool(s) take no arguments: ${noArg.map((t) => t.name).join(", ") || "(none)"}`);
}

// A real JSON Schema validation pass, using the ajv that ships inside the official SDK.
//
// This is the check that would have caught the original defect on its own: `"type":"array of
// integer"` is not one of the seven legal type names, so the 2020-12 meta-schema rejects the
// whole schema. The SDK's own ToolSchema does NOT catch it (see the note above), which is why
// the meta-schema pass is the one worth having.
const validator = new AjvJsonSchemaValidator();
const metaProblems = [];
let metaChecked = 0;

for (const tool of response?.result?.tools ?? []) {
  const schema = tool?.inputSchema;
  if (!schema || typeof schema !== "object") continue;
  metaChecked++;
  try {
    // Compiling against the 2020-12 meta-schema is the validity test: an illegal "type" value
    // throws here.
    validator.getValidator(schema);
  } catch (error) {
    metaProblems.push(`${tool.name}: ${error?.message ?? error}`);
  }
}

check(
  "every shipped inputSchema is a VALID JSON Schema (2020-12 meta-schema, via the SDK's own ajv)",
  metaProblems.length === 0,
  metaProblems.length === 0 ? "" : firstLine(metaProblems.join(" | ")),
);
console.log(`  [note] ${metaChecked} schema(s) compiled against the 2020-12 meta-schema`);

// ---------------------------------------------------------------------------------------------
// SELF-PROVING NEGATIVE CONTROL
//
// The check above is only worth having if it can actually fail. So re-inject the exact defect
// that was found in this repo - offsets advertised as "array of integer", which is not one of
// JSON Schema's seven legal type names - into the SAME real payload, and require the validator
// to reject it. If this ever stops failing, the positive check above has stopped meaning
// anything and the suite says so rather than going quietly green.
// ---------------------------------------------------------------------------------------------
const DEFECTIVE_TYPE = "array of integer";
const healthy = JSON.stringify(response?.result?.tools ?? []);
if (!healthy.includes('"items":{"type":["integer","string"]}')) {
  check(
    "the negative control can be constructed from this payload",
    false,
    "the real payload no longer contains the union this control depends on; update the control",
  );
} else {
  const poisoned = JSON.parse(raw);
  const poisonTool = poisoned.result.tools.find((t) => t.name === "read_pointer_chain");
  poisonTool.inputSchema.properties.offsets = {
    description: poisonTool.inputSchema.properties.offsets.description,
    type: DEFECTIVE_TYPE,
  };

  let rejected = false;
  let rejectionMessage = "";
  try {
    validator.getValidator(poisonTool.inputSchema);
  } catch (error) {
    rejected = true;
    rejectionMessage = error?.message ?? String(error);
  }

  check(
    `the validator REJECTS the original defect ("type":"${DEFECTIVE_TYPE}")`,
    rejected,
    rejected ? "" : "the meta-schema accepted an illegal type name - the positive check is vacuous",
  );
  if (rejected) console.log(`  [note] rejected with: ${firstLine(rejectionMessage)}`);
}

// A CallToolResult from this server must also parse - the shape an agent unwraps. Build one the
// way the server emits it (content array with a text part) and confirm the SDK accepts it.
const sampleResult = {
  content: [{ type: "text", text: '{"ok":true}' }],
  isError: false,
};
const resultParsed = CallToolResultSchema.safeParse(sampleResult);
check(
  "the SDK's own CallToolResult schema accepts this server's result shape",
  resultParsed.success,
  resultParsed.success ? "" : firstLine(JSON.stringify(resultParsed.error?.issues)),
);


console.log();
console.log(`${checks - failures}/${checks} real-schema checks passed`);
process.exit(failures === 0 ? 0 : 1);
