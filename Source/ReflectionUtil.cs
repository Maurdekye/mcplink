using System.Reflection;

namespace McpLink;

/// <summary>Reflection helpers that see private members anywhere in a type hierarchy.</summary>
internal static class ReflectionUtil
{
    private const BindingFlags AnyDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    public static FieldInfo? FindField(Type type, string name)
    {
        for (Type? t = type; t != null; t = t.BaseType)
        {
            var field = t.GetField(name, AnyDeclared);
            if (field != null)
                return field;
        }
        return null;
    }

    public static PropertyInfo? FindProperty(Type type, string name)
    {
        for (Type? t = type; t != null; t = t.BaseType)
        {
            var property = t.GetProperty(name, AnyDeclared);
            if (property != null)
                return property;
        }
        return null;
    }

    public static IEnumerable<MethodInfo> FindMethods(Type type, string name)
    {
        var seen = new HashSet<(string, string)>();
        for (Type? t = type; t != null; t = t.BaseType)
        {
            foreach (var method in t.GetMethods(AnyDeclared))
            {
                if (method.Name != name)
                    continue;
                // dedupe overrides by signature
                var key = (method.Name, string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName)));
                if (seen.Add(key))
                    yield return method;
            }
        }
    }

    /// <summary>
    /// Walk a dotted member path ("_dynamicValues", "handler._currentSpace") through fields and
    /// properties, private included. Returns the terminal value; target may be an instance or,
    /// for static walks, a Type.
    /// </summary>
    public static object? WalkPath(object? instance, Type startType, string path)
    {
        object? current = instance;
        Type currentType = startType;
        foreach (string segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var field = FindField(currentType, segment);
            if (field != null)
            {
                if (!field.IsStatic && current == null)
                    throw new ArgumentException($"'{segment}' is an instance field but there is no instance");
                current = field.GetValue(field.IsStatic ? null : current);
            }
            else
            {
                var property = FindProperty(currentType, segment)
                               ?? throw new ArgumentException(
                                   $"No field or property '{segment}' on {TypeUtil.FriendlyName(currentType)}. " +
                                   $"Available: {DescribeMembers(currentType)}");
                var getter = property.GetGetMethod(nonPublic: true)
                             ?? throw new ArgumentException($"Property '{segment}' has no getter");
                if (!getter.IsStatic && current == null)
                    throw new ArgumentException($"'{segment}' is an instance property but there is no instance");
                current = property.GetValue(getter.IsStatic ? null : current);
            }

            if (current == null)
                return null; // null mid-path terminates the walk gracefully
            currentType = current.GetType();
        }
        return current;
    }

    /// <summary>Walk to the parent of the last segment so the final member can be written.</summary>
    public static (object? parent, Type parentType, string finalSegment) WalkToParent(
        object? instance, Type startType, string path)
    {
        int lastDot = path.LastIndexOf('.');
        if (lastDot < 0)
            return (instance, startType, path);

        string parentPath = path[..lastDot];
        var parent = WalkPath(instance, startType, parentPath)
                     ?? throw new ArgumentException($"'{parentPath}' resolved to null — cannot set a member on it");
        return (parent, parent.GetType(), path[(lastDot + 1)..]);
    }

    private static string DescribeMembers(Type type)
    {
        var names = new List<string>();
        for (Type? t = type; t != null && names.Count < 40; t = t.BaseType)
        {
            names.AddRange(t.GetFields(AnyDeclared).Select(f => f.Name));
            names.AddRange(t.GetProperties(AnyDeclared).Select(p => p.Name));
        }
        return string.Join(", ", names.Distinct().Take(40));
    }
}
