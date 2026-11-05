"""Exercise the public editor's MCP CLI using only the Python standard library."""

import argparse
import json
import os
from pathlib import Path
from queue import Queue
import subprocess
import tempfile
from threading import Thread


MAP_TOOLS = {
    "map_apply", "map_query", "map_summary", "map_open", "map_new", "map_save",
    "map_undo", "map_redo", "map_build", "map_export", "map_playtest", "map_check",
}
LIVE_TOOLS = {"map_propose", "map_proposal", "editor_state", "ui_shot", "ui_input", "ui_tree"}


class Stdio:
    def __init__(self, command):
        self.process = subprocess.Popen(
            command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, encoding="utf-8",
        )
        self.lines = Queue()
        self.id = 0
        Thread(target=self.read, daemon=True).start()

    def read(self):
        for line in self.process.stdout:
            self.lines.put(line)
        self.lines.put("")

    def __enter__(self):
        return self

    def __exit__(self, exc_type, *_):
        self.process.stdin.close()
        try:
            code = self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
            raise
        finally:
            self.process.stdout.close()
        if exc_type is None:
            assert code == 0 and self.lines.get(timeout=10) == "", code

    def send(self, request):
        line = request if isinstance(request, str) else json.dumps(request, ensure_ascii=False)
        self.process.stdin.write(line + "\n")
        self.process.stdin.flush()

    def receive(self):
        self.line = self.lines.get(timeout=120)
        assert self.line, "The process closed before replying"
        return json.loads(self.line)

    def request(self, method, params=None, mcp=True):
        self.id += 1
        request = {"id": self.id, "method": method}
        if mcp:
            request["jsonrpc"] = "2.0"
        if params is not None:
            request["params"] = params
        self.send(request)
        reply = self.receive()
        assert reply["id"] == self.id, reply
        return reply

    def initialize(self):
        result = self.request("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "mcp-checks", "version": "1"},
        })["result"]
        assert result["protocolVersion"] == "2025-06-18" and "tools" in result["capabilities"], result
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def catalog(self):
        tools = self.request("tools/list")["result"]["tools"]
        return {tool["name"]: tool for tool in tools}, len(self.line.encode("utf-8"))

    def tool(self, name, arguments=None, error=False):
        reply = self.request("tools/call", {"name": name, "arguments": arguments or {}})["result"]
        assert reply["isError"] is error, (name, reply)
        body = json.loads(reply["content"][0]["text"])
        if isinstance(body, dict):
            assert reply["structuredContent"] == body, reply
        return body


def mapping_edit(mcp):
    mcp.tool("map_summary", {"classes": 4})
    ray = mcp.tool("map_help", {"kind": "query", "name": "raycast"})["example"]
    find = mcp.tool("map_help", {"kind": "query", "name": "find"})["example"]
    surface, brushes = mcp.tool("map_query", {"queries": [ray, find]})
    assert surface["hit"] and brushes["rows"], (surface, brushes)
    x, y, z = surface["at"]
    block = mcp.tool("map_help", {"kind": "op", "name": "create"})["example"]["ops"][0]
    block.update(id="McpBlock", brush={"min": [x, y, z], "max": [x + 128, y + 128, z + 96]})
    ledge = mcp.tool("map_help", {"kind": "op", "name": "ge"})["example"]["ops"][0]
    ledge.update(id="McpLedge", points=[[x, y, z + 96], [x, y + 128, z + 96]])
    return {"label": "Traversal practice", "ops": [block, ledge]}


def object_count(mcp):
    return mcp.tool("map_query", {"q": "find", "ids": ["McpBlock", "McpLedge"], "fields": ["id"]})["count"]


