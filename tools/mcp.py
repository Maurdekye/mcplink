#!/usr/bin/env python3
"""Call McpLink's HTTP endpoint directly, bypassing the always-up proxy's cached tool list.

Needed because a tool ADDED to the mod is invisible to an already-connected MCP client until that
client re-initializes -- the proxy outliving the game is exactly what makes its tool list outlive
the mod's. The endpoint below is the same dispatcher the proxy talks to, so this is not a
workaround around the code under test; it is the same code path with one less cache in front.

usage: mcp.py <tool> '<json args>'
"""
import json
import re
import sys
import urllib.request

URL = "http://localhost:7357/mcp"


def call(tool, args):
    payload = json.dumps({
        "jsonrpc": "2.0", "id": 1,
        "method": "tools/call",
        "params": {"name": tool, "arguments": args},
    }).encode()
    req = urllib.request.Request(URL, data=payload, headers={
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
    })
    raw = urllib.request.urlopen(req, timeout=600).read().decode("utf-8", "replace")
    m = re.search(r"^data: (.*)$", raw, re.M)
    doc = json.loads(m.group(1) if m else raw)
    if "error" in doc:
        return {"_rpcError": doc["error"]}
    result = doc.get("result", {})
    # MCP wraps tool output as content[].text; unwrap the JSON inside
    for item in result.get("content", []):
        if item.get("type") == "text":
            try:
                return json.loads(item["text"])
            except json.JSONDecodeError:
                return {"_text": item["text"], "_isError": result.get("isError", False)}
    return result


if __name__ == "__main__":
    out = call(sys.argv[1], json.loads(sys.argv[2]) if len(sys.argv) > 2 else {})
    print(json.dumps(out, indent=2)[:12000])
