#!/usr/bin/env python3
"""mcplink_proxy — always-up stdio MCP shim in front of McpLink's in-game HTTP server.

Claude Code spawns this per session (stdio transport), so the `mcplink` MCP server
is always "connected" even when Resonite is closed:

  - initialize / ping    -> answered locally, always succeed
  - tools/list           -> forwarded to http://localhost:7357/mcp when the game is up
                            (and the result cached to tools_cache.json); served from
                            the cache when the game is down
  - tools/call           -> forwarded when the game is up; a clear isError result
                            ("Resonite is not running") when it is down
  - stale backend session (game restarted mid-Claude-session) -> re-initializes
                            against the new game instance and retries once

No third-party dependencies; stdout carries only newline-delimited JSON-RPC,
all logging goes to stderr.
"""

import http.client
import json
import os
import sys
import time

HOST = os.environ.get("MCPLINK_HOST", "localhost")
PORT = int(os.environ.get("MCPLINK_PORT", "7357"))
PATH = os.environ.get("MCPLINK_PATH", "/mcp")
CONNECT_TIMEOUT = float(os.environ.get("MCPLINK_CONNECT_TIMEOUT", "3"))
# McpLink tools can legitimately run long (whole-world scans, eval, renders).
READ_TIMEOUT = float(os.environ.get("MCPLINK_READ_TIMEOUT", "600"))
PROTOCOL_VERSION = "2025-06-18"
CACHE_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "tools_cache.json")

DOWN_MESSAGE = (
    "Resonite is not running — McpLink's endpoint (http://{host}:{port}{path}) refused the "
    "connection. Launch the game (with the McpLink mod loaded), then retry this call. "
    "No session restart is needed; this proxy reconnects automatically."
).format(host=HOST, port=PORT, path=PATH)


def log(msg):
    sys.stderr.write("[mcplink-proxy %s] %s\n" % (time.strftime("%H:%M:%S"), msg))
    sys.stderr.flush()


class BackendDown(Exception):
    pass


# ---------------------------------------------------------------- backend HTTP

_session_id = None  # Mcp-Session-Id of our session with the in-game server, if any


def backend_post(payload, session_id=None):
    """POST one JSON-RPC message to the game. Returns (message, new_session_id, status).

    `message` is the JSON-RPC response matching payload's id (parsed from either a
    plain JSON body or an SSE stream), or None for notifications / empty bodies.
    Raises BackendDown on any socket-level failure.
    """
    try:
        conn = http.client.HTTPConnection(HOST, PORT, timeout=CONNECT_TIMEOUT)
        conn.connect()
    except OSError as e:
        raise BackendDown("connect failed: %s" % e)
    try:
        # Short timeout to *detect* a dead game, long timeout for real tool work.
        conn.sock.settimeout(READ_TIMEOUT)
        headers = {
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
            "MCP-Protocol-Version": PROTOCOL_VERSION,
        }
        if session_id:
            headers["Mcp-Session-Id"] = session_id
        conn.request("POST", PATH, json.dumps(payload), headers)
        resp = conn.getresponse()
        status = resp.status
        new_session = resp.getheader("Mcp-Session-Id")
        ctype = resp.getheader("Content-Type") or ""

        if status == 202 or "id" not in payload:
            resp.read()
            return None, new_session, status

        if "text/event-stream" in ctype:
            message = _read_sse_until_id(resp, payload["id"])
        else:
            raw = resp.read()
            message = json.loads(raw) if raw.strip() else None
        return message, new_session, status
    except OSError as e:
        raise BackendDown("request failed: %s" % e)
    finally:
        conn.close()


def _read_sse_until_id(resp, want_id):
    """Parse an SSE stream, returning the JSON-RPC message whose id == want_id."""
    data_lines = []
    while True:
        line = resp.readline()
        if not line:
            return None  # stream ended without a matching response
        line = line.decode("utf-8", "replace").rstrip("\r\n")
        if line.startswith("data:"):
            data_lines.append(line[5:].lstrip())
        elif line == "" and data_lines:
            try:
                msg = json.loads("\n".join(data_lines))
            except ValueError:
                msg = None
            data_lines = []
            if isinstance(msg, dict) and msg.get("id") == want_id:
                return msg


