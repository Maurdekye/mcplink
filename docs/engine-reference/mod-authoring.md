# Writing Resonite mods (ResoniteModLoader + Harmony)

Patterns for authoring an RML mod in C#. McpLink is itself an RML mod, so this repo doubles as a
worked example — where a claim below is about project setup, [`McpLink.csproj`](../../McpLink.csproj)
is the living reference. Every API name and signature in this file was re-verified against a live
install (Resonite build 2026.8.26.1047, ResoniteModLoader with Harmony 2.4.2.0) with a decompiler
at the time of writing; conventions and judgment calls are marked as such. Names can drift with
engine updates — when something doesn't resolve, check the source per
[`decompiler-workflow.md`](decompiler-workflow.md).

## Project setup

Target `net10.0`. Reference the DLLs straight out of the install with `<Private>false</Private>`
so your build output contains only your own mod: `FrooxEngine.dll`, `Elements.Core.dll`,
`Libraries\ResoniteModLoader.dll`, and `rml_libs\0Harmony.dll` — **matching the install's Harmony
version exactly rather than bundling your own** (convention, but a strong one: RML loads one
Harmony for everyone). The mod DLL goes to `rml_mods\`; RML loads mods once at engine startup, so
a rebuilt DLL takes a game restart to load. A word of caution from this repo's own history: an
MSBuild copy-into-`rml_mods` step fails **silently** while the game holds the file lock — treat
deploying as a decision, not a build side effect (see the deploy gate in `McpLink.csproj`).

## Skeleton and lifecycle

```csharp
public class MyMod : ResoniteMod {
    public override string Name => "MyMod";
    public override string Author => "you";
    public override string Version => "1.0.0";   // Link is optional

    public override void OnEngineInit() {
        new Harmony("com.you.MyMod").PatchAll();          // only if you patch
        Engine.Current.RunPostInit(() => { /* register menu entries etc. */ });
    }
}
```

`OnEngineInit` is the **only** lifecycle hook RML gives you, and it runs during engine startup —
no world exists yet and you are not on any world's update thread. Do registration only; touch no
Slot/component/field state from it. `Engine.RunPostInit(Action)` defers work to after engine
initialization.

Logging is static on `ResoniteMod`: `Msg` / `Debug` / `Warn` / `Error` (each takes `object` or
`object[]`), plus `IsDebugEnabled()` and `DebugFunc(Func<object>)` to skip building expensive
debug strings.

## Configuration

Declare keys as fields and let RML pick them up:

```csharp
[AutoRegisterConfigKey]
static readonly ModConfigurationKey<bool> ENABLED = new("enabled", "Turn it on", () => true);
```

Read with `GetConfiguration().GetValue(KEY)` / `TryGetValue`, write with
`Set(key, value, eventLabel)`. For manual control, override
`DefineConfiguration(ModConfigurationDefinitionBuilder builder)` and chain
`builder.Version(...).Key(...).AutoSave(bool)`. Subscribe `Config.OnThisConfigurationChanged` for
live changes — and note the handler is **not** guaranteed to run on a world thread (marshal before
touching world state; see Threading). Users edit config in-game with the community
**ResoniteModSettings** mod, so wire `OnThisConfigurationChanged` rather than assuming a restart.

## Harmony patterns

Standard Harmony 2.x semantics apply (see the
[Harmony documentation](https://harmony.pardeike.net/)); the patterns that carry the most weight
in engine modding:

- Attribute style: `[HarmonyPatch(typeof(T), "Method")]` on a class with `static bool Prefix(...)`
  / `static void Postfix(...)`. A `Prefix` returning `false` suppresses the original.
- Read a private field via the `___name` parameter convention:
  `static void Postfix(T __instance, SyncRef<X> ____privateField)`.
- Call a private/internal engine method with a reverse patch:
  `[HarmonyReversePatch][HarmonyPatch(typeof(T), "PrivateMethod")] static R Stub(T i, ...) =>
  throw new NotImplementedException();`.
- Per-instance state without leaking engine objects:
  `static readonly ConditionalWeakTable<EngineType, MyData> _data` +
  `_data.GetOrCreateValue(__instance)`.
- Toggleable features: `[HarmonyPatchCategory("X")]` on the patch class, then
  `harmony.PatchCategory(assembly, "X")` only when the config says so, and
  `harmony.PatchAllUncategorized(assembly)` for the always-on rest.
- Prefer patching **public instance methods that already run on the world's update thread**
  (most engine and UIX event paths do) — it sidesteps the marshaling problem below. Other mods
  may patch the same methods; additive Postfixes coexist better than competing Prefix returns.

## Threading — the #1 hazard

All world mutation (Slot/component/field writes, `AddSlot`, `AttachComponent`) must happen on
that world's update thread. Harmony patches on engine update-path methods and
`Button.LocalPressed` handlers are already there; config-change, input, network, and async
continuations may not be. Marshal with:

- `World.RunSynchronously(...)`, or better `Slot.RunSynchronously(action, immediatellyIfPossible)`
  / `ComponentBase.RunSynchronously(...)` — the Slot/Component overloads drop the action if their
  object was destroyed first. (`RunSynchronouslyAsync` returns an awaitable `Task`. And yes, the
  engine's parameter really is spelled `immediatellyIfPossible`.)
- Defer by frames or time: `RunInUpdates(n, action)`, `RunInSeconds(seconds, action)` — on
  `Slot`, `ComponentBase`, and `World` alike.
- In async code: `await default(ToWorld);` hops onto the world thread, `await new Updates(n);`
  waits n update cycles (both are awaitable structs in `FrooxEngine`).
- Gate checks when unsure: `World.CanMakeSynchronousChanges` / `World.CanCurrentThreadModify`.

## Undo and persistence

The undo API lives as extension methods in the `FrooxEngine.Undo` namespace:

- Batch an operation: `world.BeginUndoBatch(description)` … `world.EndUndoBatch()`
  (`SetActiveUndoBatch(world, batch)` to resume one).
- Record changes so the user can undo them: `field.UndoableSet(value)` (on `IField<T>` /
  `SyncRef<T>` / `AssetRef<T>`) or `field.CreateUndoPoint()` before you write; spawns/destroys
  via `slot.CreateSpawnUndoPoint(description)` and `slot.UndoableDestroy()`. As of this build
  there is no move-specific helper — record a transform undo point yourself.
- Don't fight drives: skip writes where `IField.IsDriven` — a driven field silently ignores
  writes (details in [`data-model.md`](data-model.md)).
- Mark transient slots `PersistentSelf = false` so they never save; use `Slot.Tag` to recognize
  slots your mod created (idempotency on re-run — convention, works well). For persisted types
  and renames, see the migration attributes in [`persistence.md`](persistence.md).

## In-world UI

A floating panel in three calls, then compose with `UIBuilder`:

```csharp
UIBuilder ui = RadiantUI_Panel.SetupPanel(slot, "Title", new float2(800, 600),
                                          pinButton: true, closeButton: true);
