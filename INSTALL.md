# McpLink — Setup Guide

McpLink is a ResoniteModLoader mod that runs an **MCP (Model Context Protocol) server inside
the Resonite process**, giving an AI agent (Claude Code or any MCP client) deep read/write
access to your live worlds: inspect slots and components, read and build ProtoFlux, take
screenshots, spawn objects, run C# against the engine, and more — 87 tools.

> ⚠ **Security, up front.** The endpoint binds to **localhost only**, but anything that can
> reach it has, in effect, the power of the game process itself (arbitrary method invocation ≈
> arbitrary code in-game). Don't expose the port beyond localhost, and set `allowWrites: false`
> in the mod config if you want a read-only agent.

---

## 1. Requirements

- **Resonite** with **[ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader)** installed and working.
- **Python 3.8+** on your PATH — only needed for the recommended proxy connection method (§3).
- An MCP client — the examples below use **Claude Code**.

## 2. Install the mod

The release zip mirrors your Resonite install:

1. Copy **`rml_mods\McpLink.dll`** into your Resonite `rml_mods` folder
   (e.g. `C:\Program Files (x86)\Steam\steamapps\common\Resonite\rml_mods`).
2. *(Optional — enables the `eval` C# scripting tool)* copy the **`rml_mods\McpLink_libs\`**
   folder (McpLinkEval.dll + the Roslyn compiler, ~10 MB) into `rml_mods` as well, so you end
   up with `rml_mods\McpLink_libs\*.dll`. Every other tool works without it. RML does not load
   these as mods — McpLink lazy-loads them on the first `eval` call.
3. Start Resonite. Confirm in the log
   (`Logs\` in the install dir, or the in-game log):

   ```
   [McpLink] Tool registry built: 87 tools.
   [McpLink] MCP server listening on http://localhost:7357/mcp
   ```

## 3. Connect your MCP client

### Recommended: the always-up proxy (Claude Code)

McpLink lives inside the game process, so its HTTP endpoint only exists while Resonite runs —
and an MCP server that is down when a Claude session starts contributes **zero tools** to that
session. The bundled proxy fixes this: it is a small dependency-free Python script that Claude
Code spawns itself, so the `mcplink` server is *always* connected. Tool calls made while the
game is closed return a clear "Resonite is not running" error instead of a dead connection,
and the proxy transparently reconnects when the game (re)starts — even mid-session.

1. Copy the **`proxy\`** folder somewhere permanent (it can live anywhere; the proxy writes a
   small `tools_cache.json` next to itself).
2. Register it:

   ```
   claude mcp add mcplink -- python "C:\path\to\proxy\mcplink_proxy.py"
   ```

3. `claude mcp list` should now show `mcplink: ✓ Connected` — **even with the game closed**.

*One-time bootstrap:* the tool cache starts empty, so start one Claude session while the game
is running (or run `/mcp` → reconnect with the game up); after that, the tools are present in
every session regardless of game state.

Proxy environment overrides (set in the `claude mcp add` environment if needed):
`MCPLINK_HOST` / `MCPLINK_PORT` / `MCPLINK_PATH` (default `localhost` / `7357` / `/mcp`),
`MCPLINK_CONNECT_TIMEOUT` (default 3 s — how quickly a closed game is detected),
`MCPLINK_READ_TIMEOUT` (default 600 s — ceiling for long tool runs such as world scans and renders).

### Alternative: direct HTTP

Works with Claude Code and any client that speaks MCP streamable HTTP, but the server only
connects if Resonite is already running when the client session starts:

```
claude mcp add --transport http mcplink http://localhost:7357/mcp
```

## 4. Teach Claude how to use it — add the usage guide to your CLAUDE.md

The tools are self-describing, but the *craft* — how to read big ProtoFlux graphs cheaply,
which footguns the engine hides, when to checkpoint before mutating — is documented in
**`CLAUDE-MCPLINK.md`** (bundled in this release). Wire it into your project so Claude reads
it automatically:

1. Copy `CLAUDE-MCPLINK.md` into the project folder where you run Claude Code (or any
   subfolder, e.g. `docs\`).
2. Add an import line to that project's `CLAUDE.md` (create the file if you don't have one):

   ```markdown
   # Resonite / McpLink
   @CLAUDE-MCPLINK.md
   ```

   The `@path` import makes Claude Code inline the guide into its context each session.
   Adjust the path if you placed it elsewhere (e.g. `@docs/CLAUDE-MCPLINK.md`).

3. Alternatively — if you prefer a single file — paste the contents of `CLAUDE-MCPLINK.md`
   directly into your `CLAUDE.md`. The `@`-import keeps upgrades easier (just replace the file
   with the next release's copy).

For clients other than Claude Code, provide `CLAUDE-MCPLINK.md` to the agent by whatever
mechanism your client uses for standing instructions/system context.

## 5. Configuration (mod settings)

Via ResoniteModLoader's config (`rml_config\McpLink.json`, or a settings UI mod):

| Key | Default | Effect |
|---|---|---|
| `enabled` | `true` | Start the server on engine init (change requires restart) |
| `port` | `7357` | TCP port for the endpoint (localhost only; change requires restart) |
| `allowWrites` | `true` | Set `false` for a **read-only** agent: every mutating tool (set/call/attach/destroy/spawn/…) is refused while reads keep working |
| `enableHooks` | `true` | Allow the impulse-stream tools to Harmony-patch the ProtoFlux dispatcher (applied only while a watch is active, fully removed after). `false` disables that capability entirely; nothing else uses Harmony |

## 6. Troubleshooting

- **`mcplink` shows "Failed to connect" (direct HTTP)** — Resonite wasn't running when the
  session started. Use the proxy (§3), or start the game and reconnect via `/mcp`.
- **Proxy connected but zero tools** — the one-time cache bootstrap hasn't happened yet; run
  one session (or `/mcp` reconnect) while the game is up.
- **Port already in use** — change `port` in the mod config and either re-register the HTTP
  URL or set `MCPLINK_PORT` for the proxy.
- **`eval` fails with "companion not found"** — the `McpLink_libs` folder (step 2.2) isn't
  installed.
- **`eval` fails with an `InvalidCastException` mentioning `EvalGlobals`** — known limitation
  after a `hot_reload` (developer feature): the eval companion's pinned load context went
  stale. Restart Resonite; all other tools are unaffected.
- **A tool call froze the game** — synchronous work (e.g. an `eval` infinite loop) runs on the
  world update thread by design. The engine's ProtoFlux watchdog aborts runaway *graphs* after
  ~10 s, but `eval` has no watchdog. See the safety notes in `CLAUDE-MCPLINK.md`.

## 7. For developers

Source layout, building (`dotnet build -c Release`, `-p:ResonitePath=...` to point at your
install), the offline smoke suite, and the optional
[ResoniteHotReloadLib](https://github.com/Nytra/ResoniteHotReloadLib)-based `hot_reload`
iteration loop are covered in `README.md` in the source repository. `CHANGELOG.md` has the
full version history.

MIT licensed — see `LICENSE`.
