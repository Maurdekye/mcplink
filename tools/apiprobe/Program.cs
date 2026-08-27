// apiprobe -- resolve a mod's engine MemberRefs against the CURRENT Resonite binaries.
//
// Written by `mod-updater` (agent seat, 2026-08-27) during the 2026.8.27.1094 breakage, and
// contributed here with their explicit permission so it does not rot in a retired seat's scratch.
// Vendored essentially as written; only this header and a README were added.
//
// WHY IT EXISTS. The obvious way to hunt an engine break -- grep mod DLLs for the name of a type
// that changed -- is unsound three ways: it cannot tell a definition from a use (the engine's own
// Elements.Core.dll DEFINES SlimListEnumerableWrapper and so matches), it cannot tell which type a
// member belongs to, and above all it CANNOT SEE A BREAK IN WHICH NO TYPE DISAPPEARS. That last
// one is not hypothetical: of the ten affected mods on this install, three broke via a changed
// parameter list (DebugManager.Box, MeshX.SetHasUV*) or an IList->IReadOnlyList swap on a
// different member (CollectionsExtensions.FindIndex). A type-disappearance screen finds none of
// them.
//
// WHAT IT DOES INSTEAD. For every MemberRef a mod makes into an engine assembly, it decodes the
// reference's signature and compares it against the signatures of the resolved TypeDef and its
// base chain in the engine binaries on disk. Mismatch => SIG CHANGED; missing name => MEMBER GONE;
// missing type => TYPE GONE. This catches removals, return-type changes and added parameters
// alike, without needing to know in advance which API moved.
//
// It also distinguishes FIXED from BROKEN on an identical reference, which a MemberRef-presence
// check cannot: a rebuilt mod still carries a MemberRef to Slot.get_Children -- it still calls the
// property -- but the decoded signature now matches, so it resolves CLEAN.
//
// THREE VERDICTS, AND TWO OF THEM ARE ABSTENTIONS THAT REFUSE TO LOOK LIKE PASSES.
//   CLEAN (n checked)   n>0 refs resolved and all matched.
//   NOT CHECKED         0 engine refs resolved. Prints WHAT the refs pointed at instead, so you
//                       can see whether it is a genuinely engine-free assembly or a bad path.
//   !! ENGINE ASSEMBLY NOT LOADED - under-checked: <asms>
//                       the dangerous one. SOME refs resolved and some did not, so without this
//                       line you would get a confident "CLEAN (n checked)" that had silently
//                       skipped every FrooxEngine reference in the file. Far harder to spot than
//                       a zero, because the count looks healthy.
//
// The earlier version of this tool had exactly that defect and reported bare "CLEAN (0 checked)".
// Both verdicts were added by the original author after it was pointed out.
//
// ⚠ CONTROL-TEST BOTH BEFORE TRUSTING A CLEAN SWEEP. Measured 2026-08-27 against 45 mods:
//   - correct engine paths          -> 0 under-checked  (the real result)
//   - Libraries+rml_libs only, no
//     game root (PARTIAL map)       -> 41 under-checked  (proves the warning can fire)
//   - a nonexistent engine dir      -> 45 NOT CHECKED, 0 under-checked
// The middle run is the one that matters: without it, "0 under-checked" is itself an abstention.
//
// USAGE
//   dotnet run -- "<install>\rml_mods" --resolve "<install>;<install>\Libraries;<install>\rml_libs"
//   dotnet run -- "<install>\rml_mods"                  # narrow: report every get_Children return type
//   dotnet run -- <asm> --def <Type> <Member>           # print the engine's own definition
//
// Known-positive / known-negative control, if you want to confirm the tool discriminates before
// trusting a result: run it against a pre-update and a post-update build of the SAME mod. The
// former reports SIG CHANGED, the latter CLEAN.

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

class Prov : ISignatureTypeProvider<string, object> {
    public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte b) => r.GetString(r.GetTypeDefinition(h).Name);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte b) => r.GetString(r.GetTypeReference(h).Name);
    public string GetTypeFromSpecification(MetadataReader r, object g, TypeSpecificationHandle h, byte b) => "spec";
    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[,]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetPointerType(string e) => e + "*";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object g, int i) => "!!" + i;
    public string GetGenericTypeParameter(object g, int i) => "!" + i;
    public string GetModifiedType(string m, string u, bool req) => u;
    public string GetPinnedType(string e) => e;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
}

class P {
    static void Main(string[] a) {
        if (a.Length > 1 && a[1] == "--resolve") {
            var res = new Resolver();
            res.LoadEngine(a[2].Split(';'));
            foreach (var f in Directory.GetFiles(a[0]).Where(x => x.EndsWith(".dll") || x.EndsWith(".dll.disabled")).OrderBy(x => x))
                res.Scan(f);
            return;
        }
        if (a.Length > 1 && a[1] == "--def") { E.Run(a[0], a[2], a[3]); return; }
        foreach (var path in Directory.GetFiles(a[0]).Where(f => f.EndsWith(".dll") || f.EndsWith(".dll.disabled")).OrderBy(f => f)) {
            try {
                using var fs = File.OpenRead(path);
                using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
                if (!pe.HasMetadata) { Console.WriteLine($"{Path.GetFileName(path),-34} (no metadata)"); continue; }
                var r = pe.GetMetadataReader();
                var prov = new Prov();
                var hits = new List<string>();
                foreach (var h in r.MemberReferences) {
                    var mr = r.GetMemberReference(h);
                    if (r.GetString(mr.Name) != "get_Children") continue;
                    if (mr.GetKind() != MemberReferenceKind.Method) continue;
                    var sig = mr.DecodeMethodSignature(prov, null);
                    string owner = "?";
                    if (mr.Parent.Kind == HandleKind.TypeReference)
                        owner = r.GetString(r.GetTypeReference((TypeReferenceHandle)mr.Parent).Name);
                    hits.Add($"{owner}.get_Children -> {sig.ReturnType}");
                }
                if (hits.Count > 0)
                    Console.WriteLine($"{Path.GetFileName(path),-34} {string.Join(" | ", hits.Distinct())}");
            } catch (Exception e) { Console.WriteLine($"{Path.GetFileName(path),-34} ERROR {e.GetType().Name}: {e.Message}"); }
        }
    }
}
