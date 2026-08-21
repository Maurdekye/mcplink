using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using FrooxEngine;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>
/// Dynamic-variable-space inventory with engine ground truth — the technique borrowed from
/// Banane9's DynVarSpaceTree mod: read the space's private registry (_dynamicValues) for every
/// linked variable identity (including read-but-never-declared phantoms), and classify each
/// IDynamicVariable component by asking its own handler which space it actually bound to
/// (handler._currentSpace) instead of reimplementing name-prefix binding resolution.
/// </summary>
internal static class ToolsDynVar
{
    public static void Register(Action<ToolDef> add)
    {
        RegisterUsers(add);
        add(new ToolDef("dynvar_space",
            "Inventory a DynamicVariableSpace with engine ground truth: every linked variable identity (name, type, " +
            "current value) from the space's own registry — including 'phantom' variables that are read but never " +
            "declared — plus each variable's declaring components (resolved via the engine's actual binding, not " +
            "name matching), and unbound declarations in the subtree. Give the slot holding the space, or any slot " +
            "under one (nearest enclosing space is used; spaceName disambiguates).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"slotId\":{\"type\":\"string\",\"description\":\"Slot with the DynamicVariableSpace, or any slot beneath one.\"}," +
            "\"spaceName\":{\"type\":\"string\",\"description\":\"Disambiguate when several spaces enclose the slot.\"}," +
            "\"includeValues\":{\"type\":\"boolean\",\"default\":true}}," +
            "\"required\":[\"slotId\"]}",
            args =>
            {
                var world = GetWorld(args);
                string slotId = RequireString(args, "slotId");
                string? spaceName = OptString(args, "spaceName");
                bool includeValues = OptBool(args, "includeValues", true);

                return WorldRunner.Run(world, () =>
                {
                    var slot = Resolve.Slot(world, slotId);
                    var spaces = FindSpaces(slot, spaceName);
                    if (spaces.Count == 0)
                        throw new ArgumentException(spaceName == null
                            ? $"No DynamicVariableSpace on '{slot.Name}' or any of its ancestors"
                            : $"No DynamicVariableSpace named '{spaceName}' on '{slot.Name}' or any of its ancestors");

                    var result = new JsonArray();
                    foreach (var space in spaces)
                        result.Add(SpaceJson(space, includeValues));
                    return (JsonNode)new JsonObject { ["spaces"] = result };
                });
            }));

        add(new ToolDef("env",
            "The dynamic-variable environment visible from a slot: walks up through EVERY enclosing " +
            "DynamicVariableSpace and lists each one's linked variables (name, type, current value) from the " +
            "space's own registry. Lighter than dynvar_space (no declaration classification).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"slotId\":{\"type\":\"string\"}," +
            "\"namePattern\":{\"type\":\"string\",\"description\":\"Regex filter on variable names.\"}," +
            "\"limit\":{\"type\":\"integer\",\"default\":200}}," +
            "\"required\":[\"slotId\"]}",
            args =>
            {
                var world = GetWorld(args);
                string slotId = RequireString(args, "slotId");
                var namePattern = ToolsSearch.MakeRegex(OptString(args, "namePattern"));
                int limit = OptInt(args, "limit", 200);

                return WorldRunner.Run(world, () =>
                {
                    var slot = Resolve.Slot(world, slotId);
                    var spaces = new JsonArray();
                    int total = 0;
                    for (var current = slot; current != null && total < limit; current = current.Parent)
                    {
                        foreach (var space in current.GetComponents<DynamicVariableSpace>())
                        {
                            var variables = new JsonArray();
                            foreach (var (name, type, value, hasValue) in ReadRegistry(space, includeValues: true))
                            {
                                if (namePattern != null && !namePattern.IsMatch(name))
                                    continue;
                                if (total++ >= limit)
                                    break;
                                variables.Add(new JsonObject
                                {
                                    ["name"] = name,
                                    ["type"] = TypeUtil.FriendlyName(type),
                                    ["value"] = value,
                                    ["hasValue"] = hasValue,
                                });
                            }
                            spaces.Add(new JsonObject
                            {
                                ["space"] = space.CurrentName,
                                ["id"] = space.ReferenceID.ToString(),
                                ["slotName"] = Shaping.Strip(space.Slot.Name),
                                ["variables"] = variables,
                            });
                        }
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["spaces"] = spaces,
                        ["truncated"] = total >= limit,
                    };
                });
            }));
    }

