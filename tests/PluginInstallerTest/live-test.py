"""Opt-in live installer check. Requires mutation authorization and an already-running game.

Builds an inert temporary plugin, serves a loopback-only repository, exercises repository
CRUD plus install/uninstall through MCP, and removes its own repository in finally.
Never starts the game, deletes configuration, or uninstalls an existing plugin.
"""
import argparse
import functools
import http.server
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import time
import urllib.request
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("--expected-mvid", required=True)
parser.add_argument("--report", required=True)
options = parser.parse_args()
base = "http://127.0.0.1:18777"
headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
name = "DalamudMCPInstallerProbe"
version = "1.0.0.0"
report = {"checks": [], "operations": []}


def check(ok, label):
    if not ok:
        raise AssertionError(label)
    report["checks"].append(label)
    print("PASS", label, flush=True)


def rpc(method, params):
    request = urllib.request.Request(base + "/mcp", data=json.dumps({
        "jsonrpc": "2.0", "id": 1, "method": method, "params": params
    }).encode(), headers=headers)
    with urllib.request.urlopen(request, timeout=20) as response:
        if response.headers.get("Mcp-Session-Id"):
            headers["Mcp-Session-Id"] = response.headers["Mcp-Session-Id"]
        value = json.load(response)
    if "error" in value:
        raise RuntimeError(value["error"])
    return value["result"]


def call(tool, arguments=None):
    result = rpc("tools/call", {"name": tool, "arguments": arguments or {}})
    if result.get("isError"):
        raise RuntimeError(result["content"])
    return json.loads(result["content"][0]["text"])


def operation(tool, arguments=None):
    op = call(tool, arguments)
    report["operations"].append(op)
    deadline = time.monotonic() + 180
    while not op["terminal"]:
        if time.monotonic() >= deadline:
            raise TimeoutError("operation remains in progress; do not retry: " + op["operationId"])
        time.sleep(0.25)
        op = call("get_plugin_operation", {"operationId": op["operationId"]})
    report["operations"][-1] = op
    if op["status"] != "succeeded":
        raise RuntimeError(op)
    return op["result"]


def repositories():
    rows, args = [], {"limit": 100}
    while True:
        page = call("list_plugin_repositories", args)
        rows.extend(page["repositories"])
        if page["nextCursor"] is None:
            return rows
        args["cursor"] = page["nextCursor"]


class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


