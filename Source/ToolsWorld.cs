using System.Text.Json.Nodes;
using FrooxEngine;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>World/user/session management and diagnostics.</summary>
internal static class ToolsWorld
{
    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("users",
            "List the users in a world: name, id, host/local flags, presence, and their root slot + head position.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}}}}}",
            args =>
            {
                var world = GetWorld(args);
                return WorldRunner.Run(world, () =>
                {
                    var users = new JsonArray();
                    foreach (var user in world.AllUsers)
                    {
                        var entry = new JsonObject
                        {
                            ["name"] = user.UserName,
                            ["id"] = user.UserID,
                            ["isHost"] = user == world.HostUser,
                            ["isLocal"] = user == world.LocalUser,
                        };
                        // presence + head are version-drifty — read reflectively, omit if absent
                        if (ReflectionUtil.FindProperty(user.GetType(), "IsPresentInWorld")?.GetValue(user) is bool present)
                            entry["present"] = present;
                        var root = user.Root;
                        if (root != null)
                        {
                            entry["rootSlot"] = Encode.ElementRef(root.Slot);
                            if (ReflectionUtil.FindProperty(root.GetType(), "HeadPosition")?.GetValue(root) is { } headPosition)
                                entry["headPosition"] = Encode.Value(headPosition);
                        }
                        users.Add(entry);
                    }
                    return (JsonNode)new JsonObject { ["count"] = users.Count, ["users"] = users };
                });
            }));

        add(new ToolDef("perf",
            "Engine/world diagnostics: per-world frame delta (→ effective update rate), user count, focus. " +
            "For deeper counters use reflect_get on 'type:FrooxEngine.Engine' statics.",
            "{\"type\":\"object\",\"properties\":{}}",
            _ =>
            {
                var manager = (Engine.Current ?? throw new InvalidOperationException("Engine not ready")).WorldManager;
                var worlds = new JsonArray();
                foreach (var world in manager.Worlds)
                {
                    worlds.Add(WorldRunner.Run(world, () =>
                    {
                        float delta = world.Time.Delta;
                        return (JsonNode)new JsonObject
                        {
                            ["name"] = world.Name,
                            ["focus"] = world.Focus.ToString(),
                            ["userCount"] = world.UserCount,
                            ["frameDeltaMs"] = delta * 1000f,
                            ["updatesPerSecond"] = delta > 0 ? 1f / delta : null,
                        };
                    }));
                }
                return new JsonObject { ["worlds"] = worlds };
            }));

        add(new ToolDef("focus_world",
            "Switch the locally focused world (the one the user sees). Background worlds skip updates/ProtoFlux/" +
            "physics, so focusing also 'wakes' a world.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"world\":{\"type\":\"string\",\"description\":\"World name (or 'userspace').\"}}," +
            "\"required\":[\"world\"]}",
            args =>
            {
                var manager = (Engine.Current ?? throw new InvalidOperationException("Engine not ready")).WorldManager;
                var world = WorldRunner.ResolveWorld(RequireString(args, "world"));
                // Live-found footgun: FocusWorld demotes the previous world to Background
                // unconditionally. Focusing an Overlay/PrivateOverlay world (Userspace!) rips it
                // out of its overlay state and breaks the dash until Focus is manually restored.
                if (world.Focus is World.WorldFocus.Overlay or World.WorldFocus.PrivateOverlay)
                    throw new InvalidOperationException(
                        $"'{world.Name}' is an {world.Focus} world — focusing it would demote it to Background " +
                        "and break its overlay (the dash, for Userspace). Target it with world:\"userspace\" " +
                        "on individual tools instead.");
                manager.FocusWorld(world);
                return new JsonObject { ["focused"] = world.Name };
            }));
    }
}
