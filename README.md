# McpLink

*For anyone browsing this repo on GitHub — what McpLink is, why, and how to install it.
Developing it? See [`CLAUDE.md`](CLAUDE.md). Driving it from an agent? See
[`CLAUDE-MCPLINK.md`](CLAUDE-MCPLINK.md).*

**An MCP server that runs inside Resonite.** McpLink is a
[ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) mod that embeds
a [Model Context Protocol](https://modelcontextprotocol.io/) server in the game process, so an
AI agent — Codex, Gemini CLI, Claude Code, or any MCP-capable client — can inspect and modify your live Resonite
worlds: slots, components, ProtoFlux, assets, physics, screenshots, event streams, and (optionally)
C# against the running engine. **97 tools**, no per-session setup, works in any world including
Userspace.

Because the server lives *inside* the engine, it sees what in-game inspectors see: real RefIDs as
addresses, private/non-synced state via reflection, unrestricted method calls. Every mutating tool
registers with the engine's undo system, so an agent's mistake is one Ctrl+Z in-game.

> ⚠ **Security, before anything else.** The endpoint binds to **localhost only**, but anything
> that can reach it has, in effect, the power of the game process itself (arbitrary method
> invocation ≈ arbitrary code in-game). Don't expose the port beyond localhost. Set
> `allowWrites: false` in the mod config if you want a strictly read-only agent.

---

## 1. Get the mod set up

**You need:** Resonite (Windows; Steam path assumed by defaults, any install works) with
**[ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader)** installed
and working — that's the one hard prerequisite, and its README covers installing it from scratch.

1. **Download** the latest release from the
   [Releases page](https://github.com/Maurdekye/mcplink/releases) — either the full
   `McpLink-x.y.z.zip` bundle (recommended) or the bare `McpLink.dll`.
2. **Install** — the zip mirrors your Resonite install; with the game closed:
   - copy `rml_mods\McpLink.dll` into your Resonite `rml_mods` folder
     (e.g. `C:\Program Files (x86)\Steam\steamapps\common\Resonite\rml_mods`);
   - *(optional — enables the `eval` C# scripting tool)* copy the zip's
     `rml_mods\McpLink_libs\` folder in beside it. Every other tool works without it.
   - Or run `tools\install.ps1` from a clone of this repo — it finds your install, downloads the
     latest release, and does the above with the file locking handled loudly.
3. **Start Resonite** and confirm in the log (`Logs\` in the install dir):

   ```
   [McpLink] Tool registry built: 97 tools.
   [McpLink] MCP server listening on http://localhost:7357/mcp
   ```

4. **Connect your agent.** For **Codex, Gemini CLI or Claude Code**, the recommended route is the bundled
   always-up proxy (a dependency-free Python 3.8+ script — the one extra requirement of this
   route). Copy the zip's `proxy\` folder somewhere permanent, then use the command for your client:

   ```
   codex mcp add mcplink -- python "C:\path\to\proxy\mcplink_proxy.py"
   gemini mcp add mcplink python "C:\path\to\proxy\mcplink_proxy.py"
   claude mcp add mcplink -- python "C:\path\to\proxy\mcplink_proxy.py"
   ```

   The proxy keeps the `mcplink` server registered even while the game is closed (calls then
   return a clean "Resonite is not running" error and recover on their own when the game starts —
   even mid-session). Without Python you can register the endpoint directly, with the caveat that
   the server only connects if Resonite is already running when your session starts:

   ```
   codex mcp add mcplink --url http://localhost:7357/mcp
   gemini mcp add --transport http mcplink http://localhost:7357/mcp
   claude mcp add --transport http mcplink http://localhost:7357/mcp
   ```

   **Any other MCP client:** point it at the streamable-HTTP endpoint
   `http://localhost:7357/mcp`, or run `python mcplink_proxy.py` as a stdio server — both are
   standard MCP; nothing here is Claude-specific. And if registration won't work at all, an
   agent with shell access can drive the server directly:
   `python tools\mcp.py <tool> '<json args>'` (bundled helper, same dispatcher, no client
   config — see [INSTALL.md](INSTALL.md#for-agents-the-no-registration-fallback)).

5. **Teach the agent the craft.** The tools are self-describing, but the hazards and idioms
   (reading big ProtoFlux graphs cheaply, checkpointing before risky mutations, what silently
   no-ops) live in **[CLAUDE-MCPLINK.md](CLAUDE-MCPLINK.md)**. For Claude Code, copy it next to
   your project's `CLAUDE.md` and add `@CLAUDE-MCPLINK.md` to it; for Codex or Gemini CLI, fold the
   relevant guidance into the project's `AGENTS.md` or `GEMINI.md`; for other agents, feed it in as
   standing context.

*First-session note:* the proxy caches the tool list. Run one session (or reconnect via `/mcp`)
while the game is up, and the tools are present in every session after that, game running or not.
After **updating** the mod, restart your MCP client / session so cached tool schemas refresh.

That's the whole setup. For the slower path with every step spelled out — plus configuration,
troubleshooting, updating, and uninstalling — see **[INSTALL.md](INSTALL.md)**.

## 2. Recommended ways to enhance your McpLink installation

McpLink works standalone, but a few optional companions round it out: **claude-orgtree** for
in-world agent panels, a **C# decompiler MCP server** for grounding engine-behavior questions in
source, and **Blender** for preparing/fixing meshes before they go into the game. None of these
are required — set up whichever is useful to you; the rest of McpLink works without any of them.

### Connect to orgtree

McpLink pairs with **[claude-orgtree](https://github.com/Maurdekye/claude-orgtree)** — a custom
orchestrator that organizes Codex, Gemini CLI and Claude Code agents into an authority hierarchy — and the
integration runs deep: an in-world **Prompt Agent** panel lets you hire an agent from inside VR by clicking a
node in your live org chart, name it, pick its model tier and thinking effort, and then *chat with
it* in a floating panel that embodies the agent — presence ticker showing what it's doing right
now, its status reports as system lines, drag-and-drop reference attachments, interactive question
cards, and 3D wires drawn between panels of related agents. Deleting a panel retires its agent;
a detach button keeps it working headless instead.

**None of this appears unless orgtree is actually set up.** On an install without the companion,
McpLink never mentions it: the menu entry stays hidden and `open_prompt_wizard` returns a clear
"not set up" error naming this section. Set it up and the surfaces appear on their own:

1. Install and run [claude-orgtree](https://github.com/Maurdekye/claude-orgtree) on the same
   machine (its own README covers setup). McpLink expects the backend's admin API on
   `http://127.0.0.1:7360` — configurable via the `orgtreeBase` mod setting.
2. That's it. McpLink probes the backend cheaply in the background; within a minute of it coming
   up (immediately, if it's already running when the game starts), **Dev Tool → Create New →
   Editor → Prompt Agent** appears, and the `open_prompt_wizard` / `wizard_drive` tools go live.
   Agents you hire from the panel act with your user's authority on the orgtree side.
3. Optional settings (see [Configuration](#configuration)): `promptDefaultOrg` preselects which
   organization new panels open on; `promptHireDir` names a folder that panel-hired agents get
   read-write access to (empty = game folder only).

The Prompt Agent tier picker reads orgtree's live provider catalog, so tiers from Claude, Codex
and Gemini providers whose catalog entry has `hire_enabled: true` appear with their current credit
costs. Providers marked false are omitted from model selection. If that catalog cannot be read,
the panel visibly says it is showing an unfiltered legacy Claude-only fallback with unknown
registration status instead of silently hiding non-Claude tiers. To
exercise that diagnostic path on demand, set `MCPLINK_FORCE_PROVIDER_FALLBACK=1` before starting
Resonite; unset it to restore the live catalog.

**Offline queue (advanced):** with no backend but a `promptOutbox` file path configured, the
panel still works in a degraded mode — each submission appends one JSON line
(`type/id/timestampUtc/prompt/refs/placement/agentName/tier/effort/world/submitter/wizardSlotId/statusTextId`)
for an external orchestrator to consume. The default `placement` value is the authors'
orchestrator convention; your consumer is free to ignore it.

### Pair with a C# decompiler

McpLink shows an agent the *running* engine — live slots, components, values — but not source
code. For "why does this component behave that way" or "what does this method actually do"
questions, pair it with a decompiler MCP server pointed at your Resonite install's assemblies.
Live introspection (McpLink) plus decompiled source (the decompiler) answers engine-behavior
questions that neither tool answers alone — it's exactly the combination used to write McpLink's
own engine-behavior reference notes.

**This isn't a McpLink feature or config setting** — it's a second, independent MCP server your
client connects to alongside McpLink.

1. Install a decompiler MCP server. One working option is
   **[ILSpy-Mcp](https://github.com/gentledepp/ILSpy-Mcp)** (NuGet package `ILSpyMcp.Server`), a
   .NET global tool:

   ```
   dotnet tool install -g ilspymcp.server
   ```

   Any other ILSpy-based decompiler MCP server works the same way; this is just the one already
   in use in the authors' own environment.
2. Register it with your MCP client (e.g. Claude Code) as a stdio server, command `ilspy-mcp`,
   no arguments.
3. Point individual tool calls at the `.dll` assemblies sitting **directly in the Resonite
   install folder** (Steam default: `C:\Program Files (x86)\Steam\steamapps\common\Resonite\`) —
   `FrooxEngine.dll` (the engine proper), `Elements.Core.dll` (math/data types),
   `ProtoFlux.Core.dll` (the visual-scripting runtime), and so on. The server takes an assembly
   path as a parameter on each call rather than a fixed target, so there's nothing to
   preconfigure beyond knowing where your Resonite install lives. A map of which assembly holds
   what, and a workflow for reading them without drowning in decompiled output, is bundled at
   [`docs/engine-reference/decompiler-workflow.md`](docs/engine-reference/decompiler-workflow.md).

Once both are registered, an agent can cross-reference: ask McpLink what a live component's
field actually contains, then ask the decompiler what the component's code does with it.

A set of engine-behavior notes already written this way — grounded in decompiled
`FrooxEngine`/`Elements.Core`/`ProtoFlux` source rather than guessed — is included in this repo
under **[`docs/engine-reference/`](docs/engine-reference/)**: data model, execution order, hard
limits, localization, networking/users, particles, persistence, ProtoFlux, rendering/assets, and
transforms/math. Point an agent at them instead of (or alongside) re-deriving the same facts from
a live decompiler session.

### Pair with Blender

McpLink can import a mesh straight into a world (`spawn_import`, `import_file`) and export one
back out (`export_asset`, `export_skinned_gltf`), but it isn't a modeling tool — it doesn't fix a
bad rig, a wrong scale, or a broken UV. For that, pair it with **Blender**, driven two ways
depending on whether you want the agent to work in a live scene or run unattended:

**Live, interactive editing** — a Blender MCP server gives an agent the running Blender GUI:
scene inspection, arbitrary `bpy` execution, rendering, docs lookup. Any Blender MCP
implementation works; the one in use here is the official
**[Blender Lab MCP add-on](https://www.blender.org/lab/mcp-server/)**:

1. `pip install git+https://projects.blender.org/lab/blender_mcp.git`
2. In Blender, add the Blender Lab extensions repository
   (`https://lab.blender.org/` — see the
   [extensions-repository docs](https://docs.blender.org/manual/en/latest/editors/preferences/extensions.html#repositories)),
   find the MCP add-on, install and enable it, then connect.
3. Register the server with your MCP client as you would McpLink — it's a separate server, not
   a McpLink feature.

A useful non-obvious property of that server: it bundles the **complete Blender Python API
reference and user manual as plain-text files** in its install (`data/api/`, `data/manual/`), so
an agent can grep exact operator signatures and enum values locally instead of guessing at `bpy`
calls or fetching docs from the web.

**Headless, scripted fixes** — for a repeatable transform (rig repair, scale/roll correction,
FBX re-export) you don't need the GUI open at all: Blender runs the same Python API from the
command line with no window and exits when the script finishes.

```
blender --background --factory-startup --python fix_mesh.py -- <src.fbx> <dst.fbx>
```

`--background` skips the UI, `--factory-startup` ignores your local preferences/add-ons so the
result doesn't depend on one machine's setup, and everything after the bare `--` is `sys.argv`
inside the script (`bpy` is available exactly as it is in the GUI's Python console). This is the
better fit for an agent batch-processing many files, or for a step wired into a larger pipeline,
where launching and babysitting the GUI would be pure overhead.

The two combine: use the live MCP server to work out *what* a fix needs to do by inspecting one
file interactively, then land it as a headless script once it's proven, and run that script over
the rest of the batch.

The hard-won knowledge about *what those fixes usually are* — bind poses, the rig defects that
are invisible at rest, FBX export settings that silently break skinning — is bundled at
[`docs/engine-reference/blender-asset-pipeline.md`](docs/engine-reference/blender-asset-pipeline.md).

### Documentation already bundled in this repo

Some of the groundwork for the companions above is already written and checked into this repo —
point an agent at it directly instead of re-deriving the same facts:

- **[`CLAUDE-MCPLINK.md`](CLAUDE-MCPLINK.md)** — craft and hazards for interfacing with a *live*
  Resonite session through McpLink itself: the connection model, RefID addressing, footguns that
  silently no-op, when to checkpoint before mutating. See
  [INSTALL §5, "Teach the agent how to use it"](INSTALL.md#5-teach-the-agent-how-to-use-it) for
  wiring it into your client.
- **[`docs/engine-reference/`](docs/engine-reference/)** — reference for the engine's *own code*:
  data model, execution internals, hard limits, localization, networking/users, particles,
  persistence, ProtoFlux, rendering/assets, transforms/math. See
  ["Pair with a C# decompiler"](#pair-with-a-c-decompiler) above for how it was produced.
- **[`docs/engine-reference/decompiler-workflow.md`](docs/engine-reference/decompiler-workflow.md)**
  — the method behind those notes: which assembly holds what, how to search source without
  drowning in decompiled output, and the source-plus-live-session pattern.
- **[`docs/engine-reference/blender-asset-pipeline.md`](docs/engine-reference/blender-asset-pipeline.md)**
  — the Blender/asset side: bind poses, rig defects invisible at rest, FBX export gotchas, and
  the supported route for getting skinned meshes back out of the game.
- **[`docs/engine-reference/mod-authoring.md`](docs/engine-reference/mod-authoring.md)** — writing
  ResoniteModLoader mods in C#: lifecycle, config, Harmony patterns, thread marshaling, undo, and
  in-world UI, with McpLink's own project files as the worked example.

---

## What's in the toolbox

All 97 tools, grouped. Every tool takes `world` (`"focused"` default, `"userspace"`, or a world
name) and `maxBytes` (oversized results return a truncation notice); `id` addresses any element by
its real RefID, and `@name` bookmarks work anywhere an id does.

| Area | Tools |
|---|---|
| Orientation | `session_info` (worlds + **which build is answering** — version, MVID, deploy consistency), `get_slot`, `tree`, `ls`, `ls_components`, `stat`, `du`, `get_slot_transform`, `users`, `perf`, `focus_world`, `env` |
| Search | `find_slots` (name/tag/spatial `near`+`radius`), `find_components`, `grep` (every value type, chunked world scans), `find_referrers`, `find_assets` |
| Reading deep | `get_component` (incl. non-synced private state), `reflect_get`, `describe_type`, `list_component_types`, `dynvar_space` (phantom variables included), `dynvar_users`, `mesh_info`, `renderer_info` (materials with resolved texture URLs + common-defect findings), `bounds` |
| Writing | `set_member`, `update_slot`, `update_component`, `add_slot`, `attach_component`, `destroy`, `cp`, `mv`, `sed` (dry-run by default), `edit_list` (sync-list ops), `reflect_set`, `move_component`, `bake_skinned_mesh` |
| Batch & meta | `run_batch` (one atomic update-tick hop, `"$N.path"` result refs, single undo batch), `bulk_build` (thousands of slots/components in one tick), `xargs` (find + apply as one undo batch), `at`/`jobs`/`cancel_job` (scheduled batches), `wait_for` |
| ProtoFlux | `get_protoflux_subgraph` (relay collapse, flow traces), `flux_build`, `flux_connect`, `flux_ports`, `flux_splice`, `flux_trace`, `eval_output` (computed pins evaluate through the real runtime), `fire` (with execution feedback), `impulse_map` |
| Live observation | `logs` (engine log from startup), `watch`/`watch_changes`/`changes`/`unwatch` (event-driven, coalesced), `impulse_watch`/`impulse_events`/`impulse_unwatch` (live ProtoFlux execution streams; the only Harmony-patched feature, patched only while a watch is active) |
| Recovery | `save_object`/`load_object` (real-serializer checkpoints), `undo`/`redo`, `history`, `diff` (reference-remap-aware subtree compare) |
| Seeing the world | `render_view` (off-screen render from any pose/camera/user view; `isolate`/`exclude` hierarchies), `orbit_render`, `view_scan`, `raycast` |
| In-world interaction | `marker`, `notify`, `jump_user`, `user_pointer`, `user_avatar`, `dynamic_impulse`, `spawn_markdown` (markdown → scrollable in-world panel) |
| Assets & import/export | `import_file`, `spawn_import` (full import pipeline, reports the transform it applied), `spawn_object` (incl. `resrec://` cloud records), `inventory`, `export_asset`, `export_package`/`import_package` (portable `.resonitepackage` round-trip), `export_skinned_gltf`, `tar` (subtree → JSON snapshot) |
| Escape hatches | `eval` (C# against the live engine; needs the optional `McpLink_libs` companion), `call_method` (any method, full argument construction), `hot_reload` (developer feature) |
| orgtree companion | `open_prompt_wizard`, `wizard_drive` (see [Connect to orgtree](#connect-to-orgtree)) |
| Session sugar | `bookmark`/`bookmarks`, aliases `rm`/`cat`/`ps`/`schedule` + the resomcp-compatible names |

Values encode the way in-game data reads: typed literals
(`{"$type":"float3","value":{"x":0,"y":1,"z":0}}`), bare JSON coerced to the target type,
`[x,y,z]` arrays for math structs, `{"$ref":"ID..."}` element references, enums by name, and
`{"$new":"TypeName","args":[...]}` for arbitrary construction.

## Configuration

The comfortable route is the in-game
**[ResoniteModSettings](https://github.com/badhaloninja/ResoniteModSettings)** mod — a settings
page for every RML mod, McpLink included, with persistent editing while you play. Editing
`rml_config\McpLink.json` by hand also works; do that with the game closed (ResoniteModLoader
rewrites the file at shutdown from the running mod's known keys, so mid-session hand-edits are
lost — and leave its `"version"` field at `"1.0.0"`, the config-format version).

| Key | Default | Effect |
|---|---|---|
| `enabled` | `true` | Start the server on engine init (change requires restart) |
| `port` | `7357` | TCP port for the endpoint (localhost only; change requires restart) |
| `allowWrites` | `true` | `false` = read-only agent: every mutating tool refuses, reads keep working |
| `enableHooks` | `true` | Allow the impulse-stream tools to Harmony-patch the ProtoFlux dispatcher (applied only while a watch is active, fully removed after); `false` disables that capability — nothing else uses Harmony |
| `orgtreeBase` | `http://127.0.0.1:7360` | Where the optional orgtree companion's admin API answers |
| `promptDefaultOrg` | *(empty)* | Org slug the Prompt Agent wizard preselects (empty = backend's first-listed) |
| `promptHireDir` | *(empty)* | Folder granted read-write to panel-hired agents (empty = game folder only) |
| `promptOutbox` | *(empty)* | Offline-queue fallback file for wizard submissions when no backend answers |

## Updating

Run `tools\update.ps1` from a clone — it downloads the latest release, swaps the DLL (telling you
plainly if the game's file lock blocked it, and cleaning up the leftover `.PENDING` note a blocked
build leaves), and verifies the copy. Or do the same by hand: game closed, overwrite
`rml_mods\McpLink.dll` (+ `McpLink_libs` if you use `eval`).

Version truth: builds are not byte-reproducible, so never trust file timestamps. Ask the running
server — MCP `initialize` reports the version, and `session_info` reports the build's MVID, per
on-disk copy, with `deployConsistent` telling you whether a restart would load the same code.
Then restart your MCP client so cached tool schemas refresh.

## Limitations and safety notes

- **Writes are live.** Mutations go straight into the data model. They're undo-aware, but
  Userspace writes can still crash the engine — the safety rails are you (and
  `save_object` checkpoints).
- A write to a **driven** field is a silent engine no-op (`set_member` warns you).
- **`eval` runs on the world update thread** — an infinite loop freezes the game; there is no
  watchdog. Compile happens off-thread; execution doesn't.
- **`hot_reload`** (developer feature) needs
  [ResoniteHotReloadLib](https://github.com/Nytra/ResoniteHotReloadLib) in `rml_libs`. Known
  issue: after a hot reload, `eval` fails with a stale-load-context error until the game
  restarts; every other tool survives reloads.
- RefIDs die with the world: after a world reload, re-find objects by name/path.
- The engine's undo stack caps at 50 steps; `run_batch`/`xargs` deliberately batch to one entry.

## Building from source

Prereqs: **.NET 10 SDK**, internet for one NuGet restore (the eval companion pulls Roslyn), a
**Resonite install** (for reference DLLs) with **ResoniteModLoader** installed, and
**[ResoniteHotReloadLib](https://github.com/Nytra/ResoniteHotReloadLib)**'s DLL in the install's
`rml_libs\` — it's optional at *runtime* but required to *compile*.

```
dotnet build -c Release -p:ResonitePath="C:\path\to\Resonite"
```

(`ResonitePath` defaults to the Steam location.) A build **never writes into your game folder** —
from a clone, a worktree, or the canonical tree; that's deliberate (a build side effect nobody
chose is a deploy nobody verified). Deploy with `tools\deploy.ps1` (game open or closed — it
stages, waits for the file lock if needed, backs up the outgoing DLLs, and verifies both mod
slots against a pinned hash), or use `tools\install.ps1 -FromBuild` for a first install. For a
hot-reload *development* loop, stage the reload slot explicitly with `-p:StageHotReload=true` —
that is prototyping, not a deploy. Never trust "it built" as "it's installed"; ask `session_info`.

The offline smoke suite — dispatcher, every schema, type resolution, codecs, real Roslyn eval, a
real Harmony patch/unpatch cycle, 255 checks, no game needed — is the gate for every change:

```
dotnet run --project test\McpLinkSmoke.csproj -c Release
```

On a non-Steam install it needs *both* halves pointed over: `-p:ResonitePath=...` (compile-time
references) *and* the `RESONITE_PATH` environment variable (runtime assembly resolution). The
test project also expects ResoniteHotReloadLib present (`test\rml_libs`).

`powershell -File package.ps1` is the packaging pipeline: Release build → smoke suite as a gate →
`release\McpLink-<version>.zip`. The dev iteration loop, deploy-verification tooling, and the
engineering diary live under [`docs/dev/`](docs/dev/) and [`tools/dev/`](tools/dev/).

**Releasing (maintainers) — every version increase ships a GitHub Release.** That's a standing
rule, not a nicety: a `VERSION` bump isn't finished until the
[Releases page](https://github.com/Maurdekye/mcplink/releases) carries it with both assets
(the zip and the bare DLL), because installs and `tools\update.ps1` feed from there. The
standard task is one command from a clean main:

```
powershell -File tools\release.ps1        (-DryRun rehearses everything but the publish)
```

It refuses loudly unless the version is bumped, the matching `CHANGELOG.md` section exists,
HEAD is a clean main, and `gh` is authenticated — then packages (suite-gated, deploys pinned
off so releasing never touches a live install), tags `v<version>`, pushes, publishes the
Release with notes auto-extracted from the changelog, and verifies both assets actually landed.

## Project history

McpLink was built AI-first: the mod is developed, tested, and maintained by Claude Code agents
(coordinated through [claude-orgtree](https://github.com/Maurdekye/claude-orgtree)) working
against the live game, with the human owner directing and verifying in-world — the commit
history and the engineering notes in [`docs/dev/`](docs/dev/) show that process honestly,
friction and all. [`CHANGELOG.md`](CHANGELOG.md) has the full version-by-version story from
the first 0.3 tool surface to today.

## License

[MIT](LICENSE) © 2026 Maurdekye
