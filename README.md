# McpLink

A standalone **MCP (Model Context Protocol) server that runs inside the Resonite process** as a
ResoniteModLoader mod. The poweruser counterpart to [resomcp](../resomcp/): where resomcp speaks
the official ResoniteLink protocol (safe, distributable, per-session opt-in, synced data model
only), McpLink trades that sandbox for total access:

- **No per-session setup.** The server starts with the engine on a fixed port — no
  "Enable ResoniteLink" dance, no changing port numbers.
- **Any world**, including **Userspace**, which ResoniteLink can never target.
- **Real RefIDs** as addresses (`ID1A2B00...`) — the same identifiers in-game inspectors show,
  valid for the world's lifetime (not per-connection synthetic ids).
- **Private state.** `reflect_get` reads any field/property, private included
  (`_dynamicValues`, `handler._currentSpace`, ...). `get_component includeNonSynced:true`
  dumps non-synced engine state ResoniteLink cannot see.
- **Unrestricted method calls.** `call_method` invokes anything with full argument
  construction — plain-class parameters (`DuplicationSettings`), optional-parameter defaults,
  generic methods, out-params — the exact calls the closed ResoniteLink verb union cannot express.

## Distribution / releases

`powershell -File package.ps1` produces `release\McpLink-<version>.zip` — the full release
pipeline: Release build of the mod + eval companion → **offline smoke suite as a gate** (89
tools, no game needed) → zip. The version is read from `McpLinkMod.VERSION` (single source of
truth; bump it + add a `CHANGELOG.md` entry before packaging). The zip is what end users get:

```
rml_mods\McpLink.dll              the mod (drop into rml_mods)
rml_mods\McpLink_libs\*.dll       optional eval companion (Roslyn closure)
proxy\mcplink_proxy.py            always-up stdio proxy for Claude Code
INSTALL.md                        user-facing setup guide
CLAUDE-MCPLINK.md                 Claude-facing usage guide (users @-import it into CLAUDE.md)
CHANGELOG.md · LICENSE            (MIT)
```

`INSTALL.md` is the document to hand a new user; it covers install, the proxy, mod config,
adding `CLAUDE-MCPLINK.md` to their CLAUDE.md, and troubleshooting. Keep `CLAUDE-MCPLINK.md`
**user-agnostic** (no workspace paths, no session ids) — it ships into other people's context.

## Install / connect

1. Build: `dotnet build -c Release` (auto-copies `McpLink.dll` into `rml_mods`; requires
   [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader)).
2. Restart Resonite. The log line `[McpLink] MCP server listening on http://localhost:7357/mcp`
   confirms it's up.
3. Connect Claude Code — **preferred: via the always-up proxy** (survives the game being closed):

   ```
   claude mcp add mcplink -- python "<repo>\mcplink\proxy\mcplink_proxy.py"
   ```

   Direct HTTP also works, but the server then only connects if Resonite is already
   running when the Claude session starts:

   ```
   claude mcp add --transport http mcplink http://localhost:7357/mcp
   ```

### The stdio proxy (`proxy/mcplink_proxy.py`)

McpLink lives inside the game process, so its HTTP endpoint vanishes whenever Resonite is
closed — and an HTTP-registered MCP server that's down at session start contributes zero
tools to that session. The proxy (stdlib-only Python, spawned by Claude Code per session
over stdio) makes `mcplink` permanently "up":

- `initialize`/`ping` are answered locally and always succeed.
- `tools/list` is forwarded to `localhost:7357` when the game is up, and the result is
  cached to `proxy/tools_cache.json`; when the game is down, the cache is served, so the
  tools still register. (Bootstrap: the cache is empty until the first `tools/list` with
  the game running — after that one session, tools are always present.)
- `tools/call` is forwarded live; if the game is closed it returns an `isError` result
  saying "Resonite is not running" instead of a dead connection.
- If the game **restarts mid-session**, the proxy detects the stale backend session
  (HTTP 404), re-initializes against the new instance, and retries the call transparently.

