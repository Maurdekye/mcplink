# McpLink — using Resonite from Claude

This project has **McpLink** connected: an MCP server running *inside* the Resonite process
(`mcp__mcplink__*` tools, 97 of them). It gives you deep read/write access to the user's live
Resonite worlds: slots, components, ProtoFlux, assets, screenshots, C# eval. This guide is how
to use it well. The tool schemas describe arguments; this describes **craft and hazards**.

## Connection model

- The server lives in the game process. If registered via the bundled proxy, the `mcplink`
  server is always connected — but a tool call while Resonite is **closed** returns an
  `isError` result saying "Resonite is not running". That is not a failure of yours: tell the
  user to launch the game, then simply retry (no reconnect needed, even after game restarts).
- Every tool takes `world`: `"focused"` (default), `"userspace"`, or a world name — and
  `maxBytes` to cap result size (oversized results return a truncation notice;
  `get_protoflux_subgraph` degrades to its summary instead).
- **Registration broken? You can always go direct.** The server is plain HTTP on
  `localhost:7357/mcp`. If the `mcplink` MCP server isn't registered in your session — or its
  cached tool list has gone stale after a mod update — drive it with the bundled helper
  instead: `python tools/mcp.py <tool> '<json args>'` (`--list` enumerates the live tools;
  `from mcp import call` in a script). Same dispatcher, same tools, no client configuration —
  it needs only Python 3.8+ and the game running.

## Addressing & values

- **Addresses are real engine RefIDs** (`"ID1A2B00..."`), plus `"Root"` for the world root.
  They are the same ids in-game inspectors show and stay valid for the world's lifetime —
  but they **die with the world**: after a world reload, re-locate objects by name/path
  instead of reusing stored ids.
- `bookmark` gives a RefID a name; `"@name"` is then accepted anywhere an id argument is.
  Bookmarks are session-scoped. Use them for anything you'll touch more than twice.
- **`id` works as the primary-target argument on every single-element tool** (since v1.3) —
  legacy names (`rootId`, `targetId`, `operationId`, `slotId`, `target`, ...) remain accepted
  aliases; passing both an alias and its canonical name is an error.
- **Value encoding** (writes, args): typed literals
  `{"$type":"float3","value":{"x":0,"y":1,"z":0}}`, bare JSON coerced to the target type,
  `[x,y,z]` arrays for math structs, enums by name, references as `{"$ref":"ID..."}` (or a
  bare `"ID..."` string), `{"$new":"TypeName","args":[...]}` to construct arbitrary objects.
  Generic type names take **literal angle brackets**: `ValueField<float3>`.
  Discover members/types with `describe_type`; find types with `list_component_types`.

## Safety rules (non-negotiable)

- **The world is the user's.** Read freely; **confirm before mutating their creations**, and
  prefer the smallest reversible change. Building new things in a scratch area is fine.
- **Checkpoint before risky mutations**: `save_object` writes a full subtree snapshot to disk;
  `load_object` restores it. This is your disaster-recovery layer beyond the engine's
  **50-step undo cap**.
- All mutating tools are **undo-aware** (Ctrl+Z-able in-game; a whole `run_batch` is ONE undo
  step). Roll back your own mistakes with `undo`/`redo`; `history` previews the stacks.
  Opt out per call with `undoable:false` only for transient scaffolding.
- **Writing a driven field is a silent engine no-op.** Check for drives (component dumps flag
  `"drive":true`); don't fight a drive — change its source instead.
- **Userspace is load-bearing**: writes there can crash the whole engine (a userspace crash
  shuts Resonite down). Touch `world:"userspace"` read-only unless the user explicitly asks.
- **Blocking the update thread freezes the game.** Synchronous ProtoFlux loops run to
  completion in one frame (the engine watchdog force-aborts graphs after ~10 s); `eval` runs
  on the update thread with **no watchdog** — never eval an unbounded loop.

## Reading a world efficiently

An in-world ProtoFlux graph is ~90 % node-editor visuals; the logic is a small fraction.
Never dump whole hierarchies. The workflow:

1. **Locate** — `find_slots` (name/tag, also spatial via `near`+`radius`) or `find_components`
   (regex over slot name / component type) from `"Root"`; then scope tighter. `grep` searches
   **values** of all types, not just strings. These scans are chunked — no game hitch.
2. **Orient** — `get_slot`, `tree`, `ls`, `ls_components`, `stat` (what is this id?), `du`,
   `top` (where is the weight?), `bounds`, `get_slot_transform`.
