using FrooxEngine;

namespace McpLink;

/// <summary>
/// All world access must happen on that world's update thread. Tool handlers run on HTTP
/// threads, so every world touch is marshaled through RunSynchronously and awaited with a
/// timeout (a stalled world must not hang the HTTP response forever).
/// </summary>
internal static class WorldRunner
{
    public static World ResolveWorld(string? spec)
    {
        var manager = Engine.Current?.WorldManager
                      ?? throw new InvalidOperationException("Engine is not ready yet");

        if (string.IsNullOrEmpty(spec) || spec.Equals("focused", StringComparison.OrdinalIgnoreCase))
            return manager.FocusedWorld ?? throw new InvalidOperationException("No focused world");

        if (spec.Equals("userspace", StringComparison.OrdinalIgnoreCase))
            return Userspace.UserspaceWorld ?? throw new InvalidOperationException("Userspace world unavailable");

        foreach (var world in manager.Worlds)
        {
            if (string.Equals(world.Name, spec, StringComparison.OrdinalIgnoreCase))
                return world;
        }
        throw new ArgumentException(
            $"No world named '{spec}'. Use \"focused\", \"userspace\", or one of: " +
            string.Join(", ", manager.Worlds.Select(w => $"'{w.Name}'")));
    }

    [ThreadStatic]
    private static World? _currentWorld;

    /// <summary>
    /// Mark the current thread as already being 'world's update thread for the duration of
    /// 'action', so nested tool calls run inline instead of queueing (which would deadlock).
    /// For callbacks the ENGINE delivers on the update thread (RunInSeconds, events) that then
    /// re-enter the tool registry — never call from an HTTP thread.
    /// </summary>
    public static T AsWorldThread<T>(World world, Func<T> action)
    {
        var previous = _currentWorld;
        _currentWorld = world;
        try
        {
            return action();
        }
        finally
        {
            _currentWorld = previous;
        }
    }

    /// <summary>True when the current thread is already 'world's update thread (reentrant tool
    /// call, e.g. inside run_batch) — blocking on world progress here would deadlock.</summary>
    public static bool IsOnWorldThread(World world) => ReferenceEquals(_currentWorld, world);

    public static T Run<T>(World world, Func<T> action, int timeoutMs = 15000)
    {
        // Reentrancy: a tool handler invoked from inside another Run (e.g. run_batch) is already
        // on this world's update thread — queueing again would deadlock the blocking wait.
        if (ReferenceEquals(_currentWorld, world))
            return action();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.RunSynchronously(() =>
        {
            var previous = _currentWorld;
            _currentWorld = world;
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception e)
            {
                completion.TrySetException(e);
            }
            finally
            {
                _currentWorld = previous;
            }
        }, false, null!, false);

        if (!completion.Task.Wait(timeoutMs))
            throw new TimeoutException($"World '{world.Name}' did not process the request within {timeoutMs} ms");
        return completion.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Cooperative subtree walk: visits up to slotsPerTick slots per update tick so large scans
    /// (grep/find over a whole world) never hitch the game. The visitor runs on the update
    /// thread; return false to stop early. Blocks the calling (HTTP) thread until done.
    /// </summary>
    public static void RunWalk(World world, string rootId, int slotsPerTick,
        Func<Slot, string, bool> visit, int timeoutMs = 120000)
    {
        slotsPerTick = Math.Clamp(slotsPerTick, 100, 100000);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        world.RunSynchronously(() =>
        {
            try
            {
                var root = Resolve.Slot(world, rootId);
                var stack = new Stack<(Slot slot, string path)>();
                stack.Push((root, Shaping.Strip(root.Name) ?? ""));

                void Step()
                {
                    var previous = _currentWorld;
                    _currentWorld = world;
                    try
                    {
                        int processed = 0;
                        while (stack.Count > 0 && processed < slotsPerTick)
                        {
                            var (slot, path) = stack.Pop();
                            if (slot.IsDestroyed)
                                continue;
                            processed++;
                            if (!visit(slot, path))
                            {
                                completion.TrySetResult(true);
                                return;
                            }
                            foreach (var child in slot.Children)
                                stack.Push((child, $"{path}/{Shaping.Strip(child.Name)}"));
                        }
                        if (stack.Count == 0)
                            completion.TrySetResult(true);
                        else
                            world.RunInUpdates(1, Step);
                    }
                    catch (Exception e)
                    {
                        completion.TrySetException(e);
                    }
                    finally
                    {
                        _currentWorld = previous;
                    }
                }

                Step();
            }
            catch (Exception e)
            {
                completion.TrySetException(e);
            }
        }, false, null!, false);

        if (!completion.Task.Wait(timeoutMs))
            throw new TimeoutException($"Chunked walk in '{world.Name}' did not finish within {timeoutMs} ms");
        completion.Task.GetAwaiter().GetResult();
    }
}