    private static void RegisterUsers(Action<ToolDef> add)
    {
        add(new ToolDef("dynvar_users",
            "Reverse lookup for dynamic variables: who declares, drives, reads, or writes variables matching a name " +
            "pattern under a subtree. Covers component declarations (IDynamicVariable), drivers/resets, and ProtoFlux " +
            "read/write/input nodes (variable names resolved from their VariableName/Path inputs when constant).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"rootId\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"namePattern\":{\"type\":\"string\",\"description\":\"Regex on the full variable name (e.g. \\\"Animator/Offset\\\").\"}," +
            "\"limit\":{\"type\":\"integer\",\"default\":200}}," +
            "\"required\":[\"namePattern\"]}",
            args =>
            {
                var world = GetWorld(args);
                string rootId = OptString(args, "rootId") ?? "Root";
                var namePattern = ToolsSearch.MakeRegex(RequireString(args, "namePattern"))!;
                int limit = OptInt(args, "limit", 200);

                return WorldRunner.Run(world, () =>
                {
                    var users = new JsonArray();
                    bool truncated = false;

                    void Visit(Slot slot, string path)
                    {
                        if (truncated)
                            return;
                        foreach (var component in slot.Components)
                        {
                            var (role, name) = Classify(component);
                            if (role == null || name == null || !namePattern.IsMatch(name))
                                continue;
                            if (users.Count >= limit)
                            {
                                truncated = true;
                                return;
                            }
                            users.Add(new JsonObject
                            {
                                ["name"] = name,
                                ["role"] = role,
                                ["id"] = component.ReferenceID.ToString(),
                                ["type"] = TypeUtil.FriendlyName(component.GetType()),
                                ["slotId"] = slot.ReferenceID.ToString(),
                                ["slotName"] = Shaping.Strip(slot.Name),
                                ["path"] = path,
                            });
                        }
                        foreach (var child in slot.Children)
                            Visit(child, $"{path}/{Shaping.Strip(child.Name)}");
                    }

                    var root = Resolve.Slot(world, rootId);
                    Visit(root, Shaping.Strip(root.Name) ?? "");
                    return (JsonNode)new JsonObject { ["count"] = users.Count, ["users"] = users, ["truncated"] = truncated };
                }, timeoutMs: 60000);
            }));
    }

    /// <summary>"Space/Name" → "Name" when the prefix matches the space (engine name parsing).</summary>
    private static string StripSpacePrefix(string variableName, string? spaceName)
    {
        int slash = variableName.IndexOf('/');
        if (slash < 0)
            return variableName;
        string prefix = variableName[..slash];
        return string.Equals(prefix, spaceName, StringComparison.OrdinalIgnoreCase)
            ? variableName[(slash + 1)..]
            : variableName;
    }

    /// <summary>Classify a component's relationship to a dynamic variable and extract its name.</summary>
    private static (string? role, string? name) Classify(Component component)
    {
        string typeName = component.GetType().Name;

        // drivers/resets implement IDynamicVariable too — classify them by type name first
        if (component is IDynamicVariable dynVar)
        {
            string role = typeName.Contains("Driver", StringComparison.Ordinal) ? "driver"
                : typeName.Contains("Reset", StringComparison.Ordinal) ? "reset"
                : "declaration";
            return (role, dynVar.VariableName);
        }

        if (typeName.Contains("DynamicV", StringComparison.Ordinal) ||
            typeName.StartsWith("Dynamic", StringComparison.Ordinal) && typeName.Contains("Variable", StringComparison.Ordinal) ||
            typeName.StartsWith("DynamicField", StringComparison.Ordinal) ||
            typeName.StartsWith("DynamicReference", StringComparison.Ordinal))
        {
            // driver / reset / field-binding components carry a VariableName sync field
            if (component.GetSyncMember("VariableName") is IField nameField)
            {
                string role = typeName.Contains("Driver", StringComparison.Ordinal) ? "driver"
                    : typeName.Contains("Reset", StringComparison.Ordinal) ? "reset"
                    : "binding";
                return (role, nameField.BoxedValue as string);
            }
        }

        if (component is FrooxEngine.ProtoFlux.ProtoFluxNode node &&
            (typeName.Contains("DynamicVariable", StringComparison.Ordinal) ||
             typeName.Contains("DynamicValueVariable", StringComparison.Ordinal) ||
             typeName.Contains("DynamicObjectVariable", StringComparison.Ordinal) ||
             typeName.Contains("DynamicReferenceVariable", StringComparison.Ordinal)))
        {
            string role = typeName.Contains("Write", StringComparison.Ordinal) ? "write-node"
                : typeName.Contains("Read", StringComparison.Ordinal) || typeName.Contains("Input", StringComparison.Ordinal) ? "read-node"
                : "node";
            return (role, NodeVariableName(node));
        }

        return (null, null);
    }

