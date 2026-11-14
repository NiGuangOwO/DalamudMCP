"""Opt-in developer-plugin management check using only an inert temporary plugin.

Requires an already-running MCP host and mutation authorization. Never starts the game,
operates existing plugin lifecycles or uses gameplay input. Leaves temporary files intact
if plugin cleanup fails, so a registered plugin is never deleted underneath Dalamud.
"""
import argparse
import hashlib
import functools
import http.server
import json
import os
from pathlib import Path
import shutil
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
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
base = "http://127.0.0.1:18777"
headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
report = {"checks": [], "gameplayInputUsed": False, "existingPluginLifecyclesChanged": False}


def check(ok, label):
    if not ok:
        raise AssertionError(label)
    report["checks"].append(label)
    print("PASS", label, flush=True)


def rpc(method, params):
    with opener.open(urllib.request.Request(base + "/mcp", json.dumps({
        "jsonrpc": "2.0", "id": 1, "method": method, "params": params
    }).encode(), headers), timeout=40) as response:
        if response.headers.get("Mcp-Session-Id"):
            headers["Mcp-Session-Id"] = response.headers["Mcp-Session-Id"]
        value = json.load(response)
    if "error" in value:
        raise RuntimeError(value["error"])
    return value["result"]


def call(tool, arguments=None, error=False):
    result = rpc("tools/call", {"name": tool, "arguments": arguments or {}})
    if error:
        check(result.get("isError", False), tool + " refuses the guarded invalid operation")
        return result
    if result.get("isError"):
        raise RuntimeError(result["content"])
    return json.loads(result["content"][0]["text"])


def inventory(query=None):
    args = {"limit": 7}
    if query:
        args["query"] = query
    rows = []
    while True:
        value = call("list_dev_plugins", args)
        rows.extend(value["plugins"])
        if value.get("nextCursor") is None:
            return rows
        args["cursor"] = value["nextCursor"]


def operation(tool, args):
    value = call(tool, args)
    until = time.monotonic() + 45
    while not value.get("terminal"):
        if time.monotonic() > until:
            raise TimeoutError("installer operation remains active: " + value["operationId"])
        time.sleep(0.2)
        value = call("get_plugin_operation", {"operationId": value["operationId"]})
    if value["status"] != "succeeded":
        raise RuntimeError(value)
    return value


def cleanup_developer_settings(path):
    root = "service:Dalamud.Configuration.Internal.DalamudConfiguration"
    value = call("object_access", {"action": "Read", "root": root, "paths": ["DevPluginSettings"]})
    handle = value["DevPluginSettings"]["ref"]
    try:
        described = call("object_access", {"action": "Describe", "root": handle, "filter": "Remove", "limit": 100})
        method = next(m for m in described["members"] if m["name"] == "Remove" and m.get("parameters") == [
            {"name": "key", "type": "System.String", "optional": False}])
        removed = call("object_access", {"action": "Invoke", "root": handle, "member": method["id"], "arguments": [str(path)]})
        if removed:
            saved = call("object_access", {"action": "Describe", "root": root, "filter": "QueueSave", "limit": 100})
            method = next(m for m in saved["members"] if m["name"] == "QueueSave" and m.get("parameters") == [])
            call("object_access", {"action": "Invoke", "root": root, "member": method["id"], "arguments": []})
        report["temporaryDeveloperSettingsRemoved"] = bool(removed)
    finally:
        call("object_access", {"action": "Release", "root": handle})


class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *_):
        pass


