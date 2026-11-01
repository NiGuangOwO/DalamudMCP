"""Passive live zone IPC verification. No gameplay input, login, movement or packet injection."""
import argparse
import collections
import hashlib
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
report = {"checks": [], "captures": [], "gameplayInputUsed": False, "rawPayloadsSaved": False}
active_session = None


def check(ok, label):
    if not ok:
        raise AssertionError(label)
    report["checks"].append(label)
    print("PASS " + label, flush=True)


def rpc(method, params):
    request = urllib.request.Request(base + "/mcp", data=json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode(), headers=headers)
    with urllib.request.urlopen(request, timeout=15) as response:
        if response.headers.get("Mcp-Session-Id"):
            headers["Mcp-Session-Id"] = response.headers["Mcp-Session-Id"]
        result = json.load(response)
    if "error" in result:
        raise RuntimeError(result["error"])
    return result["result"]


def call(name, arguments=None, expect_error=False):
    result = rpc("tools/call", {"name": name, "arguments": arguments or {}})
    if expect_error:
        check(result.get("isError") is True, name + " deliberately refused")
        return result["content"][0]["text"]
    if result.get("isError"):
        raise RuntimeError(result["content"])
    return json.loads(result["content"][0]["text"])


def start(**arguments):
    global active_session
    state = call("network_capture_start", arguments)
    active_session = state["capture"]["sessionId"]
    return state


def finish(timeout=12):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        state = call("network_capture_status")
        if not state["capture"]["active"]:
            report.setdefault("finishedStates", []).append(state)
            check(not state["hooks"]["sendEnabled"] and not state["hooks"]["receiveEnabled"], "finished capture disables both hooks")
            return state
        time.sleep(0.2)
    raise TimeoutError("capture did not stop")


def metadata(session, **filters):
    rows, after, pages = [], 0, 0
    while True:
        page = call("network_capture_list", {"sessionId": session, "count": 7, "afterId": after, **filters})
        rows += page["packets"]
        pages += 1
        if not page["hasMore"]:
            break
        check(page["lastId"] > after, "metadata cursor advances")
        after = page["lastId"]
    return rows, pages


def record(state, rows, label):
    assert state["capture"]["sessionId"] == active_session
    report["captures"].append({"label": label, "status": state["capture"], "hooks": state["hooks"],
        "directions": dict(collections.Counter(p["direction"] for p in rows)),
        "opcodes": dict(collections.Counter(str(p["opcode"]) for p in rows)),
        "metadata": [{k: v for k, v in p.items() if k != "targetId"} for p in rows]})


