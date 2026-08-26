# Resonite engine reference

Standing notes on how the Resonite engine actually behaves, grounded in decompiled
`FrooxEngine`/`Elements.Core`/`ProtoFlux` source (via a C# decompiler — see
[README §2, "Pair with a C# decompiler"](../../README.md#pair-with-a-c-decompiler)) and, where
noted, cross-checked live against a running session. They're facts about the engine, not about
McpLink — useful background for an agent (or a person) reasoning about *why* something in-game
behaves the way it does, independent of which MCP tools are in play.

Constants are pinned to the Resonite build noted in each file and can drift on engine updates;
treat them as "true as of that build," not as guarantees.

| File | Covers |
|---|---|
| [`data-model.md`](data-model.md) | Slot hierarchy/persistence/ordering/destroy, RefID layout, sync members (Sync/SyncRef/AssetRef/SyncObject), drives and links, dynamic variables, UIX/physics/data-feed/inventory |
| [`engine-internals.md`](engine-internals.md) | Component lifecycle ordering (OnAwake/OnInit/OnStart/OnChanges/destroy), immediate vs deferred change events, sync write and drive blocking, the update loop and World refresh stages, threading, coroutines/awaitables, physics/collider/grabbing |
| [`limits.md`](limits.md) | Hard limits and engine constants — session/user caps, undo, time/physics timestep caps, collider/locomotion clamps, texture/audio/OSC/cloud-variable limits |
| [`localization.md`](localization.md) | `LocaleString` as a struct (a bare string is not a key), silent in-band key-resolution failure, the additive locale-file fallback chain |
| [`networking-users.md`](networking-users.md) | RefID allocation/locality, the SyncController delta/full wire protocol, the lossy Streams channel, worlds and focus, the User component, permissions/roles/moderation, cloud variables, records |
| [`particles.md`](particles.md) | PhotonDust's CPU multithreaded sim, emitters/modules/renderers, ParticleStyle, capacity/defaults, building one in-world |
| [`persistence.md`](persistence.md) | The DataTree/BSON save format, SaveControl, RefID-to-GUID remapping, DependencyHandling, save hooks, Old-name/FeatureUpgrade type migration |
| [`protoflux.md`](protoflux.md) | Trampolined execution, MaxDepth 256, value vs action nodes, DataClass, the storage tiers, globals/GlobalRef, dynvar read cost, delay semantics, per-frame stage order, the node catalog |
| [`rendering-assets.md`](rendering-assets.md) | The Renderite/Awwdio split, asset load lifecycle, importing, texture/color encoding, MeshX, procedural mesh/asset generation, material families, MeshRenderer/SkinnedMeshRenderer, Light/Camera, audio |
| [`transforms-math.md`](transforms-math.md) | float4x4/floatQ single precision, coordinate system, TRS/decompose/inverse, the Slot space-transform pipeline, MathX quirks, transform-filter footguns |

Two companion guides in this folder are *method* rather than engine facts:

| File | Covers |
|---|---|
| [`decompiler-workflow.md`](decompiler-workflow.md) | How to read Resonite's source with a decompiler MCP server: which assembly holds what, the discover→survey→decompile-last loop, combining source with a live McpLink session, learning from installed mods |
| [`blender-asset-pipeline.md`](blender-asset-pipeline.md) | Preparing skinned assets in Blender for Resonite and exporting them back out: bind poses as ground truth, the four invisible rig defects, FBX export gotchas, why `export_skinned_gltf` exists |