RadiantUI_Constants.SetupEditorStyle(ui, extraPadding: false);
ui.VerticalLayout(4f); ui.Text("Hello"); var btn = ui.Button("Do it");
```

- Wire buttons to mod code through the **local** event: `btn.LocalPressed += (IButton b,
  ButtonEventData d) => { ... }` — it runs on the world thread. (The synced `Pressed` delegate
  needs a method on a `Worker` in the data model; `LocalPressed` is the mod-friendly path.)
- Auto-generate a field editor for any sync member:
  `SyncMemberEditorBuilder.Build(member, "label", null, ui, labelSize)` — for an `ISyncRef` this
  yields a droppable reference editor. Back UI state with `ValueField<T>` / `ReferenceField<T>`
  components on a slot.
- Parent local-only panels under `world.LocalUserSpace` and pose them with
  `SlotPositioning.PositionInFrontOfUser(slot, faceDirection, offset, distance, user, scale,
  checkOcclusion, preserveUp)`.
- A no-Harmony launcher: `DevCreateNewForm.AddAction("Category", "Name", (Slot s) => { ... })`
  adds an entry to the Dash's Create New menu; the host hands you a positioned slot on the
  update thread.

## Manipulating ProtoFlux from a mod

The in-world node is `FrooxEngine.ProtoFlux.ProtoFluxNode` (a component). Wire data with
`node.TryConnectInput(inputRef, output, allowExplicitCast, undoable)` (impulse/reference
equivalents exist on the same type and on `ProtoFluxNodeGroup`). Resolve helper/binding types at
runtime — `ProtoFluxHelper.GetRelayNode(inputType)` for a relay that fits a data type,
`ProtoFluxHelper.GetBindingForNode(nodeType)` for the binding component wrapping a runtime node
type — because the generated types in `ProtoFluxBindings.dll` are version-fragile and hardcoded
names rot. A node works headless (no visual); create the UI on demand with
`ProtoFluxVisualHelper.EnsureVisual(node)`, and reposition a node by moving its slot (the visual
lives on a `<NODE_UI>` child slot — leave that one alone; sizing constants are public statics on
`ProtoFluxNodeVisual`).

## When stuck

Decompile a mod that already does what you want — installed `rml_mods\*.dll` files are working
templates for proven idioms (`decompiler-workflow.md`, "Learn from installed mods"). For the
engine's own behavior — lifecycle ordering, sync/drive rules, save format — the other files in
this folder cover it; verify against source rather than guessing from names.
