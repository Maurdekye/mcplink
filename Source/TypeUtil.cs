using System.Collections.Concurrent;
using System.Reflection;

namespace McpLink;

/// <summary>
/// Type name resolution across all loaded assemblies. Accepts CLR full names, short names,
/// C#-style generics ("ValueField&lt;float3&gt;"), common aliases ("float", "float3"), and
/// tolerates ResoniteLink-style "[Assembly]Namespace.Type" strings for muscle-memory parity.
/// </summary>
internal static class TypeUtil
{
    private static readonly ConcurrentDictionary<string, Type?> Cache = new();

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["bool"] = "System.Boolean",
        ["byte"] = "System.Byte",
        ["sbyte"] = "System.SByte",
        ["short"] = "System.Int16",
        ["ushort"] = "System.UInt16",
        ["int"] = "System.Int32",
        ["uint"] = "System.UInt32",
        ["long"] = "System.Int64",
        ["ulong"] = "System.UInt64",
        ["float"] = "System.Single",
        ["double"] = "System.Double",
        ["decimal"] = "System.Decimal",
        ["char"] = "System.Char",
        ["string"] = "System.String",
        ["object"] = "System.Object",
    };

    public static Type Resolve(string name)
    {
        return TryResolve(name)
               ?? throw new ArgumentException($"Cannot resolve type '{name}'");
    }

    public static Type? TryResolve(string rawName)
    {
        string name = Normalize(rawName);
        return Cache.GetOrAdd(name, ResolveUncached);
    }

    /// <summary>Strip "[Assembly]" prefixes and whitespace so ResoniteLink-style names work.</summary>
    private static string Normalize(string name)
    {
        var result = new System.Text.StringBuilder(name.Length);
        int depth = 0;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '[')
            {
                // "[FrooxEngine]FrooxEngine.Slot" — bracketed assembly hint, skip it.
                // (CLR array/generic brackets never appear in the accepted input syntax here.)
                depth++;
                continue;
            }
            if (c == ']' && depth > 0)
            {
                depth--;
                continue;
            }
            if (depth > 0 || char.IsWhiteSpace(c))
                continue;
            result.Append(c);
        }
        return result.ToString();
    }

    private static Type? ResolveUncached(string name)
    {
        if (Aliases.TryGetValue(name, out var aliased))
            name = aliased;

        // C#-style generics: Foo<Bar, Baz>
        int lt = name.IndexOf('<');
        if (lt >= 0 && name.EndsWith(">", StringComparison.Ordinal))
        {
            string baseName = name[..lt];
            var argNames = SplitGenericArgs(name[(lt + 1)..^1]);
            var genericDef = ResolveSimple($"{baseName}`{argNames.Count}");
            if (genericDef == null)
                return null;
            var args = new Type[argNames.Count];
            for (int i = 0; i < argNames.Count; i++)
            {
                var arg = TryResolve(argNames[i]);
                if (arg == null)
                    return null;
                args[i] = arg;
            }
            return genericDef.MakeGenericType(args);
        }

        return ResolveSimple(name);
    }

    private static Type? ResolveSimple(string name)
    {
        var direct = Type.GetType(name, throwOnError: false);
        if (direct != null)
            return direct;

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies)
        {
            var t = assembly.GetType(name, throwOnError: false);
            if (t != null)
                return t;
        }

        // Short-name search (e.g. "Slot", "ValueField`1") — prefer FrooxEngine/Elements hits.
        Type? match = null;
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
            foreach (var t in types)
            {
                if (t.Name != name)
                    continue;
                if (t.Namespace?.StartsWith("FrooxEngine", StringComparison.Ordinal) == true ||
                    t.Namespace?.StartsWith("Elements", StringComparison.Ordinal) == true)
                    return t;
                match ??= t;
            }
        }
        return match;
    }

    private static List<string> SplitGenericArgs(string inner)
    {
        var args = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                args.Add(inner[start..i]);
                start = i + 1;
            }
        }
        args.Add(inner[start..]);
        return args;
    }

    private static readonly Dictionary<Type, string> FriendlyPrimitives = new()
    {
        [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short", [typeof(ushort)] = "ushort", [typeof(int)] = "int",
        [typeof(uint)] = "uint", [typeof(long)] = "long", [typeof(ulong)] = "ulong",
        [typeof(float)] = "float", [typeof(double)] = "double", [typeof(decimal)] = "decimal",
        [typeof(char)] = "char", [typeof(string)] = "string", [typeof(object)] = "object",
    };

    /// <summary>C#-style friendly rendering: ValueField&lt;float3&gt; instead of ValueField`1[[...]].</summary>
    public static string FriendlyName(Type type)
    {
        if (FriendlyPrimitives.TryGetValue(type, out var primitive))
            return primitive;
        if (type.IsArray)
            return FriendlyName(type.GetElementType()!) + "[]";
        if (!type.IsGenericType)
            return type.Name;
        string baseName = type.Name;
        int tick = baseName.IndexOf('`');
        if (tick >= 0)
            baseName = baseName[..tick];
        return $"{baseName}<{string.Join(",", type.GetGenericArguments().Select(FriendlyName))}>";
    }
}
