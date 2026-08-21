using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;

namespace McpLinkEval;

/// <summary>
/// Script globals — every field is directly usable as an identifier in eval code.
/// One instance persists across eval calls (so 'vars' carries state between them).
/// </summary>
public class EvalGlobals
{
    /// <summary>Target world (set per call by McpLink).</summary>
    public FrooxEngine.World world = null!;

    /// <summary>The engine.</summary>
    public FrooxEngine.Engine engine = null!;

    /// <summary>Collects diagnostic output for the tool result: log("...").</summary>
    public Action<object?> log = _ => { };

    /// <summary>Scratch state persisting across eval calls in this session.</summary>
    public ConcurrentDictionary<string, object?> vars = new();

    /// <summary>Resolve a RefID string to a live element in 'world'.</summary>
    public FrooxEngine.IWorldElement resolve(string refId)
    {
        if (string.Equals(refId, "Root", StringComparison.OrdinalIgnoreCase))
            return world.RootSlot;
        if (!Elements.Core.RefID.TryParse(refId, out var id))
            throw new ArgumentException($"'{refId}' is not a RefID");
        return world.ReferenceController.GetObjectOrNull(id)
               ?? throw new ArgumentException($"No element {refId} in world '{world.Name}'");
    }
}

/// <summary>
/// The Roslyn boundary: McpLink calls Compile via reflection and gets back a plain
/// Func&lt;object, Task&lt;object?&gt;&gt; it can invoke without referencing any Roslyn type.
/// </summary>
public static class Evaluator
{
    private static readonly string[] DefaultImports =
    [
        "System", "System.Linq", "System.Collections.Generic", "System.Threading.Tasks",
        "FrooxEngine", "Elements.Core",
    ];

    private static ScriptOptions? _baseOptions;
    private static InteractiveAssemblyLoader? _loader;

    private static ScriptOptions BaseOptions()
    {
        if (_baseOptions != null)
            return _baseOptions;

        // reference every already-loaded, disk-backed assembly the engine uses — scripts can
        // touch ProtoFlux bindings, SkyFrost, etc. without an explicit reference list
        var references = new List<Assembly>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                continue;
            references.Add(assembly);
        }
        references.Add(typeof(Evaluator).Assembly);
        references = references.Distinct().ToList();

        // register the LIVE assembly instances with the interactive loader — otherwise the
        // scripting host re-loads referenced assemblies from disk into its own load context
        // and the globals type splits identity ("[A]EvalGlobals cannot be cast to [B]EvalGlobals")
        _loader = new InteractiveAssemblyLoader();
        foreach (var assembly in references)
        {
            try { _loader.RegisterDependency(assembly); }
            catch { /* some assemblies (mixed-mode etc.) refuse — scripts just can't use those */ }
        }

        _baseOptions = ScriptOptions.Default
            .AddReferences(references)
            .AddImports(DefaultImports)
            .WithAllowUnsafe(false);
        return _baseOptions;
    }

    /// <summary>
    /// Compile a script (expression or statements; 'return' and 'await' allowed) into a
    /// runner delegate. Throws with formatted diagnostics on compile errors.
    /// </summary>
    public static Func<object, Task<object?>> Compile(string code, string[]? extraImports)
    {
        var options = BaseOptions();
        if (extraImports is { Length: > 0 })
            options = options.AddImports(extraImports);

        var script = CSharpScript.Create<object?>(code, options, typeof(EvalGlobals), _loader);
        var diagnostics = script.Compile();

        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            var message = new StringBuilder("C# compilation failed:");
            foreach (var error in errors.Take(10))
                message.Append('\n').Append(error.ToString());
            throw new ArgumentException(message.ToString());
        }

        var runner = script.CreateDelegate();
        return async globals => await runner((EvalGlobals)globals).ConfigureAwait(false);
    }
}