with tempfile.TemporaryDirectory(prefix="dalamud-installer-live-") as scratch:
    root = Path(scratch)
    sdk = Path(os.environ["APPDATA"]) / "XIVLauncherCN/addon/Hooks/dev/Dalamud.dll"
    (root / "Probe.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><AssemblyName>{name}</AssemblyName>
<AssemblyVersion>{version}</AssemblyVersion><Version>{version}</Version><PlatformTarget>x64</PlatformTarget></PropertyGroup>
<ItemGroup><Reference Include="Dalamud"><HintPath>{sdk}</HintPath><Private>false</Private></Reference></ItemGroup>
</Project>""", encoding="utf-8")
    (root / "Plugin.cs").write_text("using Dalamud.Plugin; public sealed class Plugin : IDalamudPlugin { public void Dispose() { } }", encoding="utf-8")
    subprocess.run(["dotnet", "build", str(root / "Probe.csproj"), "-c", "Release", "--nologo", "-v", "q"], check=True)
    manifest = {"Author": "NiGuangOwO", "Name": name, "InternalName": name, "AssemblyVersion": version,
                "ApplicableVersion": "any", "DalamudApiLevel": 15, "LoadRequiredState": 2,
                "LoadSync": False, "CanUnloadAsync": True,
                "Description": "Inert temporary DalamudMCP installer verification fixture."}
    with zipfile.ZipFile(root / "latest.zip", "w", zipfile.ZIP_DEFLATED) as archive:
        archive.write(root / f"bin/Release/net10.0-windows/{name}.dll", f"{name}.dll")
        archive.writestr(f"{name}.json", json.dumps(manifest))
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(QuietHandler, directory=str(root)))
    url = f"http://127.0.0.1:{server.server_port}/pluginmaster.json"
    download = f"http://127.0.0.1:{server.server_port}/latest.zip"
    manifest.update({"DownloadLinkInstall": download, "DownloadLinkUpdate": download})
    (root / "pluginmaster.json").write_text(json.dumps([manifest]), encoding="utf-8")
    threading.Thread(target=server.serve_forever, daemon=True).start()
    added = False
    installed = False
    try:
        health = json.load(urllib.request.urlopen(base + "/health"))
        report["health"] = health
        check(health["buildMvid"].lower() == options.expected_mvid.lower(), "running host MVID matches tested Debug build")
        check(health["mutatingToolsEnabled"], "mutation gate enabled")
        rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "installer-live-test", "version": "1"}})
        catalog, args = [], {}
        while True:
            page = rpc("tools/list", args)
            catalog.extend(t["name"] for t in page["tools"])
            if "nextCursor" not in page:
                break
            args = {"cursor": page["nextCursor"]}
        report["catalogCount"] = len(catalog)
        check(len(catalog) == 148 and "install_plugin" in catalog, "complete 148-tool catalog read across all pages")
        report["loggedIn"] = call("get_client_state")["loggedIn"]
        call("get_local_player")
        check(call("plugin_list", {"query": name})["total"] == 0, "probe plugin did not exist before test")
        before = repositories()
        check(not any(r["url"] == url for r in before), "test repository is new")
        # Record ownership before dispatch, so cleanup can inspect a partially failed add.
        added = True
        operation("add_plugin_repository", {"url": url, "enabled": False})
        row = next(r for r in repositories() if r["url"] == url)
        check(row["enabled"] is False and not row["effectiveEnabled"], "add disabled repository verifies configuration and inactive runtime")
        operation("set_plugin_repository_enabled", {"url": url, "enabled": True})
        row = next(r for r in repositories() if r["url"] == url)
        check(row["enabled"] and row["effectiveEnabled"] and row["state"] == "Success", "enable repository completes real index download")
        available = call("list_available_plugins", {"query": name, "repositoryUrl": url})["plugins"]
        check(len(available) == 1 and available[0]["version"] == version, "probe discovered through runtime available catalog")
        installed = True
        result = operation("install_plugin", {"internalName": available[0]["internalName"], "repositoryUrl": available[0]["repositoryUrl"], "expectedVersion": available[0]["version"]})
        check(result["installed"] and result["isLoaded"] and result["state"] == "Loaded", "real package installation and plugin load verified")
        described = call("plugin_describe", {"pluginName": name})
        check(described["isLoaded"] and not described["isDev"] and described["version"] == version and described["manifest"]["installedFromUrl"] == url, "independent installed-plugin observation confirms version and source")
        result = operation("uninstall_plugin", {"internalName": name, "expectedVersion": version})
        installed = False
        check(result["removedFromManager"] and result["deletionScheduled"] and not result["filesDeleted"] and result["configurationPreserved"], "uninstall verifies removal and deferred cleanup, preserving configuration")
        check(call("plugin_list", {"query": name})["total"] == 0, "probe absent from installed plugin list after uninstall")
        operation("set_plugin_repository_enabled", {"url": url, "enabled": False})
        row = next(r for r in repositories() if r["url"] == url)
        check(not row["enabled"] and not row["effectiveEnabled"], "disable repository verified")
        operation("remove_plugin_repository", {"url": url})
        added = False
        after = repositories()
        check(not any(r["url"] == url for r in after), "temporary repository removed")
        stable = lambda rows: sorted((r["url"], r["enabled"], r["effectiveEnabled"]) for r in rows)
        check(stable(before) == stable(after), "all original repository URLs and enabled states preserved")
        report["success"] = True
    except BaseException as error:
        report["success"] = False
        report["error"] = str(error)
        raise
    finally:
        try:
            if installed and call("plugin_list", {"query": name})["total"] == 1:
                operation("uninstall_plugin", {"internalName": name, "expectedVersion": version})
            if added and any(r["url"] == url for r in repositories()):
                operation("remove_plugin_repository", {"url": url})
        finally:
            Path(options.report).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            server.shutdown()
            server.server_close()
print(f"{len(report['checks'])} live installer checks passed; report: {options.report}")