Env overrides: `MCPLINK_HOST`/`MCPLINK_PORT`/`MCPLINK_PATH`, `MCPLINK_CONNECT_TIMEOUT`
(default 3 s — how fast a closed game is detected), `MCPLINK_READ_TIMEOUT` (default 600 s —
ceiling for long tool runs like world scans/renders).

Config (via RML settings): `port` (default 7357), `enabled`, and `allowWrites` — set false to
gate every mutating tool (`reflect_set`, `set_member`, `call_method`, `add_slot`,
`attach_component`, `destroy`) while keeping reads.

## Beyond resomcp (v0.3–v1.0)

**1.0.0** is the first stable release: 85 tools, every wave live-verified (see
`VERIFICATION.md`), full history in `CHANGELOG.md`. The 1.0 verification pass also fixed three
long-standing bugs: `{"$ref":...}` reference writes through `set_member`/`update_component`/
`bulk_build` (the field case swallowed refs — bare `"ID..."` strings had masked it), `colorX`
from `[r,g,b,a]` arrays (constructor-arity fallback), and a `history` crash on undo entries
whose targets were destroyed.

| Tool | What it does |
|---|---|
| `render_view` / `orbit_render` isolation (v1.6) | **`isolate` + `exclude` args on both camera tools**: pass a slot/component id (or array) and the render shows ONLY those hierarchies (`isolate`) or hides them (`exclude`) — occluding walls/props no longer interfere when inspecting one object. Engine-native (`RenderTask.renderObjects`/`excludeObjects`, the same mechanism as `Camera.SelectiveRender`); explicit args override a `cameraId` camera's own lists. `orbit_render targetId:X isolate:X` = walk around an object with the world stripped away |
| `move_component` (v1.5) | **move a component onto another slot** — the exact semantics of the in-game flow (grab a component reference → drop on another inspector's component view → "Move Component"): engine `ContainerWorker.MoveComponent`, world-wide reference retarget (`World.ReplaceReferenceTargets` — drives, bone refs, list elements all follow), original destroyed, new RefID returned. `copy:true` = the menu's "Copy Component" (plain copy, original kept). Not undoable |
| `bake_skinned_mesh` (v1.5) | **the inspector's "Bake to Static Mesh" button as a tool** (the button handler needs a live IButton): bakes the SMR's current pose + blendshape weights to a new static mesh asset (same MeshX bake + LocalDB save path as the button), attaches the baked StaticMesh beside the source mesh provider and a MeshRenderer with the same materials on the renderer's slot. Default KEEPS the SkinnedMeshRenderer (no duplicate-first dance); `destroyOriginal:true` replicates the button exactly |
| `flux_ports` (v1.3) | **port discovery for one node**: every data input, impulse (incl. list elements — `Calls[2]`), reference, and globalRef with the exact names `flux_connect`/`flux_splice` accept, plus the node's own connectable targets (operations, outputs) — each port with its value/target type and current target (RefID + owning node + member). Shares the enumeration with flux_connect, so names always agree |
| `flux_splice` (v1.3) | **insert a node into an existing wire** in one call + one undo batch: nodeId's impulse (a ContinuationRelay's `Next`, a Sequence `Calls[i]`) or data-input port is re-aimed at the inserted node, and the inserted node's continuation/input is wired to the original target — both via the engine's type-checked connect APIs. `insertOutPort`/`insertInPort` override the defaults (first free port) |
| `eval_output` probe (v1.3) | **computed ProtoFlux pins now evaluate**: pure value nodes, `LocalValue`, multi-output members — evaluated through the group's own ExecutionRuntime (BorrowContext → stack frame → `EvaluateValue/Object` on the mapped output — the exact `EvaluateImmediatelly` mechanics, aimed at an output). No probe objects spawned, no world mutation, synchronous. `probe:false` keeps the stored-only fast path |
| `flux_build` globals + near (v1.3) | node specs take `globals:{"VariableName":"scope/name"}` — GlobalRef members set via the engine idiom (a `GlobalValue<T>` on the node's slot, `T` inferred from the ref's `IGlobalValueProxy<T>` target type; clear error if the member isn't a GlobalRef or the value doesn't decode) — and `near:"<id>"` auto-placement: free spot beside that node, matching neighbor spacing + rotation (collision-free scan, not a layout engine). `flux_connect` gains `disconnect:true` (sever a port undoably, list elements included) |
| `fire` feedback (v1.3) | primary arg is now `id` (`operationId` still accepted); result carries an `execution` report — whether the rig actually flipped, the target group's `LastImpulseFlowError`, and error-level engine log lines captured during the settle window (empty = no observed throw). Arg-name unification across ALL tools: single-element tools accept `id` (aliases resolved centrally; legacy names — `rootId`, `targetId`, `slotId`, `target`, ... — keep working; passing both is an error) |
| `save_object` fix (v1.3) | `dependencies` accepts a bool again without exploding (`false`→`BreakAll`, `true`→`CollectAssets`) alongside the mode strings `CollectAssets`/`CollectAll`/`BreakAll` |
| `spawn_markdown` (v1.2) | **markdown → in-world RadiantUI panel** (title bar, pin/close, scrollable) — THE way to hand the user a readable report/note in-game. Headers, bold/italic/strike, inline + fenced code, lists, blockquotes, best-effort tables, rules; literal `<` is noparse-safe. Placement: in front of the local user by default (`inFrontOf` = another user, `distance`), explicit `position`+`lookAt`, or `replaceId` to update a previous panel in place (same pose). `markdownPath` for long docs; `canvasScale` default 0.001 (800 px ≈ 0.8 m). Returns the panel RefID — keep it for `replaceId` |
| `export_package` / `import_package` (v0.10) | **portable .resonitepackage round-trip** — the game's own item-export format (identical to the in-world Export dialog): object graph + every referenced local/cloud asset bundled into one self-contained file, reimportable here (`import_package`, undoable spawn) or by drag-and-drop into any Resonite install. The share/backup layer above `save_object`, which stores asset URLs only. `includeVariants:true` also bundles precomputed asset variants. Import pre-validates the package and surfaces failures the engine's importer normally swallows |
| `user_avatar` (v0.10) | what a user **looks like and is carrying**: the equipped avatar (object root + occupied body nodes), other worn attachments (per body node), and per hand the equipped tool + grabbed object roots. Complements `user_pointer` (aim/laser); feed the avatar root to `export_package` to snapshot it |
| `edit_list` (v0.10) | **sync-list editing** (SyncList/SyncFieldList/SyncRefList/SyncAssetList — `MeshRenderer.Materials`, ProtoFlux variadic inputs, ...): add/insert/set/remove/move/clear ops in order, or `values` for wholesale replace — the member kind `set_member` rejects, without the reflect_get→call_method("Add") dance. Structural ops register engine list undo points (move excepted); one undo batch per call |
| `impulse_watch` / `impulse_events` / `impulse_unwatch` (v0.9.1) | **live ProtoFlux activity streams** at node-GROUP granularity: externally-invoked executions (dynamic-impulse receivers, CallInput fires, the `fire` tool) and event dispatch (FireOnTrue, buttons) per group with ms timing, plus a dynamic-impulse bus tap (tag, target hierarchy, receiver count; untyped sends only). The dynamic truth static wiring can't show: "pull the trigger, read the trace" — pair with `get_protoflux_subgraph` flowTrace for intra-group order. The only Harmony-patched feature: patches `DynamicImpulseHelper` + `ProtoFluxNodeGroup` (non-generic methods only — see below) **lazily on the first watch, unpatches completely when the last stops**; hot path fast-exits on a flag; hook bodies are exception-proofed. Opt out entirely with the `enableHooks` mod config. Group map is a snapshot — re-watch after graph edits. **⚠️ Never patch a constructed generic method: it won't intercept organic calls (shared canonical body) and executing the stub crashes the process — v0.9.0 learned this the hard way; `ResolvePatchTargets()` now refuses generic targets.** |
| `diff` (v0.8) | structural diff of two slot subtrees: slots/components on only one side + member-level value differences on paired ones. **Reference-remap aware** — refs to targets INSIDE each subtree compare by relative path, not RefID, so a healthy copy vs a broken copy of the same gadget surfaces only real divergence. Compose with `load_object` (restore a checkpoint beside the live object, diff, destroy) for checkpoint-vs-live |
| `xargs` (v0.8) | find + apply: match slots (namePattern/tag) or components (typePattern), run any tool once per match with `$id`/`$slotId`/`$name` substituted into an args template — one atomic hop, ONE undo batch. `dryRun:true` previews the matches. "Retint every UnlitMaterial under this root" in one call |
| `at` / `jobs` / `cancel_job` (v0.8) | schedule a run_batch to fire after a delay in world time, optionally repeating — "flip this bool in 5 s while I watch", timed choreography, delayed cleanup. In-memory registry with status + last-result |
| `top` (v0.8) | hotspot ranking: the N heaviest slots in a subtree by components / ProtoFlux nodes / mesh renderers / colliders / children, plus subtree totals for all metrics — "where is the weight in this world" |
| `history` (v0.8) | read the undo/redo stacks (descriptions, validity) without performing anything — see what `undo` would roll back first |
| `mv` (v0.8) | reparent/rename slots with keepGlobalTransform:true default (objects stay where they are — unlike update_slot's parentId, which keeps local values); multi-slot moves in one undo batch |
| `orbit_render` (v0.8) | N renders orbiting a target (auto-framed from its bounds) — the "walk around it and look" inspection pass a single viewpoint can't give |
| `bookmark` / `bookmarks` (v0.8) | name a RefID once, then use `@gun` / `@trigger` anywhere an id argument is accepted — readable handles for long sessions. Session-scoped (RefIDs die with the world) |
| `tar chunked:true` (v0.8) | whole-world exports walked a few thousand slots per tick — no game hitch; snapshot is then not atomic (the default single-tick mode still is) |
| aliases (v0.8) | `rm`→destroy, `cat`→get_component, `ps`→perf, `schedule`→at |
| `eval` (v0.7) | **run C# against the live engine** — the escape hatch for anything no tool covers. Globals: `world`, `engine`, `resolve("ID...")`, `log(x)`, `vars` (persists across calls); statements or a final expression as result; `await` supported; all loaded engine assemblies referenced. Roslyn (~9.5 MB) is NOT in McpLink.dll — the `McpLinkEval` companion + closure sit in `rml_mods\McpLink_libs\` and lazy-load into an isolated `AssemblyLoadContext` on first call (build `mcplink/eval` to deploy). Compiles off-thread; **executes on the update thread — an infinite loop freezes the game, no watchdog** |
| `inventory` (v0.7) | browse cloud inventory records (items/folders/links) at a path; items carry the `resrec` URI `spawn_object` accepts. Works for group inventories via `owner:"G-..."` |
| `spawn_object` (v0.7) | now resolves **record URIs** (`resrec:///U-.../R-...`) through the cloud — inventory → spawn in two calls (previously needed the raw asset URI) |
| `find_assets` (v0.7) | asset inventory of a subtree: every Uri-valued field grouped by URL — what meshes/textures/audio a creation uses, use counts, sample holder components. Pairs with `export_asset` |
| `logs` (v0.6) | read the engine log (UniLog ring buffer, captured from startup): component exceptions, asset failures, mod errors — filter by level/regex, poll incrementally with `sinceSeq`. The place to look when an in-world action misbehaves silently |
| `watch_changes` / `changes` / `unwatch` (v0.6) | **event-driven** change subscriptions (vs the polling `watch`): Changed / child + component add/remove / transform / destroy events on a slot, component, or single field, **coalesced per (element, member, kind) with counts** — a driven field changing every frame is one entry. `fields:true` records which sync member changed with its new value; `changes waitMs` long-polls ("fire, then see what happened" in one round trip) |
| `save_object` / `load_object` (v0.6) | **checkpoint / restore** any slot subtree via the engine's real object serializer (same DataTree format as inventory items; `.brson`/`.lz4bson`/`.json`). The disaster-recovery layer beyond the 50-step undo cap: checkpoint before risky mutations of user creations, restore after a world reload. `.json` doubles as an offline structure dump |
| `undo` / `redo` (v0.6) | perform engine undo/redo steps directly — the agent rolls back its own mistakes instead of asking the user to Ctrl+Z |
| `dynamic_impulse` (v0.6) | send a dynamic impulse (optionally with a typed payload) into a hierarchy — the engine's own receiver dispatch, identical to a trigger node firing. With `impulse_map` this makes every in-world gadget's RPC surface directly callable; also fires async receivers |
| `user_pointer` (v0.6) | what a user is interacting with right now: per hand the laser's current hit (slot/path/object root/point/distance), grabbed objects, equipped tool, head view pose. "Point at it and I'll look at it" object designation |
| `marker` (v0.6) | temporary unlit sphere + floating label at a point/element, self-destroys after `ttlSeconds` — lets the in-world user SEE what the agent means |
| `jump_user` (v0.6) | teleport the local user next to a point/element (engine `JumpToPoint`) — "take me to what you built" |
| `notify` (v0.6) | toast on the user's dash (visible in VR) — completion pings for long tasks |
| `export_asset` (v0.6) | asset URL (or asset component) → file on disk, via the engine's gatherer (cloud assets download). Reverse of `import_file`: round-trip textures/meshes through Blender or an editor |
| `render_view` (v0.6: pose sources) | now also renders from `cameraId` (a Camera component uses its full settings — FOV/clip/selective-render; any slot = its pose) or `user` (that user's head view — see what they see) |
| `raycast` (v0.5) | physics ray returning **all** colliders along it sorted by distance (the target may be behind a railing); pose from `origin`+`direction`/`lookAt`/`rotation` or `cameraId` (a Camera/slot RefID — its position + forward); hits carry slot, path, and object root |
| `view_scan` (v0.5) | "what is this viewpoint looking at" for things a physics ray can't hit: slots whose **rendered** mesh bounds fall in a view cone, sorted by angle off-axis then distance; same pose args as `raycast`; `maxSize` filters out walls/roofs when hunting props |
| `bounds` (v0.5) | world-space bbox of a slot subtree via the engine's `BoundsHelper` (the inspector box); `children:true` = per-direct-child breakdown |
| `mesh_info` (v0.5) | mesh asset stats for a slot / MeshRenderer / mesh provider: vertex/triangle/submesh counts, channels, bones, blendshapes, local bounds, `degenerate` flag for broken 0-triangle meshes |
| `render_view` (v0.4) | off-screen screenshot of a world from any viewpoint → image file on disk (PNG default); aim via `lookAt` point or `rotation` quat; uses the engine's `RenderTask` queue (same path as `Camera.RenderToBitmap` / world thumbnails), creates nothing in the world |
| `bulk_build` | thousands of slots/components/cross-refs (`"@id"` placeholders) in ONE update tick — bypasses the importer's ~1.5-frames-per-object scheduling; inline spec or `specPath` file |
| `flux_build` / `flux_connect` | declarative ProtoFlux construction with the engine's type-checked `TryConnect*` APIs (can't produce broken graphs); optional visuals |
| `import_file` | file → localdb:// asset URL (the ResoniteLink import path); aliases `import_texture`/`import_audio` |
| `spawn_import` | full standard import pipeline for models/images/audio at a world position; returns the spawned **root slot** and (v0.5) waits for the imported hierarchy to stop growing — the engine's import task completes before conversion does |
| `spawn_object` | spawn a saved object by asset URI under a holder slot |
| `users` / `perf` / `focus_world` | who's here + head positions; per-world frame delta; switch focus |

**Undo-aware writes:** every mutating tool registers with the engine's undo system (batches named
"McpLink: …", field/reference undo points, spawn/destroy undo points) — agent mistakes are
Ctrl+Z-able in-game. Opt out per call with `undoable:false`. Since v0.5 a whole `run_batch` is
**one** undo batch: a 300-op mistake is a single Ctrl+Z instead of 300 entries against the
engine's 50-step undo cap.