3. **Read ProtoFlux** — `get_protoflux_subgraph` with `summary:true, depth:1` first
   (node-type histogram, entry roots, edge list, `flowTrace` execution walk). Constants are
   inlined into edges; relays/proxies/UI scaffolding are folded. Then `get_component`
   (`compact:true`) for exact wiring of specific nodes, `find_referrers` for "who consumes
   this output" (ignore `ProtoFluxNodeVisual`/`*Proxy` referrers — the logic referrer is the
   real one).
   - Logic nodes are usually the **immediate children** of a group slot (`depth:1`), but
     *unpacked* packaged networks can nest the real node container a few slots deeper — if a
     subgraph comes back empty/sparse, find the slot that directly parents the nodes, or bump
     `depth` to 2–3.
   - Multi-output nodes expose named output members (`ReadDynamicValueVariable` →
     `FoundValue`/`Value`; transform reads → `LocalPosition`/…). Single-output value nodes ARE
     their own output — wire/reference the component id itself.
   - Per-item gadget data usually lives in **dynamic variables**: `dynvar_space` inventories a
     space (including phantom reads and misbound declarations), `dynvar_users` answers who
     declares/drives/reads/writes a name, `impulse_map` tables the dynamic-impulse RPC surface.
4. **Live values** — `eval_output` reads the current value flowing through a member. Stored
   fields read directly; purely computed pins (pure value nodes, LocalValue, multi-output
   members) are evaluated through the group's own runtime — no probe objects, no world change.
   Static wiring ≠ runtime behavior; evaluate when structure can't prove it. `flux_ports` lists
   a node's full port surface (inputs/impulses/references/globalRefs + its outputs/operations)
   with current targets — the "what can I wire here" discovery call.
5. **Offline analysis** — `tar` exports a subtree as JSON to disk (`chunked:true` for whole
   worlds without hitching; `includeBounds:true` embeds world-space bounds). Grep/script the
   file instead of paging through tool calls.
6. **Deep/private state** — `get_component includeNonSynced:true`, `reflect_get` (any field or
   property, private included), `env` for engine/world globals.

## Building & mutating

- Singles: `add_slot`, `attach_component`, `set_member`, `update_slot`, `update_component`,
  `destroy` (`rm`), `cp` (real `Slot.Duplicate` — ProtoFlux wires remap correctly),
  `mv` (reparent/rename; keeps global transform by default, unlike `update_slot` parenting).
- `edit_list` edits sync-lists (`MeshRenderer.Materials`, variadic ProtoFlux inputs):
  add/insert/set/remove/move/clear, or `values` wholesale replace.
- **`run_batch`** applies a JSON array of ops in ONE atomic update tick; later ops reference
  earlier results via `"$N.path"`. Check each op's response — batch success ≠ every op succeeded.
- **`bulk_build`** creates thousands of slots/components/cross-refs (`"@id"` placeholders) in
  one tick — use it for mass creation instead of looping single calls (the standard import
  path costs ~1.5 frames per object; this bypasses that).
