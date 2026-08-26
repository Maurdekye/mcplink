# Resonite ProtoFlux Runtime

> Resonite ProtoFlux runtime and node-catalog reference (ILSpy-verified) — trampolined execution, MaxDepth 256, synchronous loops freeze the thread, value vs action nodes, DataClass, the storage tiers (Local/Store/Global/Data-Model-Store), globals & GlobalRef config ports, dynvar read cost, delay semantics, per-frame stage order, and the monomorphized node catalog.

### ProtoFlux runtime & execution model (for grounding behavior in source)

The control-flow/runtime core lives in **`ProtoFlux.Core.dll`**, not the node catalogs: the
fundamental nodes `If`, `For`, `While`, `Sequence`, `RangeLoopInt` and their async variants
(`AsyncFor`/`AsyncWhile`/`AsyncSequence`/`AsyncRangeLoopInt`) are concrete classes in
`ProtoFlux.Runtimes.Execution.Nodes` (`ProtoFlux.Core.dll`). `ProtoFlux.Nodes.Core.dll` and
`ProtoFlux.Nodes.FrooxEngine.dll` have **no** Flow namespace — searching them for these returns
nothing. To ground a flow node, decompile `ProtoFlux.Core.dll`.

**Execution = trampolining, not recursion.** Action/flow nodes *return* their next continuation
instead of calling it: `ActionNode<C>.Run` returns an `IOperation` the runtime then executes
(`If.Run` → `OnTrue.Target`/`OnFalse.Target` from `Condition.Evaluate`); `AsyncActionFlowNode<C>.RunAsync`
does `await Do(context); return Next.Target`. This keeps simple chains off the stack.
- `Sequence.Run` executes `Calls[0..n-2]` **inline/synchronously in order** via `GetImpulse(i).Execute`,
  then **returns the last** `Calls[n-1]` as its own continuation (tail position); empty list → null
  (`Sequence.Run`).

**Recursion guard — hard `MaxDepth`, throws `StackOverflowException`.** `ExecutionContext.EnterExecution()`
does `if (++CurrentDepth == MaxDepth) throw new StackOverflowException("ProtoFlux execution flow
reached maximum depth of {CurrentDepth}")`. `MaxDepth` defaults to **256**, `AutoYieldSafetyDepth` to
**128** (concrete values set by the FrooxEngine runtime) (`ExecutionContext.MaxDepth`/`.EnterExecution`).
This caps **synchronous continuation nesting** (recursive ExternalCall / nested-node calls), NOT
per-iteration loop counts. Async chains escape it: `TryEnterAsyncExecution` does `await Task.Yield()`
when `InheritedDepth>0 && CurrentDepth >= MaxDepth - AutoYieldSafetyDepth` (≥128), after
`SubtractInheritedDepth()` — sync chains get no relief and will overflow
(`ExecutionContext.TryEnterAsyncExecution`). Frame array `_frames` is fixed `new StackFrame[1024]`
(not resized); value/object data stacks grow per-frame from `runtime.TotalValue/ObjectStackSize`.

**Loops freeze the frame — the canonical footgun.** `For.Run`/`While.Run` run a **plain C# loop in a
single `Run()` call**: no per-iteration yield, **no iteration cap** (`For` never bounds `Count.Evaluate`).
The only break is `if (context.AbortExecution) throw new ExecutionAbortedException(...)` checked each
pass, and `AbortExecution` is set externally (e.g. `ProtoFluxController.AbortAllContexts`). So a huge
`For`/infinite `While` blocks the whole impulse and the update thread until done or aborted. The
**Async** variants await each iteration and can yield. (`For` reverse path counts `num-1→0` and forward
writes `Iteration=0` after finishing.) (`For.Run`/`While.Run`)

**Value vs action nodes.** Value outputs are **pulled lazily**: `ValueFunctionNode<C,T>.Compute(C)` runs
on demand when an input evaluates it (`ValueInput.Evaluate`), not cached/pushed. Action nodes can't be
evaluated: `ActionNode<C>.CanBeEvaluated => false`, `Evaluate` throws
`NotSupportedException("Evaluation is not supported for action nodes.")` (`For` also overrides
`CanBeEvaluated => false`).