**v0.5 fixes/quality:** `Uri` values decode from bare strings and from the
`{"$type":"Uri","$string":...}` shape the encoder itself emits (previously the latter silently
wrote **null** — the dangerous one); `find_slots` takes `near` + `radius` for spatial lookups
("what's within 2 m of this point"); `tar includeBounds:true` embeds per-slot world-space renderer
bounds so offline spatial analysis needs no transform math.

**Chunked scans:** `grep`, `find_slots`, `find_components`, `find_referrers` walk a few thousand
slots per update tick (`slotsPerTick`) — whole-world scans no longer hitch the game. (`tar`, `du`,
`sed` and the subgraph export remain single-tick by design: atomic snapshot / atomic mutation.)

## Tools (v0.2 — full resomcp replacement surface)

| Area | Tools |
|---|---|
| Orientation | `session_info`, `get_slot`, `tree`, `ls`, `ls_components`, `stat`, `du`, `get_slot_transform` |
| Search | `find_slots`, `find_components`, `grep` (**all** value types, not just strings), `find_referrers` |
| ProtoFlux | `get_protoflux_subgraph` (relay collapse, summary/flowTrace with cross-entry dedupe, **constants inlined into edges**), `impulse_map` (the dynamic-impulse RPC surface as a routing table), `eval_output` (v1.3: computed pins evaluate through the runtime), `fire` (v1.3: execution feedback), `flux_ports`, `flux_splice` (v1.3) |
| Deep access | `get_component` (+`includeNonSynced` private fields), `reflect_get`, `reflect_set`, `call_method`, `dynvar_space`, `dynvar_users` (who declares/drives/reads/writes a variable), `env` |
| Writes | `set_member`, `update_slot`, `update_component`, `add_slot`, `attach_component`, `destroy`, `cp` (real `Slot.Duplicate`), `sed` (dry-run by default) |
| Meta | `run_batch` (atomic single update hop, `"$N.path"` result refs), `describe_type`, `list_component_types`, `watch` (polling), `tar` (subtree → JSON file for offline analysis) |
| Observation (v0.6) | `logs`, `watch_changes`/`changes`/`unwatch` (event subscriptions) |
| Recovery (v0.6) | `save_object`/`load_object` (file checkpoints), `undo`/`redo` |
| Interaction (v0.6) | `dynamic_impulse`, `user_pointer`, `marker`, `jump_user`, `notify` |
| Escape hatch & cloud (v0.7) | `eval` (C# scripting), `inventory`, `find_assets`, resrec-aware `spawn_object` |
| Shell idioms (v0.8) | `mv`, `diff`, `top`, `history`, `at`/`jobs`/`cancel_job`, `xargs`, `orbit_render`; aliases `rm`/`cat`/`ps` |
| Impulse streams (v0.9.1) | `impulse_watch`, `impulse_events`, `impulse_unwatch` (Harmony, lazy-patched, per-group) |
| Packages, avatar & lists (v0.10) | `export_package`/`import_package` (.resonitepackage round-trip), `user_avatar`, `edit_list` |

resomcp aliases are accepted (`add_component`, `remove_slot`/`remove_component`, `call_static_method`,
`get_type_definition`/`get_component_definition`/`get_enum_definition`/`get_generic_type_definition`).
Deliberately **not** ported: `connect`/`connection_status`/`disconnect` (no connection to manage) and
`resolve_reference` (member ids are real RefIDs here — `stat` answers "what is this id" directly).
Not yet ported: `import_cubemap`/`import_mesh` (raw-data codecs), `diff`, `ln`/`readlink`.

**The original "later" list has fully shipped:** C# eval (v0.7, Roslyn-weight concern solved by
lazy isolated-ALC loading from `rml_mods\McpLink_libs\`), resrec:// resolution (v0.7
`spawn_object`), in-mod `diff` (v0.8, reference-remap aware), chunked `tar` (v0.8, opt-in), and
impulse streams (v0.9, user-approved Harmony — lazily patched, fully unpatched between uses).

## Building

`dotnet build -c Release` in `mcplink/` deploys `McpLink.dll` to `rml_mods` (skipped if the game
holds the lock) **and** to `rml_mods\HotReloadMods\` (always succeeds); the same in
`mcplink/eval/` deploys `McpLinkEval.dll` + the Roslyn closure to `rml_mods\McpLink_libs\`
(optional — every tool except `eval` works without it).

**Iteration loop (no game restart, v1.1+):** with
[ResoniteHotReloadLib](https://github.com/Nytra/ResoniteHotReloadLib) in `rml_libs`, the cycle is
edit → `dotnet build -c Release` → call the **`hot_reload`** tool → test. The server tears itself
down (port, watches, Harmony patches, jobs), the new DLL loads from memory, and the server is back
on the same port in ~1 s. Session state (bookmarks/watches/jobs/eval vars) resets; the eval
companion is the one piece that still needs a restart to swap. Verify a reload took via `logs`.

`mcplink/test/` holds the offline smoke
suite (`dotnet run -c Release`, 88 checks) — run it after changes; it exercises the dispatcher,
every schema, type resolution, codecs, real Roslyn eval, a real Harmony patch/unpatch cycle of the
impulse-stream hooks, the invariant that no impulse patch target is a constructed generic (the
2026-07-07 crash guard), and the v1.3 wave (arg-name aliasing, disconnect/splice validation,
GlobalRef T-inference, free-position scan, and an engine-drift guard over the eval_output
evaluation path) — all without a running game.

Every tool takes `world`: `"focused"` (default), `"userspace"`, or a world name, and an optional
`maxBytes` budget (oversized results return a truncation notice; `get_protoflux_subgraph` degrades
to its summary instead). All world access is marshaled to that world's update thread with a timeout.

`dynvar_space` uses the technique from Banane9's DynVarSpaceTree mod: it reads the space's private
`_dynamicValues` registry (so **phantom variables** — read but never declared — appear) and
classifies declaring components by their own `handler._currentSpace` (the engine's actual binding,
not name-prefix guessing), also reporting unbound declarations and ones bound to other spaces.

## Value encoding

Compatible with resomcp muscle memory: `{"$type":"float3","value":{"x":0,"y":1,"z":0}}` typed
literals (assembly-bracketed type names tolerated), plus `{"$ref":"ID..."}` element references,
enums by name, bare JSON coerced to the parameter type, `[x,y,z]` arrays for math structs, and
`{"$new":"TypeName","args":[...]}` for constructing arbitrary objects.

## Architecture notes

- **Zero dependencies** — the MCP streamable-HTTP surface a tools-only server needs
  (`initialize`, `tools/list`, `tools/call`, `ping`) is hand-implemented over `HttpListener`
  (localhost-only, no URL ACL needed). Pulling the official MCP SDK + ASP.NET Core hosting into
  the game process would invite dependency conflicts in `rml_mods`.
- **Harmony only when observing impulses** — everything else hosts a server, reflects, and
  (v0.6) subscribes to the engine's public events (UniLog, Changed, ChildAdded, ...). The v0.9
  impulse streams patch the ProtoFlux dispatcher while a watch is active and unpatch when the
  last watch stops; `enableHooks:false` in the mod config disables the capability entirely.
- Threading, undo, drive semantics: writes go straight into the live data model. A driven field
  write is a silent engine no-op (`set_member` warns when the target is driven). Mutations are
  undo-aware (and `undo`/`redo` can roll them back), but Userspace writes can still crash the
  whole engine — the safety rails are you.

## Security

The endpoint binds to localhost only and is as powerful as the game process itself (arbitrary
method invocation ≈ arbitrary code in-game). Don't raise the surface beyond localhost, and use
resomcp instead where the sandbox matters.