- **ProtoFlux**: `flux_build`/`flux_connect` use the engine's type-checked connect APIs — they
  cannot produce broken graphs; prefer them over hand-wiring references. Node specs take
  `globals:{"VariableName": value}` (GlobalRef members set via the engine's GlobalValue idiom)
  and `near:"<id>"` (auto-place beside an existing node, free-spot scan). `flux_connect
  disconnect:true` severs a port (list elements like `Calls[2]` included); `flux_splice`
  inserts a node into an existing impulse/data wire in one undo batch. `fire` edge-triggers an
  impulse (the "call this manual entry point" tool) and reports execution feedback (fired?,
  group impulse-flow error, error log lines during settle). `sed` does pattern rewrites
  (**dry-run by default** — inspect before applying). `xargs` = find + apply any tool per
  match (`$id`/`$slotId`/`$name` templates), one undo batch; `dryRun:true` first.
- `call_method` invokes anything with full argument construction (plain-class params, optional
  defaults, generics, out-params).

## Observing & debugging

- `logs` — the engine log from startup (level/regex filter, `sinceSeq` incremental polling).
  The first place to look when something misbehaves silently.
- `watch_changes` / `changes` / `unwatch` — event subscriptions on a slot/component/field,
  coalesced per (element, member, kind) with counts; `changes waitMs` long-polls: "fire, then
  see what happened" in one round trip.
- `impulse_watch` / `impulse_events` / `impulse_unwatch` — live ProtoFlux execution traces at
  node-group granularity plus the dynamic-impulse bus (untyped sends). Harmony-patched ONLY
  while a watch is active. The group map is a snapshot — re-watch after graph edits.
- `dynamic_impulse` sends a real dynamic impulse (typed payload supported) — with
  `impulse_map`, every in-world gadget's RPC surface is directly callable.
- `diff` — structural diff of two subtrees, **reference-remap aware** (refs inside each
  subtree compare by relative path, so a copy diffs clean against its original). Compose with
  `load_object` for checkpoint-vs-live.
- `watch` (polling) for simple value-settle checks; `perf` for per-world frame delta.

## Communicating with the user in-world

- **`spawn_markdown` is THE way to deliver findings/reports in-game** — markdown in, a
  grabbable, scrollable RadiantUI panel out, placed in front of the user (`inFrontOf` targets
  someone else; `position`+`lookAt` for explicit placement). Keep the returned RefID and pass
  it as `replaceId` to update the panel in place instead of spawning a second one. Don't
  hand-build TextRenderer reports.
- `marker` (temporary labeled sphere, self-destroys) shows the user what you mean;
  `notify` toasts their dash (visible in VR — completion pings for long tasks);
  `jump_user` teleports them to what you built.
- `user_pointer` = what they're pointing at / grabbing / holding — "point at it and I'll look"
  object designation. `user_avatar` = what they look like and are wearing/carrying. `users`
  lists presence and head positions.
- **Seeing the world**: `render_view` renders a screenshot from any pose, `cameraId` (uses a
  Camera component's full settings), or `user` (their head view); `orbit_render` walks around
  a target. ⚠ A camera placed exactly at a user's head position renders *inside their avatar*
  (black/blocked frame) — offset the camera between the user and the subject.
- `raycast` (physics ray, all hits sorted) and `view_scan` (rendered-bounds view cone — finds
  things rays can't hit) answer "what is at/near where I'm looking"; `mesh_info` for mesh
  stats and broken-mesh detection.

## Assets, items, packages

- `import_file` (file → asset URL), `spawn_import` (full import pipeline at a position),
  `export_asset` (asset URL → file; cloud assets download) — round-trip content through
  external editors.
- `export_package` / `import_package` — `.resonitepackage` round-trip, the game's own
  portable item format (object graph + all referenced assets in one file, drag-and-drop
  compatible). The share/backup layer above `save_object` (which stores asset URLs only).
- `inventory` browses cloud inventory; `spawn_object` spawns records (`resrec://...`) or
  asset URIs. `find_assets` inventories every asset a subtree uses.

## `eval` — the escape hatch

`eval` runs C# against the live engine when no structured tool fits. Globals: `world`,
`engine`, `resolve("ID...")`, `log(x)`, `vars` (persists across calls); `await` supported;
the final expression is the result. Compiles off-thread, **executes on the update thread** —
no watchdog, so no unbounded loops, no blocking waits. Prefer structured tools when they
exist: they validate, undo, and can't freeze the game. (Requires the optional
`McpLink_libs` install; a "companion not found" error means it isn't installed. If `eval`
throws `InvalidCastException` about `EvalGlobals` after a `hot_reload`, the eval context is
stale until the game restarts — route around with structured tools.)

## Engine footguns (they will bite)

- **Transform writes self-sanitize silently**: NaN/Inf position → `Zero`, bad scale → `One`,
  and a **non-near-unit quaternion rotation is reset to Identity** — always write normalized
  quaternions. Transforms are single-precision; world is left-handed, Y-up, +Z forward, meters.
- **Driven field writes are silent no-ops** (see Safety). One drive per field.
- **RefIDs are never persisted**: saving remaps them, and references whose target is outside
  the saved hierarchy are **nulled** — the usual cause of broken refs after a partial
  save/paste. `save_object`/`export_package` a subtree that actually contains what it points at.
- Undo = 50 steps. `run_batch`/`xargs` = one step each — use them for multi-part changes.
- Slot names can contain rich-text markup; name payloads are `{value,id}` shapes (tools offer
  `stripRichText`/`flattenNames`).
- A driven-but-unhooked transform makes an object ungrabbable; `MeshCollider` is static-only.

## Quick tool index

| Need | Tools |
|---|---|
| Orient | `session_info` `get_slot` `tree` `ls` `ls_components` `stat` `du` `top` `bounds` `get_slot_transform` |
| Find | `find_slots` `find_components` `grep` `find_referrers` `raycast` `view_scan` |
| ProtoFlux | `get_protoflux_subgraph` `impulse_map` `flux_ports` `eval_output` `fire` `flux_build` `flux_connect` `flux_splice` |
| Deep read | `get_component` `reflect_get` `dynvar_space` `dynvar_users` `env` `describe_type` `list_component_types` `mesh_info` `find_assets` |
| Write | `set_member` `update_slot` `update_component` `add_slot` `attach_component` `destroy` `cp` `mv` `sed` `edit_list` `reflect_set` `call_method` |
| Bulk/atomic | `run_batch` `bulk_build` `xargs` |
| Observe | `logs` `watch_changes`/`changes`/`unwatch` `impulse_watch`/`impulse_events` `watch` `perf` `diff` |
| Recover | `save_object` `load_object` `undo` `redo` `history` |
| User-facing | `spawn_markdown` `marker` `notify` `jump_user` `user_pointer` `user_avatar` `users` `render_view` `orbit_render` |
| Assets | `import_file` `spawn_import` `spawn_object` `export_asset` `export_package` `import_package` `inventory` |
| Interact | `dynamic_impulse` `focus_world` `bookmark`/`bookmarks` `at`/`jobs`/`cancel_job` |
| Escape hatch | `eval` |

Aliases: `rm`→destroy, `cat`→get_component, `ps`→perf, `schedule`→at.

**Default working style**: read with summaries and shallow depth; ground claims in what you
actually read (decompile/probe rather than guess); mutate only with a clear OK, atomically,
undoably, checkpointing first when the target is the user's own creation.