    /// <summary>Resolve a ProtoFlux dynvar node's variable name from its VariableName/Path input, when constant.</summary>
    private static string? NodeVariableName(FrooxEngine.ProtoFlux.ProtoFluxNode node)
    {
        foreach (ISyncRef input in node.AllInputs)
        {
            if (input is not ISyncMember member)
                continue;
            string? portName = node.GetSyncMemberName(member);
            if (portName is not ("VariableName" or "Path" or "Name"))
                continue;
            var target = input.Target;
            if (target == null)
                return null;
            var source = target as FrooxEngine.ProtoFlux.ProtoFluxNode
                         ?? target.FindNearestParent<FrooxEngine.ProtoFlux.ProtoFluxNode>();
            return source switch
            {
                FrooxEngine.ProtoFlux.IInput literal => literal.BoxedValue as string,
                FrooxEngine.ProtoFlux.IGlobalValueProxy globalValue => globalValue.BoxedValue as string,
                _ => null, // dynamic (FormatString etc.) — name not statically known
            };
        }
        return null;
    }

    /// <summary>Enumerate a space's registry identities (name, type, value) via the private _dynamicValues.</summary>
    private static IEnumerable<(string name, Type type, JsonNode? value, bool hasValue)> ReadRegistry(
        DynamicVariableSpace space, bool includeValues)
    {
        if (ReflectionUtil.WalkPath(space, typeof(DynamicVariableSpace), "_dynamicValues") is not IDictionary registry)
            yield break;
        var tryReadValue = typeof(DynamicVariableSpace).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == "TryReadValue" && m.IsGenericMethodDefinition);

