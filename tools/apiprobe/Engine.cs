using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

class E {
    public static void Run(string dll, string typeName, string method) {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var r = pe.GetMetadataReader();
        var prov = new Prov();
        bool found = false;
        foreach (var th in r.TypeDefinitions) {
            var td = r.GetTypeDefinition(th);
            if (r.GetString(td.Name) != typeName) continue;
            foreach (var mh in td.GetMethods()) {
                var md = r.GetMethodDefinition(mh);
                if (r.GetString(md.Name) != method) continue;
                var sig = md.DecodeSignature(prov, null);
                Console.WriteLine($"  DEF {typeName}.{method}({string.Join(",", sig.ParameterTypes)}) -> {sig.ReturnType}");
                found = true;
            }
        }
        if (!found) Console.WriteLine($"  *** {typeName}.{method} NOT FOUND as a TypeDef in {Path.GetFileName(dll)} ***");
    }
}
