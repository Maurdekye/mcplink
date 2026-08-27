using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Resolves every MemberRef a mod makes into the game's own assemblies against the
// CURRENT engine binaries, and reports the ones that no longer exist / changed shape.
class Asm {
    public string Name; public string Path; public PEReader Pe; public MetadataReader R;
    // "Namespace.Type" (nested joined by '/') -> handle
    public Dictionary<string, TypeDefinitionHandle> Types = new();
}

class Resolver {
    static Prov prov = new Prov();
    Dictionary<string, Asm> engine = new(StringComparer.OrdinalIgnoreCase);

    static string TypeFullName(MetadataReader r, TypeDefinitionHandle h) {
        var td = r.GetTypeDefinition(h);
        string n = r.GetString(td.Name);
        if (td.IsNested) return TypeFullName(r, td.GetDeclaringType()) + "/" + n;
        string ns = td.Namespace.IsNil ? "" : r.GetString(td.Namespace);
        return ns.Length == 0 ? n : ns + "." + n;
    }

    public void LoadEngine(IEnumerable<string> dirs) {
        foreach (var d in dirs) {
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.GetFiles(d, "*.dll")) {
                try {
                    var fs = File.OpenRead(f);
                    var pe = new PEReader(fs);
                    if (!pe.HasMetadata) { pe.Dispose(); continue; }
                    var r = pe.GetMetadataReader();
                    if (!r.IsAssembly) { pe.Dispose(); continue; }
                    var name = r.GetString(r.GetAssemblyDefinition().Name);
                    if (engine.ContainsKey(name)) { pe.Dispose(); continue; }
                    var a = new Asm { Name = name, Path = f, Pe = pe, R = r };
                    foreach (var th in r.TypeDefinitions) a.Types[TypeFullName(r, th)] = th;
                    engine[name] = a;
                } catch { }
            }
        }
    }

    // Resolve a TypeRef to "assemblyName", "Namespace.Type"
    static (string asm, string full) ResolveTypeRef(MetadataReader r, TypeReferenceHandle h) {
        var tr = r.GetTypeReference(h);
        string name = r.GetString(tr.Name);
        string ns = tr.Namespace.IsNil ? "" : r.GetString(tr.Namespace);
        string full = ns.Length == 0 ? name : ns + "." + name;
        var scope = tr.ResolutionScope;
        if (scope.Kind == HandleKind.TypeReference) {
            var (a, outer) = ResolveTypeRef(r, (TypeReferenceHandle)scope);
            return (a, outer + "/" + name);
        }
        if (scope.Kind == HandleKind.AssemblyReference) {
            var ar = r.GetAssemblyReference((AssemblyReferenceHandle)scope);
            return (r.GetString(ar.Name), full);
        }
        return (null, full);
    }

    // all (name, sigstring) members of a type and its base chain, within engine assemblies
    void Collect(Asm a, TypeDefinitionHandle th, HashSet<string> names, HashSet<string> sigs, int depth) {
        if (depth > 12) return;
        var td = a.R.GetTypeDefinition(th);
        foreach (var mh in td.GetMethods()) {
            var md = a.R.GetMethodDefinition(mh);
            string n = a.R.GetString(md.Name);
            names.Add(n);
            try { var s = md.DecodeSignature(prov, null);
                  sigs.Add(n + "(" + string.Join(",", s.ParameterTypes) + ")->" + s.ReturnType); } catch { }
        }
        foreach (var fh in td.GetFields()) {
            var fd = a.R.GetFieldDefinition(fh);
            string n = a.R.GetString(fd.Name);
            names.Add(n);
            try { sigs.Add(n + ":" + fd.DecodeSignature(prov, null)); } catch { }
        }
        var bt = td.BaseType;
        if (bt.IsNil) return;
        if (bt.Kind == HandleKind.TypeDefinition) {
            Collect(a, (TypeDefinitionHandle)bt, names, sigs, depth + 1);
        } else if (bt.Kind == HandleKind.TypeReference) {
            var (basm, bfull) = ResolveTypeRef(a.R, (TypeReferenceHandle)bt);
            if (basm != null && engine.TryGetValue(basm, out var ba) && ba.Types.TryGetValue(bfull, out var bth))
                Collect(ba, bth, names, sigs, depth + 1);
        }
    }

    public void Scan(string modPath) {
        using var fs = File.OpenRead(modPath);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata) return;
        var r = pe.GetMetadataReader();
        var problems = new List<string>();
        int checkedCount = 0;
        foreach (var h in r.MemberReferences) {
            var mr = r.GetMemberReference(h);
            if (mr.Parent.Kind != HandleKind.TypeReference) continue;
            var (asmName, full) = ResolveTypeRef(r, (TypeReferenceHandle)mr.Parent);
            if (asmName == null || !engine.TryGetValue(asmName, out var a)) continue;   // not an engine type
            if (!a.Types.TryGetValue(full, out var th)) {
                problems.Add($"TYPE GONE   {full} (in {asmName})");
                continue;
            }
            string mname = r.GetString(mr.Name);
            var names = new HashSet<string>(); var sigs = new HashSet<string>();
            Collect(a, th, names, sigs, 0);
            checkedCount++;
            if (!names.Contains(mname)) { problems.Add($"MEMBER GONE {full}.{mname}"); continue; }
            if (mr.GetKind() == MemberReferenceKind.Method) {
                try {
                    var s = mr.DecodeMethodSignature(prov, null);
                    string key = mname + "(" + string.Join(",", s.ParameterTypes) + ")->" + s.ReturnType;
                    if (!sigs.Contains(key)) problems.Add($"SIG CHANGED {full}.{key}");
                } catch { }
            }
        }
        string label = System.IO.Path.GetFileName(modPath);
        var uniq = problems.Distinct().ToList();
        if (uniq.Count == 0) Console.WriteLine($"{label,-34} CLEAN  ({checkedCount} engine memberrefs checked)");
        else {
            Console.WriteLine($"{label,-34} {uniq.Count} PROBLEM(S)  ({checkedCount} checked)");
            foreach (var p in uniq.Take(8)) Console.WriteLine($"      {p}");
            if (uniq.Count > 8) Console.WriteLine($"      … {uniq.Count - 8} more");
        }
    }
}
