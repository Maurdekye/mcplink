# Decompiler workflow — reading Resonite's source effectively

How to actually *use* a C# decompiler MCP server against Resonite once it's installed
(setup: [README §2, "Pair with a C# decompiler"](../../README.md#pair-with-a-c-decompiler)).
The other files in this folder are engine *facts*; this one is the *method* that produced them —
which assembly to open, how to find what you need without drowning in decompiled output, and how
to combine source reading with a live McpLink session.

## Which assembly holds what

The game's .NET assemblies sit **directly in the Resonite install folder** (Steam default:
`C:\Program Files (x86)\Steam\steamapps\common\Resonite\` — there is no `Resonite_Data\Managed`
subfolder; that's a Unity-era layout Resonite doesn't use). The ones that matter:

| Assembly | Contents |
|---|---|
| `FrooxEngine.dll` | The engine proper: `Slot`, every component, worlds, users, sync machinery. When in doubt, start here. |
| `Elements.Core.dll` | Math and data primitives: `float4x4`/`floatQ`/`float3`, `MathX`, `RefID`, colors. (Slot transforms are single-precision — verified here.) |
| `Elements.Assets.dll` | Asset-level types: `MeshX` (mesh data incl. bind poses), texture/audio asset classes. |
| `ProtoFlux.Core.dll` | The visual-scripting VM — **and the flow primitives (`If`, `For`, `While`, `Sequence`), which live here, not in the node DLLs**. |
| `ProtoFlux.Nodes.Core.dll` | Engine-independent node implementations (math, logic, strings). |
| `ProtoFlux.Nodes.FrooxEngine.dll` | Nodes that touch the engine (transforms, slots, assets, input). |
| `ProtoFluxBindings.dll` | Generated in-world binding components wrapping each node type. These are version-fragile generated types — resolve them at runtime, don't hardcode names. |
| `Libraries\ResoniteModLoader.dll`, `rml_libs\0Harmony.dll` | Modding infrastructure (if RML is installed). |

Two naming quirks to expect: ProtoFlux operators/math/casts are **type-monomorphized** — there is
no generic `Add<T>`, there's an `Add` per numeric type — and generic type names carry literal
angle brackets (`ValueInput<bool>`).

## The efficient loop

Decompiling whole types burns context fast; most questions need one method body or none at all.

1. **Discover** — `list_assembly_types` with a namespace filter to find candidate types, or
   `search_members_by_name` when you know the operation but not the owner (e.g. which type has
   `Inverse`? which node computes `SetGlobalTransformMatrix`?).
2. **Survey** — `get_type_members` for the full API surface (methods/properties/fields) without
   bodies. This alone answers most "what can I call / what fields exist" questions.
3. **Decompile last** — `decompile_method` for exactly the member whose behavior matters;
   `decompile_type` only for small types. `find_type_hierarchy` and `find_extension_methods`
   fill in inheritance and extension-method questions.

## Combining source with a live session (the high-value pattern)

Source tells you what the code *does*; the live world tells you what your objects *are*. The
pattern that answers engine-behavior questions neither answers alone:

1. **Ground it in source** — confirm the real API, types, precision, and edge-case handling with
   the decompiler instead of guessing from member names. Names lie; bodies don't.
2. **Inspect the live state with McpLink** — `find_components` to locate the thing,
   `get_protoflux_subgraph` (summary mode first) to read a flux graph, `get_component` /
   `find_referrers` to nail specific wiring, `describe_type` for a quick reflection surface
   without leaving McpLink at all.
3. **Verify behavior** — read the value actually flowing (`eval_output`), or make the smallest
   reversible change and observe. Static structure plus source usually predicts behavior, but a
   measurement settles what reasoning can't.

## Learn from installed mods

If ResoniteModLoader is installed, every DLL in `rml_mods\` is a working, decompilable example of
engine integration. When writing a mod (or an `eval` snippet) and unsure how to do something —
build UI, patch a method, marshal onto the update thread — find an installed mod that already
does it and decompile that, rather than deriving the idiom from scratch. Proven code beats
first-principles guessing at an undocumented engine.

## Caveats

- Findings are **build-pinned**: constants, private member names, and generated binding types
  drift across engine updates. Re-verify version-sensitive facts after the install updates
  (the notes in this folder each state the build they were verified against).
- Decompiled private/internal APIs are reachable from mods via Harmony/reflection but carry no
  stability promise — prefer public surface where one exists.