        foreach (var key in registry.Keys)
        {
            var keyType = key.GetType();
            if (ReflectionUtil.FindField(keyType, "name")?.GetValue(key) is not string name ||
                ReflectionUtil.FindField(keyType, "type")?.GetValue(key) is not Type variableType)
                continue;

            JsonNode? value = null;
            bool hasValue = false;
            if (includeValues)
            {
                try
                {
                    var read = tryReadValue.MakeGenericMethod(variableType);
                    object?[] parameters = [name, null];
                    hasValue = (bool)read.Invoke(space, parameters)!;
                    value = hasValue ? Encode.Value(parameters[1]) : null;
                }
                catch (Exception e)
                {
                    value = $"<error: {e.Message}>";
                }
            }
            yield return (name, variableType, value, hasValue);
        }
    }

    private static List<DynamicVariableSpace> FindSpaces(Slot slot, string? spaceName)
    {
        for (var current = slot; current != null; current = current.Parent)
        {
            var found = new List<DynamicVariableSpace>();
            foreach (var space in current.GetComponents<DynamicVariableSpace>())
            {
                if (spaceName == null ||
                    string.Equals(space.CurrentName, spaceName, StringComparison.OrdinalIgnoreCase))
                    found.Add(space);
            }
            if (found.Count > 0)
                return found;
        }
        return [];
    }

    private static JsonObject SpaceJson(DynamicVariableSpace space, bool includeValues)
    {
        var result = new JsonObject
        {
            ["id"] = space.ReferenceID.ToString(),
            ["name"] = space.CurrentName,
            ["onlyDirectBinding"] = space.OnlyDirectBinding.Value,
            ["slot"] = Encode.ElementRef(space.Slot),
        };

        // Declarations, classified by the engine's own binding: handler._currentSpace.
        var declarationsByName = new Dictionary<string, JsonArray>();
        var unbound = new JsonArray();
        var boundElsewhere = new JsonArray();
        foreach (var dynVar in space.Slot.GetComponentsInChildren<IDynamicVariable>())
        {
            object? boundSpace;
            try
            {
                boundSpace = ReflectionUtil.WalkPath(dynVar, dynVar.GetType(), "handler._currentSpace");
            }
            catch (Exception)
            {
                continue; // exotic IDynamicVariable without the standard handler — skip
            }

            var component = (Worker)dynVar;
            var entry = new JsonObject
            {
                ["id"] = component.ReferenceID.ToString(),
                ["type"] = TypeUtil.FriendlyName(component.GetType()),
                ["slotId"] = ((Component)component).Slot.ReferenceID.ToString(),
                ["slotName"] = ((Component)component).Slot.Name,
                ["variableName"] = dynVar.VariableName,
            };

            if (ReferenceEquals(boundSpace, space))
            {
                // registry identities use BARE names; declarations carry the "Space/" prefix —
                // strip it so the two sides key identically (live-found bug, Inspector test)
                string name = StripSpacePrefix(dynVar.VariableName ?? "", space.CurrentName);
                if (!declarationsByName.TryGetValue(name, out var list))
                    declarationsByName[name] = list = new JsonArray();
                list.Add(entry);
            }
            else if (boundSpace == null)
            {
                unbound.Add(entry);
            }
            else
            {
                entry["boundTo"] = (boundSpace as DynamicVariableSpace)?.CurrentName;
                boundElsewhere.Add(entry);
            }
        }

        // Linked identities from the space's authoritative private registry.
        var variables = new JsonArray();
        var registry = ReflectionUtil.WalkPath(space, typeof(DynamicVariableSpace), "_dynamicValues") as IDictionary;
        if (registry != null)
        {
            var tryReadValue = typeof(DynamicVariableSpace).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "TryReadValue" && m.IsGenericMethodDefinition);

            foreach (var key in registry.Keys)
            {
                var keyType = key.GetType();
                string? name = ReflectionUtil.FindField(keyType, "name")?.GetValue(key) as string;
                var variableType = ReflectionUtil.FindField(keyType, "type")?.GetValue(key) as Type;
                if (name == null || variableType == null)
                    continue;

                var variable = new JsonObject
                {
                    ["name"] = name,
                    ["type"] = TypeUtil.FriendlyName(variableType),
                };

                if (includeValues)
                {
                    try
                    {
                        var read = tryReadValue.MakeGenericMethod(variableType);
                        object?[] parameters = [name, null];
                        bool success = (bool)read.Invoke(space, parameters)!;
                        variable["value"] = success ? Encode.Value(parameters[1]) : null;
                        variable["hasValue"] = success;
                    }
                    catch (Exception e)
                    {
                        variable["value"] = $"<error: {e.Message}>";
                    }
                }

                // A registry identity with no declaring component = a phantom (readers only).
                if (declarationsByName.TryGetValue(name, out var declarations))
                    variable["declarations"] = declarations;
                else
                    variable["phantom"] = true;

                variables.Add(variable);
            }
        }
        else
        {
            result["warning"] = "_dynamicValues registry not readable — engine layout may have changed";
        }

        // Declarations bound to this space whose identity name did not appear in the registry
        // (shouldn't happen, but surface rather than hide).
        var seenNames = new HashSet<string>(variables.Select(v => v!["name"]!.GetValue<string>()));
        foreach (var (name, declarations) in declarationsByName)
        {
            if (seenNames.Contains(name))
                continue;
            variables.Add(new JsonObject
            {
                ["name"] = name,
                ["declarations"] = declarations,
                ["warning"] = "declared and bound, but missing from the space registry",
            });
        }

        result["variables"] = variables;
        if (unbound.Count > 0)
            result["unboundDeclarations"] = unbound;
        if (boundElsewhere.Count > 0)
            result["boundToOtherSpaces"] = boundElsewhere;
        return result;
    }
}
