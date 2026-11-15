"""Bounded live verification; read-only unless --move is explicitly supplied."""
import argparse
from collections import Counter
import json
from pathlib import Path
import urllib.request


class Client:
    def __init__(self, expected_mvid):
        self.headers = {"Accept": "application/json", "Content-Type": "application/json"}
        self.serial = 0
        health = json.load(urllib.request.urlopen("http://127.0.0.1:18777/health", timeout=10))
        if health["buildMvid"] != expected_mvid:
            raise RuntimeError("running host does not match tested build")
        self.health = health
        self.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                               "clientInfo": {"name": "armoury-live-check", "version": "1"}})
        self.rpc("notifications/initialized")
        cursor = None
        self.catalog = {}
        while True:
            page = self.rpc("tools/list", {"cursor": cursor} if cursor else {})
            self.catalog.update((tool["name"], tool) for tool in page["tools"])
            cursor = page.get("nextCursor")
            if not cursor:
                break
        for name in ("get_armoury_inventory", "move_inventory_item_to_armoury"):
            if name not in self.catalog:
                raise RuntimeError("required tool missing: " + name)

    def rpc(self, method, params=None):
        self.serial += 1
        data = {"jsonrpc": "2.0", "method": method}
        if not method.startswith("notifications/"):
            data["id"] = self.serial
        if params is not None:
            data["params"] = params
        request = urllib.request.Request("http://127.0.0.1:18777/mcp", data=json.dumps(data).encode(), headers=self.headers)
        with urllib.request.urlopen(request, timeout=30) as response:
            session = response.headers.get("Mcp-Session-Id")
            if session:
                self.headers["Mcp-Session-Id"] = session
                self.headers["MCP-Protocol-Version"] = "2025-06-18"
            raw = response.read()
        if not raw:
            return None
        result = json.loads(raw)
        if "error" in result:
            raise RuntimeError(result["error"])
        return result.get("result")

    def tool(self, name, args=None):
        result = self.rpc("tools/call", {"name": name, "arguments": args or {}})
        text = next(c["text"] for c in result["content"] if c["type"] == "text")
        if result.get("isError"):
            raise RuntimeError(text)
        return json.loads(text)


def holdings(state):
    result = Counter()
    rows = state["carriedEquipment"] + [i for c in state["categories"] for i in c.get("items", [])]
    for item in rows:
        result[item["itemId"]] += item["quantity"]
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--expected-mvid", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--move", action="store_true")
    parser.add_argument("--limit", type=int, default=1, choices=range(1, 141), metavar="1..140")
    args = parser.parse_args()
    client = Client(args.expected_mvid)
    # Required orientation after each host reconnect.
    state = client.tool("get_client_state")
    player = client.tool("get_local_player")
    if not state["loggedIn"] or not player["playerLoaded"]:
        raise RuntimeError("local character not ready")
    before = client.tool("get_armoury_inventory")
    if not before["complete"]:
        raise RuntimeError("incomplete live storage: " + json.dumps(before["unavailable"]))
    equipment = client.tool("get_equipment")
    report = {"buildMvid": args.expected_mvid, "tools": len(client.catalog), "before": before, "moves": []}
    args.output.parent.mkdir(parents=True, exist_ok=True)

    def save(current):
        report["after"] = current
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    current = before
    save(current)
    print(json.dumps({"equipment": before["carriedEquipmentCount"], "categories": [
        {k: c[k] for k in ("container", "capacity", "used", "free")} for c in before["categories"]]}, ensure_ascii=False), flush=True)
    for _ in range(args.limit):
        candidate = next((i for i in current["carriedEquipment"] if i["canMove"]), None)
        if candidate is None:
            break
        request = {"container": candidate["container"], "slot": candidate["slot"], "expectedItemId": candidate["itemId"]}
        preview = client.tool("move_inventory_item_to_armoury", request)
        if preview["dispatched"] or not preview["dryRun"]:
            raise RuntimeError("dry run unexpectedly dispatched")
        if client.tool("get_armoury_inventory") != current:
            raise RuntimeError("inventory changed during dry run")
        if not args.move:
            report["preview"] = preview
            save(current)
            print(json.dumps({"preview": preview}, ensure_ascii=False), flush=True)
            break
        result = client.tool("move_inventory_item_to_armoury", dict(request, dryRun=False))
        report["moves"].append(result)
        save(current)
        if not result.get("confirmed") or not result.get("success"):
            raise RuntimeError("dispatched move was not confirmed; no retry will be attempted")
        current = client.tool("get_armoury_inventory")
        if not current["complete"] or holdings(current) != holdings(before):
            raise RuntimeError("item conservation or complete storage check failed")
        if current["carriedEquipmentCount"] != before["carriedEquipmentCount"] - len(report["moves"]):
            raise RuntimeError("unexpected carried equipment count")
        if client.tool("get_equipment") != equipment:
            raise RuntimeError("equipped items changed during transfer")
        save(current)
        print(json.dumps({"moved": len(report["moves"]), "name": candidate["name"], "itemId": candidate["itemId"],
                          "remaining": current["carriedEquipmentCount"], "destination": result["destination"]}, ensure_ascii=False), flush=True)
    report["inventoryAfter"] = client.tool("get_inventory")
    report["equipmentUnchanged"] = client.tool("get_equipment") == equipment
    report["itemQuantitiesConserved"] = holdings(current) == holdings(before)
    save(current)
    print(json.dumps({"moved": len(report["moves"]), "remainingEquipment": current["carriedEquipmentCount"],
                      "remainingInventoryItems": report["inventoryAfter"]["count"],
                      "blocked": [i["name"] for i in current["carriedEquipment"] if not i["canMove"]]}, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