scratch = Path(tempfile.mkdtemp(prefix="dalamud-dev-management-live-"))
name = "DalamudMCPDevManagementProbe"
dll = scratch / "bin/Release/net10.0-windows" / (name + ".dll")
held = dll.with_suffix(".held")
owned_registration = False
owned_repository = False
owned_online = False
server = None
repository_url = None
try:
    with opener.open(base + "/health") as response:
        health = json.load(response)
    check(health["buildMvid"].lower() == options.expected_mvid.lower(), "running MCP host matches the tested Debug MVID")
    check(health["mutatingToolsEnabled"], "existing mutation gate is enabled")
    report["buildMvid"] = health["buildMvid"]
    initialized = rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
        "clientInfo": {"name": "developer-management-live-test", "version": "1"}})
    report["loggedIn"] = call("get_client_state")["loggedIn"]
    call("get_local_player")
    catalog, page_sizes, args = [], [], {}
    while True:
        page = rpc("tools/list", args)
        catalog.extend(page["tools"])
        page_sizes.append(len(page["tools"]))
        if not page.get("nextCursor"):
            break
        args = {"cursor": page["nextCursor"]}
    report["catalogPages"] = page_sizes
    names = {tool["name"] for tool in catalog}
    check(len(catalog) == 189 and {"list_dev_plugins", "set_dev_plugin_settings", "unload_dev_plugin", "remove_dev_plugin", "disable_repository_plugin"} <= names,
        "complete 189-tool running catalog includes developer management and online disable tools")
    check(next(t for t in catalog if t["name"] == "list_dev_plugins")["annotations"]["readOnlyHint"], "development inventory is advertised as read-only")
    check("list_dev_plugins" in initialized["instructions"] and "remove_dev_plugin" in initialized["instructions"], "running instructions describe developer management")
    before = inventory()
    check(not any(row.get("internalName") == name for row in before), "temporary probe does not already exist")
    check(call("plugin_list", {"query": name})["total"] == 0, "no online probe installation exists before the test")
    for tool in ("set_dev_plugin_settings", "unload_dev_plugin", "remove_dev_plugin"):
        host = next(row for row in before if row.get("internalName") == "DalamudMCP" and row["isLoaded"])
        args = {"filePath": host["filePath"]}
        if tool == "set_dev_plugin_settings":
            args["autoReload"] = host["autoReload"]
        call(tool, args, error=True)
    sdk = Path(os.environ["APPDATA"]) / "XIVLauncherCN/addon/Hooks/dev/Dalamud.dll"
    (scratch / "Probe.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><AssemblyName>{name}</AssemblyName>
<AssemblyVersion>1.0.0.0</AssemblyVersion><Version>1.0.0.0</Version><PlatformTarget>x64</PlatformTarget></PropertyGroup>
<ItemGroup><Reference Include="Dalamud"><HintPath>{sdk}</HintPath><Private>false</Private></Reference></ItemGroup>
</Project>""", encoding="utf-8")
    (scratch / "Plugin.cs").write_text("using Dalamud.Plugin; public sealed class Plugin : IDalamudPlugin { public void Dispose() { } }", encoding="utf-8")
    subprocess.run(["dotnet", "build", str(scratch / "Probe.csproj"), "-c", "Release", "--nologo", "-v", "q"], check=True)
    manifest = dll.with_suffix(".json")
    manifest.write_text(json.dumps({"Author": "NiGuangOwO", "Name": name, "InternalName": name,
        "AssemblyVersion": "1.0.0.0", "ApplicableVersion": "any", "DalamudApiLevel": 15,
        "LoadRequiredState": 2, "LoadSync": False, "CanUnloadAsync": True,
        "Description": "Inert temporary developer-management verification fixture."}), encoding="utf-8")
    report["probeDllPath"] = str(dll)
    online_manifest = json.loads(manifest.read_text(encoding="utf-8"))
    with zipfile.ZipFile(scratch / "latest.zip", "w", zipfile.ZIP_DEFLATED) as archive:
        archive.write(dll, name + ".dll")
        archive.write(manifest, name + ".json")
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(QuietHandler, directory=str(scratch)))
    repository_url = f"http://127.0.0.1:{server.server_port}/pluginmaster.json"
    package_url = f"http://127.0.0.1:{server.server_port}/latest.zip"
    online_manifest.update({"DownloadLinkInstall": package_url, "DownloadLinkUpdate": package_url})
    (scratch / "pluginmaster.json").write_text(json.dumps([online_manifest]), encoding="utf-8")
    threading.Thread(target=server.serve_forever, daemon=True).start()
    owned_repository = True
    operation("add_plugin_repository", {"url": repository_url})
    owned_online = True
    installed = operation("install_plugin", {"internalName": name, "expectedVersion": "1.0.0.0", "repositoryUrl": repository_url})
    check(installed["result"]["isLoaded"], "inert online probe installation starts enabled")
    call("load_dev_plugin", {"filePath": str(dll)}, error=True)
    check(not inventory(name), "enabled same-name online plugin prevents local registration")
    disabled = operation("disable_repository_plugin", {"internalName": name, "expectedVersion": "1.0.0.0"})
    check(disabled["result"]["stateVerified"] and disabled["result"]["installed"] and not disabled["result"]["enabled"],
        "typed online disable verifies the exact installed version and profile-disabled state")
    until = time.monotonic() + 15
    while True:
        online = [p for p in call("plugin_list", {"query": name})["plugins"] if p["internalName"] == name and not p["isDev"]]
        if len(online) == 1 and not online[0]["isLoaded"]:
            break
        if time.monotonic() > until:
            raise TimeoutError("online disable did not become observable")
        time.sleep(0.1)
    check(not online[0]["isLoaded"], "runtime-discovered online plugin is disabled and verified unloaded before local loading")
    owned_registration = True
    loaded = call("load_dev_plugin", {"filePath": str(dll), "autoReload": False})
    check(loaded["buildVerified"] and not loaded["autoReload"], "temporary DLL registers and loads the verified build")
    both = [p for p in call("plugin_list", {"query": name})["plugins"] if p["internalName"] == name]
    check(len(both) == 2 and any(not p["isDev"] and not p["isLoaded"] for p in both) and any(p["isDev"] and p["isLoaded"] for p in both),
        "local version runs while the same-name online installation remains installed and disabled")
    current = inventory(name)
    check(len(current) == 1 and current[0]["registered"] and current[0]["loadLocationConfigured"], "temporary developer plugin is discoverable by name and exact DLL path")
    row = current[0]
    settings = {"startOnBoot": not row["startOnBoot"], "autoReload": True, "notifyForErrors": not row["notifyForErrors"]}
    changed = call("set_dev_plugin_settings", {"filePath": str(dll), **settings})
    check(changed["stateVerified"] and changed["saveQueued"] and all(changed[key] == value for key, value in settings.items()), "all three developer flags update through Dalamud and verify")
    unchanged = call("set_dev_plugin_settings", {"filePath": str(dll), **settings})
    check(unchanged["stateVerified"] and not unchanged["saveQueued"], "repeating identical settings queues no further save")
    call("set_dev_plugin_settings", {"filePath": str(dll), "autoReload": "false"}, error=True)
    stopped = call("unload_dev_plugin", {"filePath": str(dll)})
    check(stopped["status"] == "completed" and stopped["stateVerified"] and stopped["registered"] and stopped["autoReload"], "unload verifies the unloaded state and retains the watcher and registration")
    state = call("get_dev_plugin_state", {"filePath": str(dll)})
    check(not state["isLoaded"] and state["autoReload"] and not state["buildMatches"], "separate state inspection confirms unload")
    again = call("load_dev_plugin", {"filePath": str(dll)})
    check(again["buildVerified"] and again["autoReload"], "existing unloaded registration can load again without changing its watcher")
    original = hashlib.sha256(dll.read_bytes()).hexdigest()
    original_manifest = manifest.read_bytes()
    removed = call("remove_dev_plugin", {"filePath": str(dll)})
    check(removed["status"] == "completed" and removed["stateVerified"] and removed["removedFromManager"] and removed["loadLocationRemoved"], "loaded probe unloads, disposes and loses its exact DLL registration")
    owned_registration = False
    check(not inventory(name) and not call("get_dev_plugin_state", {"filePath": str(dll)})["registered"], "fresh inventory and state confirm removal")
    check(hashlib.sha256(dll.read_bytes()).hexdigest() == original and manifest.read_bytes() == original_manifest and not removed["filesDeleted"], "removal preserves DLL bytes and manifest")
    owned_registration = True
    call("load_dev_plugin", {"filePath": str(dll), "autoReload": False})
    call("unload_dev_plugin", {"filePath": str(dll)})
    dll.rename(held)
    check(not inventory(name)[0]["fileExists"], "missing DLL remains discoverable after unload")
    missing = call("remove_dev_plugin", {"filePath": str(dll)})
    check(missing["stateVerified"] and missing["loadLocationRemoved"] and not inventory(name), "missing DLL registration can be removed without reading assembly metadata")
    owned_registration = False
    held.rename(dll)
    after = inventory()
    keys = ("filePath", "registered", "isLoaded", "state", "startOnBoot", "autoReload", "notifyForErrors", "loadLocationConfigured", "loadLocationEnabled")
    snapshot = lambda rows: sorted([json.dumps({key: row.get(key) for key in keys}, sort_keys=True) for row in rows])
    check(snapshot(before) == snapshot(after), "all pre-existing developer plugin states and settings remain unchanged")
    report["existingDeveloperEntries"] = len(before)
    report["probeRemoved"] = True
except Exception as error:
    report["error"] = str(error)
    raise
finally:
    if held.exists() and not dll.exists():
        held.rename(dll)
    if owned_registration:
        try:
            if inventory(name):
                cleanup = call("remove_dev_plugin", {"filePath": str(dll), "timeoutMs": 30000})
                if cleanup.get("stateVerified"):
                    owned_registration = False
            else:
                owned_registration = False
        except Exception as error:
            report["cleanupError"] = str(error)
    report["cleanupVerified"] = not owned_registration
    if not owned_registration:
        try:
            cleanup_developer_settings(dll)
        except Exception as error:
            report["settingsCleanupError"] = str(error)
    if owned_online and not owned_registration:
        try:
            operation("uninstall_plugin", {"internalName": name, "expectedVersion": "1.0.0.0"})
            owned_online = False
        except Exception as error:
            report["onlineCleanupError"] = str(error)
    if owned_repository and not owned_online:
        try:
            operation("remove_plugin_repository", {"url": repository_url})
            owned_repository = False
        except Exception as error:
            report["repositoryCleanupError"] = str(error)
    if server:
        server.shutdown()
        server.server_close()
    report["onlineAndRepositoryCleanupVerified"] = not owned_online and not owned_repository
    if not owned_registration:
        if scratch.parent.resolve() != Path(tempfile.gettempdir()).resolve() or not scratch.name.startswith("dalamud-dev-management-live-"):
            raise RuntimeError("unexpected temporary cleanup path")
        shutil.rmtree(scratch)
    else:
        report["retainedTemporaryDirectory"] = str(scratch)
    output = Path(options.report)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
