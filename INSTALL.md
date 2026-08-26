# McpLink — Setup Guide, from zero

This is the long-form walkthrough. If you just want the short version, the
[README's section 1](README.md#1-get-the-mod-set-up) covers a working setup in five steps;
everything there is repeated here with more detail, plus configuration, troubleshooting,
updating, and uninstalling.

McpLink is a ResoniteModLoader mod that runs an **MCP (Model Context Protocol) server inside
the Resonite process**, giving an AI agent (Claude Code, or any MCP client) deep read/write
access to your live worlds — 97 tools.

> ⚠ **Security, up front.** The endpoint binds to **localhost only**, but anything that can
> reach it has, in effect, the power of the game process itself (arbitrary method invocation ≈
> arbitrary code in-game). Don't expose the port beyond localhost, and set `allowWrites: false`
> in the mod config if you want a read-only agent.

## 1. What you need

| Requirement | Needed for | Notes |
|---|---|---|
| **Resonite** (Windows) | everything | any install location; the Steam default is assumed by scripts and can be overridden |
| **[ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader)** | everything | the mod loader; McpLink does nothing without it |
| **Python 3.8+** on PATH | the recommended proxy connection (§4) only | the proxy is a single dependency-free script; direct HTTP needs no Python |
| An **MCP client** | talking to the server | examples use Claude Code; any streamable-HTTP or stdio MCP client works |
| **[claude-orgtree](https://github.com/Maurdekye/claude-orgtree)** | *optional* — the in-world agent panels only | see [README §2](README.md#2-connecting-mcplink-to-orgtree); everything else works without it |
| .NET 10 SDK + [ResoniteHotReloadLib](https://github.com/Nytra/ResoniteHotReloadLib) | *building from source only* | release-zip users need neither; see [README "Building from source"](README.md#building-from-source) |

**Install ResoniteModLoader first** if you haven't — follow
[its install guide](https://github.com/resonite-modding-group/ResoniteModLoader#installation)
and verify a modded launch works before adding McpLink.

## 2. Install the mod

Download from the [Releases page](https://github.com/Maurdekye/mcplink/releases). The
`McpLink-x.y.z.zip` bundle mirrors your Resonite install. **With the game closed:**

1. Copy **`rml_mods\McpLink.dll`** into your Resonite `rml_mods` folder
   (e.g. `C:\Program Files (x86)\Steam\steamapps\common\Resonite\rml_mods`).
2. *(Optional — enables the `eval` C# scripting tool)* copy the zip's
   **`rml_mods\McpLink_libs\`** folder in beside it, so you end up with
   `rml_mods\McpLink_libs\*.dll` (McpLinkEval + the Roslyn compiler, ~10 MB). RML does not load
   these as mods — McpLink lazy-loads them on the first `eval` call. Every other tool works
   without them.

Scripted alternative from a clone of this repo:
`powershell -File tools\install.ps1` (add `-ResonitePath "D:\path\to\Resonite"` for non-Steam
installs). It checks RML is present, downloads the latest release, refuses loudly if the game
is running (the DLL is file-locked then), and hash-verifies the copy.

## 3. Verify the mod loads

Start Resonite and check the newest log in `Logs\` (in the install dir):

```
[McpLink] Tool registry built: 97 tools.
[McpLink] MCP server listening on http://localhost:7357/mcp
```

Both lines present = the server is up. Neither present = RML didn't load the mod
(re-check §1's RML install).

## 4. Connect your MCP client

### Recommended: the always-up proxy (Claude Code)

McpLink lives inside the game process, so its HTTP endpoint only exists while Resonite runs —
and an MCP server that is down when a session starts contributes **zero tools** to it. The
bundled proxy fixes this: a small dependency-free Python script your client spawns over stdio,
so `mcplink` is *always* connected. Calls made while the game is closed return a clear
"Resonite is not running" error, and the proxy reconnects by itself when the game (re)starts —
even mid-session.

1. Copy the zip's **`proxy\`** folder somewhere permanent (anywhere; the proxy writes a small
   `tools_cache.json` next to itself).
2. Register it:

   ```
   claude mcp add mcplink -- python "C:\path\to\proxy\mcplink_proxy.py"
   ```

3. `claude mcp list` should show `mcplink: ✓ Connected` — **even with the game closed**.

*One-time bootstrap:* the tool cache starts empty, so run one Claude session (or `/mcp` →
reconnect) while the game is running; after that the tools are present in every session
regardless of game state.

Environment overrides, if you need them: `MCPLINK_HOST` / `MCPLINK_PORT` / `MCPLINK_PATH`
(default `localhost` / `7357` / `/mcp`), `MCPLINK_CONNECT_TIMEOUT` (default 3 s — how quickly a
closed game is detected), `MCPLINK_READ_TIMEOUT` (default 600 s — ceiling for long tool runs
such as world scans and renders).

### Alternative: direct HTTP

Works with any client that speaks MCP streamable HTTP, with the caveat that the server only
connects if Resonite is already running when the client session starts:

```
claude mcp add --transport http mcplink http://localhost:7357/mcp
```

### Other MCP clients

Nothing in McpLink is Claude-specific. Configure your client with either transport:

- **HTTP**: endpoint `http://localhost:7357/mcp` (streamable HTTP).
- **stdio**: command `python`, args `["C:\\path\\to\\proxy\\mcplink_proxy.py"]` — the usual
  `mcpServers` JSON shape in most clients' config files.

### For agents: the no-registration fallback

If MCP registration isn't working for whatever reason — a client whose config can't be
changed, tool schemas cached stale after a mod update, a session that started without the
server — an agent with shell access can drive McpLink directly over plain HTTP with the
bundled helper (`tools\mcp.py` in this repo, attached standalone to each Release, and inside
release zips going forward; Python 3.8+, stdlib only):

```
python tools\mcp.py --list                        # live tool names from the server
python tools\mcp.py get_slot "{\"id\": \"Root\"}" # one call, JSON result on stdout
```

It talks to the exact same dispatcher as a registered client (`from mcp import call` works
in scripts too), and errors plainly when Resonite isn't running.

## 5. Teach the agent how to use it

The tools are self-describing, but the *craft* — how to read big ProtoFlux graphs cheaply,
which engine footguns silently no-op, when to checkpoint before mutating — is documented in
**[CLAUDE-MCPLINK.md](CLAUDE-MCPLINK.md)** (also bundled in the release zip). For Claude Code:

1. Copy `CLAUDE-MCPLINK.md` into the project folder where you run Claude Code.
2. Add an import line to that project's `CLAUDE.md` (create it if needed):

   ```markdown
   # Resonite / McpLink
   @CLAUDE-MCPLINK.md
   ```

For other agents, provide the file as standing context by whatever mechanism your client uses.

## 6. Configuration

Via ResoniteModLoader's config file **`rml_config\McpLink.json`** (created on first modded
launch), or a settings-UI mod. Keys and defaults are in the
[README's Configuration table](README.md#configuration).

Two rules that will save you confusion, both consequences of how RML persists config:

- **Edit the file only while the game is closed.** RML rewrites it at every game shutdown from
  the *running* mod's known keys — a key you hand-add mid-session is silently erased on quit.
- **Leave the file's `"version"` field at `"1.0.0"`.** It's the config-format version, not the
  mod version; changing it gets the whole file rejected to a `.bak`.

## 7. Updating

From a clone: `powershell -File tools\update.ps1`. It asks the *running server* its version
over MCP `initialize` (file timestamps prove nothing — builds aren't byte-reproducible),
downloads the latest release if newer, refuses plainly while the game holds the file lock,
hash-verifies the swap, and only updates the eval companion where you'd installed it.

By hand: game closed, overwrite `rml_mods\McpLink.dll` (+ the `McpLink_libs` contents if you
use `eval`).

Either way, afterwards **restart your MCP client / Claude session too** — clients cache tool
schemas per session and would keep showing the previous version's tools until they reconnect.
To confirm what's actually running, call the `session_info` tool: it reports the version, the
running build's MVID, and whether the on-disk copies match it (`deployConsistent`).

## 8. Troubleshooting

- **`mcplink` shows "Failed to connect" (direct HTTP)** — Resonite wasn't running when the
  session started. Use the proxy (§4), or start the game and reconnect via `/mcp`.
- **Proxy connected but zero tools** — the one-time cache bootstrap hasn't happened; run one
  session (or `/mcp` reconnect) while the game is up.
- **Tools look stale after an update** — same cache: restart the client session so schemas
  refresh.
- **Port already in use** — change `port` in the mod config (game closed!), then re-register
  the HTTP URL or set `MCPLINK_PORT` for the proxy.
- **`eval` fails with "companion not found"** — the `McpLink_libs` folder (§2 step 2) isn't
  installed.
- **`eval` fails with an `InvalidCastException` mentioning `EvalGlobals`** — known limitation
  after a `hot_reload` (developer feature): restart Resonite; all other tools are unaffected.
- **A tool call froze the game** — synchronous work (e.g. an `eval` infinite loop) runs on the
  world update thread by design; there is no watchdog for `eval`. See the safety notes in
  `CLAUDE-MCPLINK.md`.
- **A `McpLink.dll.PENDING` file appeared in rml_mods** — a *developer build* tried to deploy
  while the game was running and was blocked by the file lock; the note says exactly that. A
  successful install/update (scripts or a rebuild with the game closed) removes it.
- **A config key you added vanished** — you edited `McpLink.json` while the game was running;
  see §6.
- **MCP registration won't work at all** — agents (or you) can bypass registration entirely:
  `python tools\mcp.py <tool> '<json args>'` speaks plain HTTP to the same server (see §4,
  "the no-registration fallback").
- **The Prompt Agent menu entry is missing / `open_prompt_wizard` says "not set up"** — those
  surfaces need the optional orgtree companion:
  [README §2](README.md#2-connecting-mcplink-to-orgtree).

## 9. Uninstall

Game closed: delete `rml_mods\McpLink.dll`, the `rml_mods\McpLink_libs\` folder if present,
and (optionally) `rml_config\McpLink.json`. Deregister the client side with
`claude mcp remove mcplink` (or your client's equivalent). The proxy folder you copied in §4
is self-contained — delete it too if you're done with it.

---

MIT licensed — see [LICENSE](LICENSE). Building from source, the offline test suite, and the
developer iteration loop: [README "Building from source"](README.md#building-from-source).
