"""Verify the fixed-source mapping and names on real packets; no gameplay input or packet injection."""
import argparse
import collections
import json
import time
import urllib.request
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--expected-mvid", required=True)
parser.add_argument("--report", required=True)
options = parser.parse_args()
headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
report = {"checks": [], "gameplayInputUsed": False, "rawPayloadsSaved": False}
session = None


def check(ok, label):
    if not ok:
        raise AssertionError(label)
    report["checks"].append(label)
    print("PASS " + label, flush=True)


def rpc(method, params):
    request = urllib.request.Request("http://127.0.0.1:18777/mcp", data=json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode(), headers=headers)
    with urllib.request.urlopen(request, timeout=20) as response:
        if response.headers.get("Mcp-Session-Id"):
            headers["Mcp-Session-Id"] = response.headers["Mcp-Session-Id"]
        result = json.load(response)
    if "error" in result:
        raise RuntimeError(result["error"])
    return result["result"]


def call(name, args=None):
    result = rpc("tools/call", {"name": name, "arguments": args or {}})
    if result.get("isError"):
        raise RuntimeError(result["content"])
    return json.loads(result["content"][0]["text"])


try:
    with urllib.request.urlopen("http://127.0.0.1:18777/health", timeout=10) as response:
        health = json.load(response)
    check(health["buildMvid"].lower() == options.expected_mvid.lower(), "running opcode build matches tested DLL")
    report["health"] = health
    rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "opcode-mapping-live-test", "version": "1"}})
    client = call("get_client_state")
    player = call("get_local_player")
    report["loggedIn"] = client["loggedIn"] and player.get("loggedIn", False)
    tools, sizes, args = [], [], {}
    while True:
        page = rpc("tools/list", args)
        tools += page["tools"]
        sizes.append(len(page["tools"]))
        if not page.get("nextCursor"):
            break
        args = {"cursor": page["nextCursor"]}
    report["catalogPages"] = sizes
    names = {t["name"] for t in tools}
    check(len(tools) == 157 and {"network_opcode_lookup", "network_opcode_refresh"} <= names, "complete 157-tool catalog includes lookup and refresh")
    for tool in tools:
        if tool["name"] in {"network_opcode_lookup", "network_opcode_refresh"}:
            check(tool["annotations"]["readOnlyHint"] == (tool["name"] == "network_opcode_lookup"), "mutation classification " + tool["name"])
    mapped = call("network_opcode_lookup", {"opcodes": [872]})
    report["lookup872"] = mapped
    labels = {r["direction"]: r["opcodeName"] for r in mapped["entries"]}
    check(labels == {"send": "ClientTrigger", "receive": "FreeCompanyDialog"}, "same CN opcode resolves independently by direction")
    check(mapped["mapping"]["region"] == "CN" and mapped["mapping"]["tableVersion"] == "7.57", "mapping reports CN table and version")
    check(mapped["mapping"]["versionMatch"] == "unverified" and not mapped["mapping"]["semanticsVerified"], "table claim is not promoted to verified client compatibility")
    unknown = call("network_opcode_lookup", {"opcodes": [65535]})
    check(all(r["opcodeName"] is None and r["opcodeMappingStatus"] == "unknown" for r in unknown["entries"]), "unknown numeric opcode retains explicit unknown status")
    global_lookup = call("network_opcode_lookup", {"region": "Global", "direction": "receive", "opcodes": [553]})
    check(global_lookup["entries"][0]["opcodeName"] == "ActorControl", "explicit alternate region lookup uses its own table")
    state = call("network_capture_status")
    check(not state["hooks"]["sendEnabled"] and not state["hooks"]["receiveEnabled"], "lookup/status do not install packet hooks")
    if report["loggedIn"]:
        check(state["capture"] is None or not state["capture"]["active"], "no existing active capture is replaced")
        state = call("network_capture_start", {"direction": "both", "durationSeconds": 5, "maxPackets": 128, "maxBytesPerPacket": 64})
        session = state["capture"]["sessionId"]
        original_mapping = state["capture"]["opcodeMapping"]
    refreshed = call("network_opcode_refresh")
    report["refresh"] = refreshed
    check(refreshed["refreshed"] and refreshed["cacheSaved"] and not refreshed["catalog"]["refreshing"], "real fixed-source refresh validates and saves cache off the game thread")
    check(refreshed["catalog"]["source"] == "https://cdn.jsdelivr.net/gh/karashiiro/FFXIVOpcodes@latest/opcodes.min.json", "refresh reports exact user-supplied URL")
    if session:
        deadline = time.monotonic() + 7
        while time.monotonic() < deadline:
            state = call("network_capture_status")
            if not state["capture"]["active"]:
                break
            time.sleep(0.2)
        check(not state["capture"]["active"] and not state["hooks"]["sendEnabled"] and not state["hooks"]["receiveEnabled"], "mapped capture expires and disables hooks")
        check(state["capture"]["opcodeMapping"] == original_mapping, "refresh preserves active capture's exact mapping snapshot")
        rows, after = [], 0
        while True:
            page = call("network_capture_list", {"sessionId": session, "count": 30, "afterId": after})
            rows += page["packets"]
            if not page["hasMore"]:
                break
            after = page["lastId"]
        check(bool(rows), "real zone packets captured with mapping attached")
        named = [p for p in rows if p["opcodeName"]]
        check(bool(named), "real captured opcodes receive names from CN table")
        for packet in named[:4]:
            lookup = call("network_opcode_lookup", {"region": "CN", "direction": packet["direction"], "opcodes": [packet["opcode"]]})
            if original_mapping["sha256"] == refreshed["catalog"]["sha256"]:
                check(lookup["entries"][0]["opcodeName"] == packet["opcodeName"], "captured name equals direction-scoped lookup")
            args = {"sessionId": session, "packetId": packet["packetId"]}
            read = call("network_capture_read", {**args, "count": 16})
            check(read["opcodeName"] == packet["opcodeName"], "hex read preserves real packet name")
            decoded = call("network_capture_decode", {**args, "fields": [{"name": "opcode", "type": "u16", "offset": 0 if packet["direction"] == "send" else 2}]})
            check(decoded["packet"]["opcodeName"] == packet["opcodeName"] and decoded["fields"][0]["value"] == packet["opcode"], "field decoder preserves name and raw opcode")
        if len(named) >= 2:
            compared = call("network_capture_compare", {"sessionId": session, "leftPacketId": named[0]["packetId"], "rightPacketId": named[1]["packetId"]})
            check(compared["left"]["opcodeName"] == named[0]["opcodeName"] and compared["right"]["opcodeName"] == named[1]["opcodeName"], "real packet comparison carries both labels")
        report["capture"] = state["capture"]
        report["directions"] = dict(collections.Counter(p["direction"] for p in rows))
        report["namedPackets"] = len(named)
        report["unknownPackets"] = sum(p["opcodeMappingStatus"] == "unknown" for p in rows)
        report["metadata"] = [{k: v for k, v in p.items() if k != "targetId"} for p in rows]
    else:
        report["liveGap"] = "Logged out: lookup and HTTP refresh verified; no new real capture."
    report["completed"] = True
finally:
    if session:
        report["cleanup"] = call("network_capture_stop", {"sessionId": session})
    Path(options.report).parent.mkdir(parents=True, exist_ok=True)
    Path(options.report).write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