def ensure_backend_session():
    global _session_id
    if _session_id is not None:
        return
    init = {
        "jsonrpc": "2.0",
        "id": "proxy-init",
        "method": "initialize",
        "params": {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "mcplink-proxy", "version": "1.0.0"},
        },
    }
    msg, sid, status = backend_post(init)
    if status >= 400 or msg is None or "error" in msg:
        raise BackendDown("backend initialize failed (HTTP %s): %r" % (status, msg))
    _session_id = sid or ""  # "" = live session that doesn't use session ids
    try:
        backend_post({"jsonrpc": "2.0", "method": "notifications/initialized"},
                     session_id=sid)
    except BackendDown:
        pass  # some servers don't care; the session is already usable
    log("backend session established%s" % (" (id %s)" % sid if sid else ""))


def forward(method, params, req_id):
    """Forward one request to the game, transparently recovering a stale session."""
    global _session_id
    for attempt in (1, 2):
        ensure_backend_session()
        msg, _sid, status = backend_post(
            {"jsonrpc": "2.0", "id": req_id, "method": method, "params": params},
            session_id=_session_id or None)
        if status in (400, 404) and attempt == 1:
            # Game restarted since we initialized: our session id is gone.
            log("backend session stale (HTTP %d) - re-initializing" % status)
            _session_id = None
            continue
        if msg is None:
            raise BackendDown("no response for %s (HTTP %s)" % (method, status))
        return msg
    raise BackendDown("session re-init retry exhausted")


# ---------------------------------------------------------------- tools cache

def load_cached_tools():
    try:
        with open(CACHE_FILE, "r", encoding="utf-8") as f:
            return json.load(f)["result"]
    except (OSError, ValueError, KeyError):
        return None


def save_cached_tools(result):
    try:
        with open(CACHE_FILE, "w", encoding="utf-8") as f:
            json.dump({"cached_at": time.strftime("%Y-%m-%d %H:%M:%S"),
                       "result": result}, f, indent=1)
    except OSError as e:
        log("could not write tools cache: %s" % e)


# ---------------------------------------------------------------- stdio front

def send(obj):
    sys.stdout.write(json.dumps(obj, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def reply(req_id, result=None, error=None):
    msg = {"jsonrpc": "2.0", "id": req_id}
    if error is not None:
        msg["error"] = error
    else:
        msg["result"] = result
    send(msg)


def handle_tools_list(req_id, params):
    try:
        msg = forward("tools/list", params or {}, req_id)
        if "result" in msg:
            save_cached_tools(msg["result"])
        send(msg)
    except BackendDown as e:
        cached = load_cached_tools()
        if cached is not None:
            log("game down (%s) - serving %d cached tools" %
                (e, len(cached.get("tools", []))))
            reply(req_id, result=cached)
        else:
            log("game down and no tools cache yet - serving empty tool list")
            reply(req_id, result={"tools": []})


def handle_tools_call(req_id, params):
    try:
        send(forward("tools/call", params or {}, req_id))
    except BackendDown as e:
        log("tools/call while game down: %s" % e)
        reply(req_id, result={
            "content": [{"type": "text", "text": DOWN_MESSAGE}],
            "isError": True,
        })


def main():
    global _session_id
    # Windows Python defaults std streams to the ANSI codepage (cp1252): UTF-8 JSON-RPC from
    # the MCP client decoded as cp1252 mojibake'd every non-ASCII tool argument (em-dash →
    # "â€") before it ever reached the game. Same fix as orgtree's externtool.
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8", errors="replace")
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    log("started (backend http://%s:%s%s, cache %s)" % (HOST, PORT, PATH, CACHE_FILE))
    for raw in sys.stdin:
        raw = raw.strip()
        if not raw:
            continue
        try:
            msg = json.loads(raw)
        except ValueError:
            log("ignoring non-JSON line on stdin")
            continue
        method = msg.get("method")
        req_id = msg.get("id")
        params = msg.get("params") or {}

        if method == "initialize":
            reply(req_id, result={
                "protocolVersion": params.get("protocolVersion", PROTOCOL_VERSION),
                "capabilities": {"tools": {"listChanged": False}},
                "serverInfo": {"name": "mcplink", "version": "1.0.0-proxy"},
                "instructions": (
                    "McpLink (in-process Resonite MCP server) behind an always-up "
                    "proxy. Tool calls only succeed while Resonite is running; when "
                    "the game is closed they return an isError result saying so."
                ),
            })
        elif req_id is None:
            pass  # notification (initialized, cancelled, ...) — nothing to do
        elif method == "ping":
            reply(req_id, result={})
        elif method == "tools/list":
            handle_tools_list(req_id, params)
        elif method == "tools/call":
            handle_tools_call(req_id, params)
        else:
            reply(req_id, error={"code": -32601,
                                 "message": "Method not supported by mcplink proxy: %s" % method})
    log("stdin closed - exiting")


if __name__ == "__main__":
    main()
