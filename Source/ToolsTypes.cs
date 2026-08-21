using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FrooxEngine;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>Type reflection: replaces resomcp's get_type/component/enum/generic definition suite.</summary>
internal static class ToolsTypes
{
    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("describe_type",
            "Describe any engine type by name: kind, base chain, generic parameters, enum values, sync members " +
            "(for workers/components — what add/attach can initialize), and optionally methods. " +
            "Covers what get_type_definition / get_component_definition / get_enum_definition / " +
            "get_generic_type_definition did in resomcp. Accepts C#-style generics and short names.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"type\":{\"type\":\"string\"}," +
            "\"includeMethods\":{\"type\":\"boolean\",\"default\":false}}," +
            "\"required\":[\"type\"]}",
            args =>
            {
                var type = TypeUtil.Resolve(RequireString(args, "type"));
                bool includeMethods = OptBool(args, "includeMethods", false);

                var result = new JsonObject
                {
                    ["fullName"] = type.FullName,
                    ["friendly"] = TypeUtil.FriendlyName(type),
                    ["assembly"] = type.Assembly.GetName().Name,
                    ["kind"] = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class",
                    ["isAbstract"] = type.IsAbstract,
                };

                if (type.IsGenericTypeDefinition)
                    result["genericParameters"] = new JsonArray(type.GetGenericArguments()
                        .Select(a => (JsonNode)a.Name).ToArray());

                var bases = new JsonArray();
                for (Type? b = type.BaseType; b != null && bases.Count < 6; b = b.BaseType)
                    bases.Add(TypeUtil.FriendlyName(b));
                result["baseTypes"] = bases;

                if (type.IsEnum)
                {
                    var values = new JsonObject();
                    foreach (var name in Enum.GetNames(type))
                        values[name] = Convert.ToInt64(Enum.Parse(type, name));
                    result["values"] = values;
                }

                if (typeof(Worker).IsAssignableFrom(type) && !type.IsGenericTypeDefinition)
                {
                    var members = new JsonObject();
                    for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
                    {
                        foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        {
                            if (typeof(ISyncMember).IsAssignableFrom(field.FieldType) && !members.ContainsKey(field.Name))
                                members[field.Name] = TypeUtil.FriendlyName(field.FieldType);
                        }
                    }
                    result["syncMembers"] = members;
                }

                if (includeMethods)
                {
                    var methods = new JsonArray();
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                                 .Where(m => !m.IsSpecialName).Take(150))
                    {
                        methods.Add($"{TypeUtil.FriendlyName(method.ReturnType)} {method.Name}" +
                                    $"({string.Join(", ", method.GetParameters().Select(p => $"{TypeUtil.FriendlyName(p.ParameterType)} {p.Name}"))})");
                    }
                    result["methods"] = methods;
                }
                return result;
            }));

        add(new ToolDef("list_component_types",
            "List attachable component types matching a regex (searched over full names in FrooxEngine + " +
            "ProtoFluxBindings and other loaded engine assemblies). Generic definitions are marked.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"pattern\":{\"type\":\"string\"}," +
            "\"limit\":{\"type\":\"integer\",\"default\":100}}," +
            "\"required\":[\"pattern\"]}",
            args =>
            {
                var pattern = new Regex(RequireString(args, "pattern"),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                int limit = OptInt(args, "limit", 100);

                var matches = new JsonArray();
                bool truncated = false;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = assembly.GetName().Name ?? "";
                    if (!name.StartsWith("FrooxEngine", StringComparison.Ordinal) &&
                        !name.StartsWith("ProtoFlux", StringComparison.Ordinal))
                        continue;
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
                    foreach (var type in types)
                    {
                        if (type.IsAbstract || !typeof(Component).IsAssignableFrom(type))
                            continue;
                        if (!pattern.IsMatch(type.FullName ?? type.Name))
                            continue;
                        if (matches.Count >= limit)
                        {
                            truncated = true;
                            break;
                        }
                        matches.Add(new JsonObject
                        {
                            ["type"] = type.FullName,
                            ["genericDefinition"] = type.IsGenericTypeDefinition ? true : null,
                        });
                    }
                    if (truncated)
                        break;
                }
                return new JsonObject { ["count"] = matches.Count, ["types"] = matches, ["truncated"] = truncated };
            }));
    }
}