try:
    with urllib.request.urlopen(base + "/health", timeout=10) as response:
        health = json.load(response)
    check(health["buildMvid"].lower() == options.expected_mvid.lower(), "running build matches tested DLL")
    report["health"] = health
    rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "real-zone-ipc-test", "version": "1"}})
    client_before = call("get_client_state")
    player = call("get_local_player")
    check(client_before["loggedIn"] and player.get("loggedIn", False), "client and local player logged in")
    report["clientBefore"] = client_before
    report["playerLoggedIn"] = True
    tools, page_sizes, args = [], [], {}
    while True:
        page = rpc("tools/list", args)
        tools += page["tools"]
        page_sizes.append(len(page["tools"]))
        if not page.get("nextCursor"):
            break
        args = {"cursor": page["nextCursor"]}
    check(sum(t["name"].startswith("network_capture_") for t in tools) == 7, "complete catalog includes seven network tools")
    report["catalogPages"] = page_sizes
    initial = call("network_capture_status")
    check(initial["hooks"]["supported"] and not initial["hooks"]["sendEnabled"] and not initial["hooks"]["receiveEnabled"], "supported ABI and no existing active network hooks")
    check(initial["capture"] is None or not initial["capture"]["active"], "no other active capture is replaced")

    state = start(direction="both", durationSeconds=10, maxPackets=256, maxBytesPerPacket=64)
    check(state["hooks"]["sendEnabled"] and state["hooks"]["receiveEnabled"], "both real hook directions enabled")
    first_session = active_session
    state = finish()
    rows, pages = metadata(first_session)
    check(len(rows) == state["capture"]["packets"] and len(rows) > 1, "real packets retained and all metadata pages exhausted")
    check([p["packetId"] for p in rows] == list(range(1, len(rows) + 1)), "real packet ids ordered without page gaps")
    check(state["capture"]["invalidPackets"] == 0, "real native buffers pass all length and read checks")
    check(state["capture"]["contentionDroppedPackets"] == 0, "no cache contention drops in passive sample")
    check(state["capture"]["stopReason"] == "timeout", "real capture ends at monotonic deadline")
    record(state, rows, "passive_both_10_seconds")
    report["sendObserved"] = any(p["direction"] == "send" for p in rows)
    report["receiveObserved"] = any(p["direction"] == "receive" for p in rows)
    for direction in ("send", "receive"):
        selected, _ = metadata(first_session, direction=direction)
        check(selected == [p for p in rows if p["direction"] == direction], "metadata direction filter " + direction)
    chosen_opcode = collections.Counter(p["opcode"] for p in rows if p["direction"] == "receive").most_common(1)[0][0]
    selected, _ = metadata(first_session, opcodes=[chosen_opcode])
    check(selected == [p for p in rows if p["opcode"] == chosen_opcode], "metadata numeric opcode filter equals observed records")

    samples = []
    for direction in ("send", "receive"):
        candidates = [p for p in rows if p["direction"] == direction]
        samples += candidates[:2]
    truncated = next((p for p in rows if p["truncated"]), None)
    if truncated and truncated not in samples:
        samples.append(truncated)
    all_bytes = {}
    report["byteChecks"] = []
    for packet in samples:
        args = {"sessionId": first_session, "packetId": packet["packetId"]}
        data, offset = bytearray(), 0
        while True:
            page = call("network_capture_read", {**args, "offset": offset, "count": 17})
            raw = bytes.fromhex(page["hex"])
            check(len(raw) == page["count"] and page["offset"] == offset, "real hex page offset/count consistent")
            check(b"".join(bytes.fromhex(r["hex"]) for r in page["rows"]) == raw, "hex rows reconstruct exact captured bytes")
            data.extend(raw)
            if page["nextOffset"] is None:
                break
            offset = page["nextOffset"]
        check(len(data) == packet["capturedLength"], "hex paging reconstructs retained length")
        opcode_offset = 0 if packet["direction"] == "send" else 2
        decoded = call("network_capture_decode", {**args, "fields": [{"name": "opcode", "type": "u16", "offset": opcode_offset}, {"name": "missingTail", "type": "u32", "offset": packet["capturedLength"]}]})
        check(decoded["fields"][0]["value"] == packet["opcode"] == int.from_bytes(data[opcode_offset:opcode_offset + 2], "little"), "decoded real opcode matches metadata and bytes")
        check(not decoded["fields"][1]["available"], "decoder rejects uncaptured bytes without padding")
        if packet["direction"] == "send":
            check(int.from_bytes(data[8:16], "little") + 16 == packet["declaredLength"], "real send declared extent matches call header size field")
        check(packet["truncated"] == (packet["capturedLength"] < packet["declaredLength"]), "real truncation metadata consistent")
        all_bytes[packet["packetId"]] = bytes(data)
        report["byteChecks"].append({"packetId": packet["packetId"], "direction": packet["direction"], "opcode": packet["opcode"], "sha256": hashlib.sha256(data).hexdigest(), "capturedLength": len(data)})
    left, right = samples[:2]
    a, b = all_bytes[left["packetId"]], all_bytes[right["packetId"]]
    difference = call("network_capture_compare", {"sessionId": first_session, "leftPacketId": left["packetId"], "rightPacketId": right["packetId"], "count": 256})
    expected = [{"offset": i, "left": a[i] if i < len(a) else None, "right": b[i] if i < len(b) else None} for i in range(max(len(a), len(b))) if (a[i] if i < len(a) else None) != (b[i] if i < len(b) else None)]
    check(difference["differences"] == expected, "real packet comparison equals independently computed byte differences")
    report["comparison"] = {"leftPacketId": left["packetId"], "rightPacketId": right["packetId"], "differenceCount": len(expected)}
    if truncated:
        check(truncated["capturedLength"] == 64 and truncated["declaredLength"] > 64, "live oversized message truncates at configured 64 bytes")

    state = start(direction="receive", opcodes=[chosen_opcode], durationSeconds=3, maxPackets=64, maxBytesPerPacket=32)
    check(not state["hooks"]["sendEnabled"] and state["hooks"]["receiveEnabled"], "receive-only capture enables only receive hook")
    stale = call("network_capture_read", {"sessionId": first_session, "packetId": 1}, expect_error=True)
    check("Unknown/replaced" in stale, "old session cannot read a new capture")
    duplicate = call("network_capture_start", {"durationSeconds": 1}, expect_error=True)
    check("capture is active" in duplicate, "active capture cannot be silently replaced")
    state = finish(5)
    filtered_rows, _ = metadata(active_session)
    check(all(p["direction"] == "receive" and p["opcode"] == chosen_opcode for p in filtered_rows), "capture-level direction and opcode allowlist hold")
    check(len(filtered_rows) > 0 and state["capture"]["filteredPackets"] > 0, "real matching packets retained and other opcodes filtered")
    record(state, filtered_rows, "receive_opcode_allowlist")

    if report["sendObserved"]:
        state = start(direction="send", durationSeconds=3, maxPackets=64, maxBytesPerPacket=64)
        check(state["hooks"]["sendEnabled"] and not state["hooks"]["receiveEnabled"], "send-only capture enables only send hook")
        state = finish(5)
        send_rows, _ = metadata(active_session)
        check(all(p["direction"] == "send" for p in send_rows), "send-only capture contains no receives")
        record(state, send_rows, "send_only")

    state = start(direction="receive", durationSeconds=5, maxPackets=1, maxBytesPerPacket=64)
    state = finish(7)
    limit_rows, _ = metadata(active_session)
    check(len(limit_rows) == 1 and state["capture"]["stopReason"] == "packet_limit", "one real packet reaches limit and disables hooks")
    record(state, limit_rows, "packet_limit")

    state = start(direction="receive", durationSeconds=30, maxPackets=128, maxBytesPerPacket=32)
    stopped = call("network_capture_stop", {"sessionId": active_session})
    check(not stopped["capture"]["active"] and stopped["capture"]["stopReason"] == "requested", "explicit real stop ends capture")
    count = stopped["capture"]["packets"]
    time.sleep(0.3)
    twice = call("network_capture_stop", {"sessionId": active_session})
    check(twice["capture"]["packets"] == count and twice["capture"]["stopReason"] == "requested", "repeated stop preserves data and terminal reason")
    stop_rows, _ = metadata(active_session)
    record(twice, stop_rows, "explicit_stop")
    final = call("network_capture_status")
    check(not final["hooks"]["sendEnabled"] and not final["hooks"]["receiveEnabled"], "all passive tests leave hooks disabled")
    report["clientAfter"] = call("get_client_state")
    check(report["clientAfter"]["loggedIn"] and report["clientAfter"]["processId"] == client_before["processId"], "same live client remains logged in after hook start/stop cycles")
    report["completed"] = True
finally:
    if active_session:
        try:
            report["cleanup"] = call("network_capture_stop", {"sessionId": active_session})
        except Exception as error:
            report["cleanupError"] = str(error)
    Path(options.report).parent.mkdir(parents=True, exist_ok=True)
    Path(options.report).write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
