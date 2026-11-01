"""Bounded passive check against an already-running verified host; never logs in or starts the game."""
import argparse
import json
import time
import urllib.request
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--expected-mvid", required=True)
parser.add_argument("--report", required=True)
options = parser.parse_args()
base = "http://127.0.0.1:18777"
headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
report = {"checks": [], "liveTrafficVerified": False}


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
        session = response.headers.get("Mcp-Session-Id")
        if session:
            headers["Mcp-Session-Id"] = session
        value = json.load(response)
    if "error" in value:
        raise RuntimeError(value["error"])
    return value["result"]


def call(name, arguments=None, error=False):
    result = rpc("tools/call", {"name": name, "arguments": arguments or {}})
    if error:
        check(result.get("isError") is True, name + " deliberately refused")
        return result["content"][0]["text"]
    if result.get("isError"):
        raise RuntimeError(result["content"])
    return json.loads(result["content"][0]["text"])


session_id = None
try:
    with urllib.request.urlopen(base + "/health", timeout=10) as response:
        health = json.load(response)
    check(health["buildMvid"].lower() == options.expected_mvid.lower(), "running build MVID equals tested DLL")
    report["health"] = health
    rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "network-packet-check", "version": "1"}})
    report["client"] = call("get_client_state")
    # Read readiness without retaining raw character identity in the report.
    player = call("get_local_player")
    report["playerLoggedIn"] = player.get("loggedIn", False)
    tools = []
    page_sizes = []
    args = {}
    while True:
        page = rpc("tools/list", args)
        tools += page["tools"]
        page_sizes.append(len(page["tools"]))
        if not page.get("nextCursor"):
            break
        args = {"cursor": page["nextCursor"]}
    report["catalogPageSizes"] = page_sizes
    names = {t["name"] for t in tools}
    expected = {"network_capture_" + suffix for suffix in ("start", "stop", "status", "list", "read", "decode", "compare")}
    check(len(tools) == 155 and expected <= names, "all 155 tools discovered across every page, including seven network tools")
    for tool in tools:
        if tool["name"] in expected:
            check(tool["annotations"]["readOnlyHint"] == (tool["name"] not in {"network_capture_start", "network_capture_stop"}), "classification " + tool["name"])
    state = call("network_capture_status")
    report["initialStatus"] = state
    check(state["hooks"]["supported"], "running executable and SDK satisfy the inspected ABI checks")
    check(not state["hooks"]["sendEnabled"] and not state["hooks"]["receiveEnabled"], "status query leaves both hooks disabled")
    check(state["capture"] is None, "no capture is started implicitly")
    if not report["client"]["loggedIn"] or not report["playerLoggedIn"]:
        reason = call("network_capture_start", {"durationSeconds": 1}, error=True)
        check("logged-in" in reason, "logged-out start is refused before hook installation")
        report["liveGap"] = "Client logged out; no native hooks installed and no real traffic captured."
    else:
        state = call("network_capture_start", {"durationSeconds": 1, "maxPackets": 8, "maxBytesPerPacket": 64})
        session_id = state["capture"]["sessionId"]
        time.sleep(1.3)
        state = call("network_capture_status")
        check(not state["capture"]["active"] and not state["hooks"]["sendEnabled"] and not state["hooks"]["receiveEnabled"], "bounded capture stops automatically and disables hooks")
        page = call("network_capture_list", {"sessionId": session_id})
        report["capturedPacketMetadata"] = page
        for packet in page["packets"][:2]:
            args = {"sessionId": session_id, "packetId": packet["packetId"]}
            data = call("network_capture_read", {**args, "count": 32})
            check(len(data["hex"]) == data["count"] * 2, "captured hex matches returned byte count")
            opcode_offset = 0 if packet["direction"] == "send" else 2
            decoded = call("network_capture_decode", {**args, "fields": [{"name": "opcode", "type": "u16", "offset": opcode_offset}]})
            check(decoded["fields"][0]["value"] == packet["opcode"], "decoded opcode equals observed metadata")
        report["liveTrafficVerified"] = bool(page["packets"])
        report["liveGap"] = "Passive sample only; no full channel, throughput, server-delivery or other-hook coexistence certification."
    final = call("network_capture_status")
    report["finalStatus"] = final
    check(not final["hooks"]["sendEnabled"] and not final["hooks"]["receiveEnabled"], "verification leaves no network hook enabled")
finally:
    if session_id:
        try:
            call("network_capture_stop", {"sessionId": session_id})
        except Exception as error:
            report["cleanupError"] = str(error)
    Path(options.report).parent.mkdir(parents=True, exist_ok=True)
    Path(options.report).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