def check_project(cli, root):
    project = str(root / "McpDemo.osmap")
    with Stdio([*cli, "serve"]) as script:
        reference = script.request("describe", mcp=False)["result"]
        script.request("new", {"id": "McpDemo", "title": "Essai d’été", "path": project}, mcp=False)
        assert "result" in script.request("summary", mcp=False)
        assert script.request("query", {"q": "find", "kind": "brush"}, mcp=False)["result"]["count"] > 0
        script.request("exit", mcp=False)

    with Stdio([*cli, "mcp", project]) as mcp:
        mcp.initialize()
        catalog, size = mcp.catalog()
        assert catalog.keys() == MAP_TOOLS | {"map_help"}, catalog.keys()
        assert catalog["map_help"]["annotations"]["readOnlyHint"]
        assert not catalog["map_apply"]["annotations"]["readOnlyHint"]
        assert mcp.request("ping")["result"] == {}
        assert mcp.request("tools/call", {"name": "unavailable"})["error"]["code"] == -32602
        assert mcp.request("tools/call", {"name": "map_summary", "arguments": []})["error"]["code"] == -32602
        mcp.send("{")
        assert mcp.receive()["error"]["code"] == -32700
        mcp.send("{}")
        assert mcp.receive()["error"]["code"] == -32600

        index = mcp.tool("map_help")
        assert set(index["methods"]) == catalog.keys()
        assert index["ops"] == reference["ops"] and index["queries"] == reference["queries"]
        for kind, names in (("method", index["methods"]), ("op", index["ops"]), ("query", index["queries"])):
            for name in names:
                help_entry = mcp.tool("map_help", {"kind": kind, "name": name})
                assert help_entry["tool"] in catalog and isinstance(help_entry["example"], dict), help_entry
                if kind == "op":
                    op, = help_entry["example"]["ops"]
                    assert op["op"] == name and set(op) - {"op"} <= set(help_entry["arguments"]), (name, op)
                if kind == "query":
                    assert help_entry["example"]["q"] == name
        assert mcp.tool("map_help", {"kind": "query", "name": "unknown"}, error=True)["field"] == "name"

        edit = mapping_edit(mcp)
        mcp.tool("map_apply", {**edit, "dryRun": True})
        assert object_count(mcp) == 0
        failure = mcp.tool("map_apply", {"ops": [*edit["ops"], {"op": "unknown"}]}, error=True)
        assert failure["op"] == 2 and failure["field"] == "op", failure
        assert object_count(mcp) == 0
        mcp.tool("map_apply", edit)
        assert object_count(mcp) == 2
        mcp.tool("map_undo")
        assert object_count(mcp) == 0
        mcp.tool("map_redo")
        assert object_count(mcp) == 2
        assert mcp.tool("map_build", {"out": str(root / "svmap"), "fast": True})["ok"]
        assert "errors" in mcp.tool("map_check", {"limit": 5})
        mcp.tool("map_save")
    print(f"Project workflow passed. MCP discovery: {size} UTF-8 bytes.")
    return size


def check_live(cli, root, mode):
    with Stdio([*cli, "mcp", "--live"]) as mcp:
        mcp.initialize()
        catalog, size = mcp.catalog()
        assert catalog.keys() == MAP_TOOLS | LIVE_TOOLS | {"map_help"}, catalog.keys()
        mcp.tool("editor_state")
        mcp.tool("map_new", {"id": "McpLive", "path": str(root / "McpLive.osmap")})
        edit = mapping_edit(mcp)
        proposal = mcp.tool("map_propose", edit)
        assert proposal["status"] == "pending" and object_count(mcp) == 0

        if mode == "background":
            assert "controls" in mcp.tool("ui_tree", {"limit": 10})
            mcp.tool("ui_shot", {"name": "proposal", "dir": str(root), "lint": False})
            assert any(root.glob("*.png"))
            for button, status in (("Reject", "rejected"), ("Accept", "accepted")):
                mcp.tool("ui_input", {"steps": [{"op": "click", "at": f"text:{button}"}]})
                assert mcp.tool("map_proposal", {"id": proposal["proposal"]})["status"] == status
                if button == "Reject":
                    assert object_count(mcp) == 0
                    proposal = mcp.tool("map_propose", edit)
        else:
            for tool, params in (("ui_tree", {}), ("ui_shot", {}), ("ui_input", {"steps": [{"op": "escape"}]})):
                assert "background" in mcp.tool(tool, params, error=True)["message"]
            mcp.tool("map_apply", edit)

        assert object_count(mcp) == 2 and mcp.tool("editor_state")["dirty"]
        mcp.tool("map_new", {"id": "Guard", "discard": True}, error=True)
        mcp.tool("map_undo")
        assert object_count(mcp) == 0
        mcp.tool("map_apply", edit)
        assert mcp.tool("map_build", {"out": str(root / "live-svmap"), "fast": True})["ok"]
        assert "errors" in mcp.tool("map_check", {"limit": 5})
    print(f"Live {mode} workflow passed. MCP discovery: {size} UTF-8 bytes.")
    return size


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", choices=["background", "headless"], help="Connect to an isolated test editor in OSE_LIVE_DIR")
    parser.add_argument("--private-catalog-bytes", type=int, help="Compare against a measured private build catalog")
    parser.add_argument("cli", nargs="+", help="ose.exe, or dotnet followed by the path to ose.dll")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="ose-mcp-") as folder:
        root = Path(folder)
        if args.live:
            assert os.environ.get("OSE_LIVE_DIR"), "Set OSE_LIVE_DIR to the isolated test editor"
            size = check_live(args.cli, root, args.live)
        else:
            size = check_project(args.cli, root)
        if args.private_catalog_bytes:
            assert size * 2 <= args.private_catalog_bytes
            print(f"Discovery is {1 - size / args.private_catalog_bytes:.1%} smaller than the private build.")


if __name__ == "__main__":
    main()
