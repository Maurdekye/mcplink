#!/usr/bin/env python3
"""Drive McpLink over plain HTTP -- no MCP client registration needed.

The always-available fallback when the normal route (registering the mcplink MCP server in
your client) isn't working for whatever reason: a tool list cached stale after a mod update,
a client whose config you can't change, a session that started without the server, or no MCP
client at all. This is not a workaround around the real thing -- it POSTs to the same
dispatcher the proxy and every registered client talk to, with one less cache in front.

usage:   mcp.py <tool> ['<json args>']     one call, result JSON on stdout
         mcp.py --list                     live tool names + descriptions from the server
python:  from mcp import call; call("get_slot", {"id": "Root"})

Env overrides: MCPLINK_HOST / MCPLINK_PORT / MCPLINK_PATH (default localhost / 7357 / /mcp).
Needs only Python 3.8+ (stdlib); errors plainly when Resonite isn't running.
"""
import json
import os
import re
import sys
import urllib.request

URL = "http://%s:%s%s" % (os.environ.get("MCPLINK_HOST", "localhost"),
                          os.environ.get("MCPLINK_PORT", "7357"),
                          os.environ.get("MCPLINK_PATH", "/mcp"))


def _post(method, params):
    payload = json.dumps({
        "jsonrpc": "2.0", "id": 1,
        "method": method,
        "params": params,
    }).encode()
    req = urllib.request.Request(URL, data=payload, headers={
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
    })
    try:
        raw = urllib.request.urlopen(req, timeout=600).read().decode("utf-8", "replace")
    except OSError as e:
        return {"_error": "McpLink unreachable at %s (%s) -- is Resonite running with the mod loaded?" % (URL, e)}
    m = re.search(r"^data: (.*)$", raw, re.M)
    doc = json.loads(m.group(1) if m else raw)
    if "error" in doc:
        return {"_rpcError": doc["error"]}
    return doc.get("result", {})


def call(tool, args=None):
    result = _post("tools/call", {"name": tool, "arguments": args or {}})
    if "_error" in result or "_rpcError" in result:
        return result
    # MCP wraps tool output as content[].text; unwrap the JSON inside
    for item in result.get("content", []):
        if item.get("type") == "text":
            try:
                return json.loads(item["text"])
            except json.JSONDecodeError:
                return {"_text": item["text"], "_isError": result.get("isError", False)}
    return result


def list_tools():
    # tools/list paginates (nextCursor); one page would silently look like the whole set
    tools, cursor = [], None
    while True:
        result = _post("tools/list", {"cursor": cursor} if cursor else {})
        if not isinstance(result, dict) or "tools" not in result:
            return result if not tools else tools
        tools.extend(result["tools"])
        cursor = result.get("nextCursor")
        if not cursor:
            return tools


if __name__ == "__main__":
    # tool descriptions contain non-ASCII; a cp1252 console/pipe would crash the print
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    if len(sys.argv) > 1 and sys.argv[1] == "--list":
        tools = list_tools()
        if isinstance(tools, list):
            for t in tools:
                print("%-24s %s" % (t.get("name", "?"), (t.get("description") or "").split("\n")[0][:90]))
        else:
            print(json.dumps(tools, indent=2))
    elif len(sys.argv) > 1:
        out = call(sys.argv[1], json.loads(sys.argv[2]) if len(sys.argv) > 2 else {})
        print(json.dumps(out, indent=2)[:12000])
    else:
        print(__doc__.strip())