**`DataClass` is binary: `Value` (unmanaged) vs `Object`** — splits the whole runtime into parallel
`ValueStack`/`ObjectStack`, `Value/ObjectInput`, `Value/ObjectOutput`, `StoredValue<T>`/`StoredObject`,
`ReadValue`/`ReadObject`. All value-side generics are `where T : unmanaged` (`ValueWrite<T>`,
`StoredValue<T>`, `LocalValue<T>`, `ValueLocal<T>`, `ValueRelay<T>`, latches) and live in a packed byte
stack via `Unsafe.Read/WriteUnaligned`; managed payloads must go through the Object class
(`DataClass`/`ExecutionContext.ReadValue/ReadObject`).

**Variable storage — the four in-graph tiers (ILSpy-verified 2026-07-25).**
- **Local** (`LocalValue<T>`/`LocalObject`): `ValueLocal<T>`/`ObjectLocal` — a bare `int offset` into the
  execution context's value/object stack, **transient, valid only within the current frame/run**; per
  client (`StoredValue`/`LocalValue`/`ExecutionContext.WriteStoredValue`).
- **Store** (`StoredValue<T>`/`StoredObject`): **persists across executions** via `ValueStore<T>`/
  `ObjectStore` reading `SharedExecutionScope.Values/ObjectsStore` at the frame's store offset (allocated
  only when `runtime.RequiresScopeData`). Still **client-local** (each client's runtime owns its arrays),
  never synced/saved, and **resets to default on any group rebuild** (graph edit, pack/unpack, reload).
- **Global**: a named cell registered per `NodeRuntime` — but the *storage is the data model*:
  `FrooxEngine.ProtoFlux.GlobalValue<T> : Component` (one `Sync<T> Value`) registers itself via
  `SetupProxyManager` → `runtime.AddGlobal<T>(Slot.Name)` (global's name = the component's slot name), and
  `ScopePoint._mappedGlobals[index]` holds **the component itself** (`IGlobalValue`). `ReadGlobal` returns
  its synced field; `WriteGlobal` calls `SetValue` → a real synced+persistent field write
  (`GlobalRefHelper`, `ScopePoint.ReadGlobal/WriteGlobal`). ⚠️ `GlobalValue.SetValue` does **not** check
  drives — writing a driven global is the engine's silent no-op. Changes are **push**: proxy
  `OnValueChanged` → `GlobalProxyManager` marks the group's globals dirty → `runtime.UpdateGlobal` →
  `GlobalChanged` fires on every listening node's `GlobalRef` (`Global<T>.ValueChanged`). One `GlobalValue`
  component can be shared by many nodes and mapped into several groups (a `GlobalRef` may only target a
  global of its own runtime — the shared *component* bridges groups via per-group proxy managers).
- **Data Model Store** (`DataModelValueFieldStore<T>` + `DataModelObject{Field,Ref,AssetRef}Store`,
  `DataModelUserRefStore`, `DataModelTypeStore` — `ProtoFlux.Nodes.FrooxEngine.dll::…Variables`): value in
  a nested `Store : ProtoFluxEngineProxy` (`Sync<T>` under the node) — synced+saved, `[ChangeSource]`, and
  its `Write` **checks `IsBlockedByDrive` and returns false** (the drive-safe writable cell).

Decision rule: private graph variable → Data Model Store; object/world data others consume →
`ValueField`/field on the owning component (reach via Source); name-addressed cross-hierarchy contract →
dynamic variable; **GlobalRef config port → global (nothing else fits)**. As *pure storage* a global has no
edge over a DM store — identical `Sync<T>` machinery; perf across all synced cells is flat. Only
Local/Store skip sync + replication entirely (the fast tier for per-frame scratch); every synced-cell
write pays delta encode + send per user + change dispatch.

**Config ports (GlobalRef ports) — the non-wire sockets on nodes.** A config port is a `GlobalRef` member
on the runtime node (`SyncRef<IGlobalValueProxy<T>>` on the binding): it accepts only a **proxy
component**, never a wire — for bind-time values that must exist outside execution and push changes.
Exactly three proxy families implement `IGlobalValueProxy` (`FrooxEngine.ProtoFlux`): **`GlobalValue<T>`**
(inline value), **`GlobalReference<T>`** (element/field/slot/user ref), **`GlobalDelegate`** (method ref).
Verified ports: `DynamicVariableInput<T>.VariableName : GlobalRef` (rebinds via `OnVariableNameChanged`);
`ValueSource<T>.Source : GlobalRef` (the Source node's field cell — the `FrooxEngine.ProtoFlux.CoreNodes`
Source family `SlotSource`/`UserRefSource`/`ElementSource`/… matches); `GlobalToValueOutput<T>.Global :
GlobalRef<T>` ("Global To Output", Core — an `IVariable`, so a Write node can target it and the write
lands in the synced field). Contrast `ValueInput<T>`: its editable box is a plain `Sync<T>` pushed to the
runtime on change (`OnChanges` → `TypedNodeInstance.SetValue`) — same synced/saved/drivable guarantees as
a global, but it's `IInput` (a read-only literal, not Write-targetable) and can never sit in a config port.

**Three non-wire attachment mechanisms on a node** (all inspectable on the node's slot):
① **GlobalRef config ports** → `GlobalValue`/`GlobalReference`/`GlobalDelegate` components;
② **engine proxies** — nested `ProtoFluxEngineProxy` components holding live engine objects:
`FieldDriveBase<T>.Proxy` carries a real `FieldDrive<T>` (the Drive node's target is a proxy, **not** a
global port; `FieldHookBase.Target` is a plain wired `ObjectInput`), `DataModelValueFieldStore.Store`,
`DynamicVariableInputProxy`; ③ **plain sync members on the binding** (`ValueInput<T>.Value`).
`DynamicVariableInput` uses ① for its name and ② for the variable link.

**Dynamic-variable read cost — bound vs wired.** `ReadDynamicVariable<T>.ComputeOutputs` re-resolves on
**every evaluation**: `DynamicVariableHelper.ParsePath` (string parse) → `slot.FindSpace(spaceName)`
(hierarchy walk) → `space.TryReadValue` (dict lookup) — and `FoundValue` is `[ContinuouslyChanging]`, so
watchers re-pull every frame. `DynamicVariableValueInput<T>` instead binds once through its proxy + name
global and reads a cached link, re-resolving only on name change. Hot paths ∴ bound input flavor ≫ wired
Read DynVar; and for continuously-changing synced values prefer a **drive** (computed per client, zero
replication) over per-frame writes into any synced cell.

**Time delay semantics.** `DelayTime.Do` starts `Task.Delay(duration)`, runs `BeforeDelay`, then
`await OnTriggered.ExecuteAsync` (the OnTriggered impulse fires **immediately on entry**), then
`await delayTask`. Because `AsyncActionFlowNode` returns `Next.Target` only after `Do()` completes, the
node's **`Next` continuation fires only after the timer elapses** (`DelayTime.Do`). Two delay families:
seconds/timespan (`DelayTime`/`DelayTimeSpan`/`DelaySeconds{Double,Float,Int}`, `ProtoFlux.Core.dll`) vs
engine-update-based `FrooxEngine.Async.DelayUpdates` + `DelayUpdatesOrSeconds*`/`DelayUpdatesOrTime*`
(wait N updates, or whichever of updates-vs-time elapses first) (`ProtoFlux.Nodes.FrooxEngine.dll`).

**Per-frame stage order in the World refresh cycle:** `World.RefreshStage` orders the ProtoFlux phases
`ProtoFluxRebuild → ProtoFluxEvents → ProtoFluxUpdates → ProtoFluxContinuousChanges →
ProtoFluxDiscreteChangesPre → ProtoFluxDiscreteChangesPost` — i.e. groups rebuild, then queued events,
then update-nodes, then continuous, then discrete (pre/post). `ProtoFluxController` exposes matching
`Rebuild`/`RunNodeEvents`/`RunNodeUpdates`/`RunContinuousChanges`/`RunDiscreteChanges`
(`World.RefreshStage`/`ProtoFluxController`).

**`NodeContextPath`** — readonly struct over `INode[]` (the `IExecutionNestedNode` chain) identifying a
nested-node execution path; zero-length normalizes to null = `(Root)`. Supports `Nest` (prepend),
element-wise `Equals`, `CompareTo` (by `PathLength` then per-node `IndexInGroup`),
`FindSharedRootLength`; built by `ExecutionContext.CaptureContextPath()` walking current frames. This is
how the runtime distinguishes the same node instantiated inside different nested-node invocations.

**Static loop detection.** A self-feeding **continuation** wire is a build/rebuild-time error:
`ImpulseLoopError : ImpulseValidationError` ("Continuation Loop detected at impulse {name} on node
{node}", carries `Node`/`ImpulseIndex`/`IsAsync`); last error at `ProtoFluxNodeGroup.LastImpulseFlowError`.
Distinct from the legitimate runtime loop nodes `While`/`For`, which use **Call** impulses, not
Continuations.

**External pull-evaluation (note the misspelling).** `ProtoFluxNodeGroup.EvaluateImmediatelly<T>(input,
NodeContextPath)` borrows a `FrooxEngineContext`, pins a stack frame, `MoveToContext` to the path, calls
`input.Evaluate`, unwinds, returns the context to the pool — the supported way to read a flux value
outside normal execution. Also `ExecuteImmediatelly(path, Action)` / `ExecuteImmediatellyAsync(path,
Func)`. (Param name is literally `Immediatelly`.)

**"Packed" = no active visual.** `ProtoFluxVisualHelper.IsPacked(node)` is literally
`return !node.HasActiveVisual();` — a packed network is one whose `ProtoFluxNode`s have no rendered
`<NODE_UI>`, not a separate storage format; `EnsureVisual()` un-packs.

### ProtoFlux node catalog notes

- **Operators/Math/Casts are type-monomorphized** — no single generic `Add`. `Operators` has ~921
  classes (`Add_Double_Double2`, `Add_Color32_Byte`, `Add_Float3_Float`, …), `Math` ~758
  (`Acos_Float4`, `Atan2_Double2_Double`), `Casts` ~507 explicit pairs (`Cast_byte2_To_double2`, …) —
  one type per signature (`ProtoFlux.Nodes.Core.dll::Nodes.{Operators,Math,Casts}`). The picker shows
  one logical node with overloads (`NodeOverload*` in `ProtoFlux.Core`); backing types are distinct.
- **Scaffolding nodes the subgraph tool folds live in `ProtoFlux.Core.dll`** (`...Execution.Nodes`):
  relays `Value/Object/Call/AsyncCall/ContinuationRelay`, `ContinuouslyChangingValue/ObjectRelay`; casts
  `ValueCast`/`ObjectCast`/`ValueToObjectCast`/`NullableToObjectCast`; `Value/ObjectConditional`;
  multiplexers `Value/ObjectMultiplex`, `ImpulseMultiplexer/Demultiplexer`, `Value/ObjectDemultiplex`;
  null handling `IsNull`/`NotNull`/`NullCoalesce`/`MultiNullCoalesce`/`Pack/UnpackNullable`; `Box`/`Unbox`;
  `Link`; `Value/ObjectWrite` + `*WriteLatch`/`*IndirectWrite`; `GetType`.
- **Network nodes are host-access gated.** `FrooxEngine.Network` has `GET_String`/`POST_String`,
  `WebRequestBase`/`StringResponseWebRequest`, and a Websocket family (`WebsocketConnect`,
  `WebsocketTextMessageSender/Receiver`, `Websocket{Events,ConnectionEvents}`); access requires the
  explicit `RequestHostAccess`/`RequestHostAccessUrl` flow gated by `IsHostAccessAllowed{,Url}` — not
  just calling GET/POST.
- **In-graph self-modification.** `FrooxEngine.Nodes` has `PackProtoFluxNodes`, `PackProtoFluxInPlace`,
  `PackProtoFluxFromNode`, `UnpackProtoFlux` — graphs can pack/unpack graphs at runtime (the in-world
  equivalent of Moduprint packing).
- **Bindings namespace pattern (ILSpy nav):** `ProtoFluxBindings.dll` prepends `FrooxEngine.` to the
  runtime node's namespace — `ProtoFlux.Runtimes.Execution.Nodes.*` → binding
  `FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.*`; FrooxEngine.dll's own
  `FrooxEngine.ProtoFlux.CoreNodes.*` → doubled `FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes.*`. Generic
  types need backtick arity for ilspy-mcp (`GlobalValue`1`).
- **`EngineReady` dynamic impulse fires once at startup** on every world's `RootSlot`:
  `Engine.HandleEngineReadySignal` (each `BeginUpdate`) decrements `AutoReadyAfterUpdates`, and at 0 runs
  `ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(world.RootSlot, "EngineReady",
  excludeDisabled:false)` for every world — graphs can key once-after-startup logic off an `EngineReady`
  dynamic-impulse tag (`AutoReadyAfterUpdates<0` disables auto-ready).

**Additional `ProtoFluxNodeVisual` consts** (beyond the size set already noted): `NODE_SCALE=1.25f`,
`CONNECTOR_WIDTH=16f`, `SPACING=2f`, `COLOR_BOOST=1.5f` (type colors `MulRGB(1.5f)`), `LINE_WIDTH=3f`,
`LINE_VERTICAL_OFFSET=0.1f`, `LINE_HORIZONTAL_OFFSET=30.5f`; slot-name consts `SLOT_NAME="<NODE_UI>"`,
`CONNECT_POINT_NAME="<WIRE_POINT>"`; Canvas size defaults to `node.OverrideWidth ?? 128f`.
