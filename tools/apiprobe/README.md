# apiprobe

Resolves every engine `MemberRef` a Resonite mod makes against the **current** game binaries and
reports the ones that no longer match. Use it after a game update to find out what broke, in which
mod, and how — before anyone has to reproduce it in-world.

**Written by `mod-updater` (agent seat, 2026-08-27)** during the `2026.8.27.1094` breakage, and
vendored here with their explicit permission so it doesn't rot in a retired seat's scratch space.
Essentially as written; only this README and a source header were added.

Needs `dotnet` and nothing else. It only ever reads files.

## Usage

```
# the useful one: resolve every mod's engine references against the installed engine
dotnet run -- "<install>\rml_mods" --resolve "<install>;<install>\Libraries;<install>\rml_libs"

# narrow probe: report the return type every mod expects from .Children
dotnet run -- "<install>\rml_mods"

# print the engine's own definition of a member
dotnet run -- <assembly> --def <Type> <Member>
```

`<install>` is typically
`C:\Program Files (x86)\Steam\steamapps\common\Resonite`.

## Reading the output

```
McpLink.dll                        CLEAN  (506 engine memberrefs checked)
ProtoFluxOverhaul.dll              1 PROBLEM(S)  (260 checked)
      SIG CHANGED FrooxEngine.Slot.get_Children()->SlimListEnumerableWrapper`1<Slot>
```

The quoted signature is **what the mod expects**, not what the engine now provides — a mismatch
against the current `MethodDef` is the finding. Three verdicts: `SIG CHANGED`, `MEMBER GONE`,
`TYPE GONE`.

⚠ **Read the `checked` count, not just the word `CLEAN`.** An assembly with no engine member
references reports `CLEAN (0 engine memberrefs checked)` — that means there was nothing to check,
not that it was checked and found sound. Two mods on this install do exactly that, and a count of
zero should never be read as a pass.

## Why not just grep for the changed type name

Because it is unsound in three ways, and the third is fatal:

1. It cannot tell a **definition** from a **use** — the engine's own `Elements.Core.dll` defines
   `SlimListEnumerableWrapper`, so it matches and looks "affected".
2. It cannot tell **which type** a member belongs to. `CustomInspectors` and `FastModelImport` both
   reference a `get_Children`, on `Elements.Core.DataTreeList` and `Assimp.Node` — neither is
   affected by anything here.
3. **It cannot see a break in which no type disappears.** Of the ten affected mods found on this
   install, three broke through a changed parameter list (`DebugManager.Box`, `MeshX.SetHasUV`,
   `SetHasUV_3D`, `SetHasUV_4D`) or an `IList`→`IReadOnlyList` swap on a different member
   (`CollectionsExtensions.FindIndex`). A type-disappearance screen finds **none** of them.

Resolving the decoded signature against the engine's `MethodDef` catches removals, return-type
changes and added parameters alike, without knowing in advance which API moved.

It also tells **fixed** from **broken** on an identical reference — which a MemberRef-presence
check cannot. A rebuilt mod still carries a `MemberRef` to `Slot.get_Children`, because it still
calls the property; its signature simply matches again, so it resolves `CLEAN`.

## Confirming the tool discriminates before you trust it

Run it against a **pre-update and a post-update build of the same mod**. The former should report
`SIG CHANGED`, the latter `CLEAN`. If both come back the same, the tool is not discriminating and
its verdicts mean nothing — a probe that can only ever say one thing is indistinguishable from a
working one.
