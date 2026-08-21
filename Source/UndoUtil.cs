using FrooxEngine;
using FrooxEngine.Undo;

namespace McpLink;

/// <summary>
/// Undo integration: mutating tools register their changes with the engine's undo system so
/// agent mistakes can be Ctrl+Z'd in-game. Undo points are best-effort — a failure to record
/// one never blocks the actual operation.
/// </summary>
internal static class UndoUtil
{
    // Reentrancy guard: run_batch wraps the WHOLE batch in one undo batch (so a 300-op mistake is
    // ONE Ctrl+Z, not 300 entries against the engine's 50-step cap); ops inside it that call
    // Batch themselves (sed, bulk_build, cp, ...) must not open a nested engine batch.
    [ThreadStatic]
    private static int _batchDepth;

    public static T Batch<T>(World world, string description, Func<T> action)
    {
        bool opened = false;
        if (_batchDepth == 0)
        {
            try
            {
                world.BeginUndoBatch($"McpLink: {description}");
                opened = true;
            }
            catch { /* no undo manager available (e.g. userspace edge cases) — proceed without */ }
        }
        _batchDepth++;
        try
        {
            return action();
        }
        finally
        {
            _batchDepth--;
            if (opened)
            {
                try { world.EndUndoBatch(); } catch { }
            }
        }
    }

    /// <summary>
    /// Like Batch, but the completed prefix can be rolled back afterwards (run_batch
    /// transactional:true). Must be the OUTERMOST batch — rollback works by undoing the engine
    /// BatchAction this call opens, and the engine's Undo() refuses to run while any batch is
    /// still active, so a nested transactional batch is an error rather than a silent no-op.
    /// Flow: run 'action', END the batch, then consult 'shouldRollback' — if true, revert the
    /// batch through the same per-user undo stack the 'undo' tool uses. Rollback only covers
    /// world mutations that recorded undo points; non-world side effects (file writes, renders)
    /// stay. A rollback failure is reported via 'rollbackError', never thrown — the caller's
    /// original result (with the failing op's error inside) must survive.
    /// </summary>
    public static T BatchTransactional<T>(World world, string description, Func<T> action,
        Func<bool> shouldRollback, out bool rolledBack, out string? rollbackError)
    {
        if (_batchDepth != 0)
            throw new InvalidOperationException(
                "A transactional batch cannot run nested inside another undo batch — rollback would have to revert the enclosing batch too");

        BatchAction? batch = null;
        try
        {
            batch = world.BeginUndoBatch($"McpLink: {description}");
        }
        catch { /* no undo manager available — degraded transactional semantics, reported below */ }

        // The batch must be ENDED before any rollback (the engine's Undo() throws while a batch
        // is active), including on the exception path — hence an idempotent close instead of a
        // plain finally, which would run too late relative to the catch-path rollback.
        bool closed = false;
        void Close()
        {
            if (closed)
                return;
            closed = true;
            _batchDepth--;
            if (batch != null)
            {
                try { world.EndUndoBatch(); } catch { }
            }
        }

        _batchDepth++;
        T result;
        try
        {
            result = action();
        }
        catch
        {
            // An exception ESCAPED the batch body (malformed op, handler bug). Still honor the
            // transactional promise — revert the applied prefix — then rethrow the original.
            Close();
            TryRollback(world, batch, out _, out _);
            throw;
        }
        Close();

        rolledBack = false;
        rollbackError = null;
        if (shouldRollback())
            TryRollback(world, batch, out rolledBack, out rollbackError);
        return result;
    }

    /// <summary>Revert a just-ended undo batch. Never throws — failure lands in rollbackError.</summary>
    private static void TryRollback(World world, BatchAction? batch, out bool rolledBack, out string? rollbackError)
    {
        rolledBack = false;
        rollbackError = null;
        try
        {
            if (batch == null)
            {
                rollbackError = "no engine undo batch could be opened (undo manager unavailable) — nothing was rolled back";
            }
            else if (!batch.IsActionValid)
            {
                // No undoable mutation was recorded before the failure — the world is unchanged.
                // Do NOT call Undo() here: the engine skips (and destroys) invalid steps when
                // picking the undo target, so it would revert an OLDER, unrelated step instead.
                rolledBack = true;
            }
            else if (world.GetUndoManager(false) is { } manager
                     && ReferenceEquals(manager.GetUndoStep(world.LocalUser), batch))
            {
                manager.Undo();
                rolledBack = true;
            }
            else
            {
                rollbackError = "the batch is no longer the newest undo step — skipped rollback to avoid reverting unrelated changes";
            }
        }
        catch (Exception e)
        {
            rollbackError = e.Message;
        }
    }

    public static void RecordFieldChange(IField field)
    {
        try { field.CreateUndoPoint(false); } catch { }
    }

    public static void RecordRefChange(ISyncRef reference)
    {
        try { reference.CreateUndoPoint(false); } catch { }
    }

    public static void RecordListAdd(ISyncList list, ISyncMember element) =>
        RecordListOp(list, element, "CreateListElementAddedUndoPoint");

    public static void RecordListRemove(ISyncList list, ISyncMember element) =>
        RecordListOp(list, element, "CreateListElementRemovedUndoPoint");

    /// <summary>
    /// The engine's list undo extensions are generic on SyncList&lt;T&gt; — recover T from the
    /// concrete list type and invoke reflectively. Best-effort like every other undo point.
    /// </summary>
    private static void RecordListOp(ISyncList list, ISyncMember element, string methodName)
    {
        try
        {
            Type? baseType = list.GetType();
            while (baseType != null &&
                   !(baseType.IsGenericType && baseType.GetGenericTypeDefinition() == typeof(SyncList<>)))
                baseType = baseType.BaseType;
            if (baseType == null)
                return;
            typeof(AddListElementExtensions)
                .GetMethod(methodName)!
                .MakeGenericMethod(baseType.GetGenericArguments()[0])
                .Invoke(null, [list, element, (Elements.Core.LocaleString)"McpLink: edit_list"]);
        }
        catch { }
    }

    public static void RecordSpawn(Slot slot, string description)
    {
        try { slot.CreateSpawnUndoPoint(description, null!); } catch { }
    }

    public static void RecordSpawn(Component component)
    {
        try { component.CreateSpawnUndoPoint(null!); } catch { }
    }

    /// <summary>Destroy with undo when possible, falling back to a plain destroy.</summary>
    public static void UndoableDestroyElement(IWorldElement element)
    {
        switch (element)
        {
            case Slot slot:
                try { slot.UndoableDestroy(true); return; } catch { }
                slot.Destroy();
                return;
            case Component component:
                try { component.UndoableDestroy(null!); return; } catch { }
                component.Destroy();
                return;
            case IDestroyable destroyable:
                destroyable.Destroy();
                return;
            default:
                throw new ArgumentException($"{TypeUtil.FriendlyName(element.GetType())} is not destroyable");
        }
    }
}
