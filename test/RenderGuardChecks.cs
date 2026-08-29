using System.Text.Json.Nodes;
using Elements.Assets;
using Elements.Core;
using McpLink;
using Renderite.Shared;

// No namespace, matching PanelChecks/WireChecks — Program.cs is top-level statements.
// Run's signature stays engine-free for the reason documented at the top of WireChecks.cs:
// an engine type in a Program.cs local resolves while Main is being JITted, i.e. before Main's
// first statement installs the AssemblyResolve hook, which crashes the suite before check one.

/// <summary>
/// Checks for RenderGuard — the refusal of a render whose target was never written.
///
/// These run against a REAL Elements.Assets.Bitmap2D, not a mock, because the thing under test is
/// "what do the engine's own pixels look like when nothing drew them". A freshly allocated RGBA32
/// bitmap is zero-filled, which is exactly the never-written target observed live; writing a
/// single pixel into it produces the other leg. Both legs come from the same fixture, so neither
/// can pass for a reason that has nothing to do with the guard.
///
/// BOTH LEGS ARE MANDATORY. A guard that never fires and a guard that always fires both look
/// "green" if you only ever test one direction — the failure this whole change exists to fix was
/// an instrument that could not say NO.
/// </summary>
internal static class RenderGuardChecks
{
    private static Bitmap2D Empty(int w = 8, int h = 8) =>
        new Bitmap2D(w, h, TextureFormat.RGBA32, false, ColorProfile.sRGB);

    private static Bitmap2D WithOnePixel(int x, int y, int w = 8, int h = 8)
    {
        var bmp = Empty(w, h);
        bmp.SetPixel32(x, y, new color32(1, 0, 0, 0));
        return bmp;
    }

    /// <summary>The 'properties' object of a registered tool's schema, or null if absent.</summary>
    private static JsonObject? SchemaProperties(string toolName)
    {
        foreach (var tool in ToolRegistry.DescribeTools())
            if (tool?["name"]?.GetValue<string>() == toolName)
                return (tool["inputSchema"] as JsonObject)?["properties"] as JsonObject;
        return null;
    }

    internal static void Run(Action<string, Func<bool>> Check)
    {
        Console.WriteLine("== render guard ==");

        // ---- the fixture itself is a claim; verify it before trusting either leg ----
        Check("fixture: a freshly allocated Bitmap2D really is all (0,0,0,0)", () =>
        {
            var bmp = Empty();
            for (int y = 0; y < bmp.Size.y; y++)
                for (int x = 0; x < bmp.Size.x; x++)
                {
                    var p = bmp.GetPixel32(x, y);
                    if (p.r != 0 || p.g != 0 || p.b != 0 || p.a != 0)
                        return false;
                }
            return true;
        });

        // ---- NEGATIVE leg: the guard must FIRE on a never-written target ----
        Check("AnyPixelWritten is false for a never-written bitmap", () =>
            !RenderGuard.AnyPixelWritten(Empty()));

        Check("EnsureDrewSomething THROWS on a never-written bitmap", () =>
        {
            try
            {
                RenderGuard.EnsureDrewSomething(Empty(), "TestWorld", allowEmpty: false, "render_view");
                return false; // did not throw — the guard cannot say NO
            }
            catch (InvalidOperationException) { return true; }
        });

        // ---- POSITIVE leg: the guard must STAY QUIET on a real render ----
        // A guard that always throws would pass every negative check above.
        Check("AnyPixelWritten is true when a single pixel was written", () =>
            RenderGuard.AnyPixelWritten(WithOnePixel(3, 4)));

        Check("EnsureDrewSomething does NOT throw when something was drawn", () =>
        {
            RenderGuard.EnsureDrewSomething(WithOnePixel(3, 4), "TestWorld", allowEmpty: false, "render_view");
            return true;
        });

        // A lone written pixel in the LAST scanline is the case a sampling implementation would
        // miss and wrongly refuse. Exhaustive scan or this goes red.
        Check("a single written pixel in the final corner still counts as drawn", () =>
            RenderGuard.AnyPixelWritten(WithOnePixel(7, 7)));

        // Alpha alone is enough — a black but opaque background is a real render.
        Check("opaque black (0,0,0,255) counts as drawn, not as empty", () =>
        {
            var bmp = Empty();
            bmp.SetPixel32(0, 0, new color32(0, 0, 0, 255));
            return RenderGuard.AnyPixelWritten(bmp);
        });

        // ---- the documented opt-out ----
        Check("allowEmpty:true suppresses the refusal", () =>
        {
            RenderGuard.EnsureDrewSomething(Empty(), "TestWorld", allowEmpty: true, "render_view");
            return true;
        });

        // ---- the message has to be usable, not just present ----
        Check("refusal message names the world, the size and the opt-out", () =>
        {
            try
            {
                RenderGuard.EnsureDrewSomething(Empty(16, 9), "Local", allowEmpty: false, "render_view");
                return false;
            }
            catch (InvalidOperationException e)
            {
                return e.Message.Contains("Local")
                    && e.Message.Contains("16x9")
                    && e.Message.Contains("allowEmpty")
                    && e.Message.Contains("render_view");
            }
        });

        // ---- the force-failure affordance (MCPLINK_RENDER_FORCE_EMPTY) ----
        // Mirrors MCPLINK_GAME / MCPLINK_BUILT in tools/dev/verify-deploy-artifact.sh: a check
        // nobody can drive into failure is a check nobody has evidence works. This is what lets
        // the LIVE tool's failure path be exercised against a real, working render.
        //
        // ⚠ SET the variable rather than reading whatever the environment happens to hold. A check
        // that depends on ambient environment is abstaining, not testing — and it would go green
        // asserting the opposite of the truth on a machine where the var is already set.
        Check("MCPLINK_RENDER_FORCE_EMPTY=1 forces a refusal on a bitmap that DID draw", () =>
        {
            string? prior = Environment.GetEnvironmentVariable(RenderGuard.ForceEmptyVar);
            try
            {
                Environment.SetEnvironmentVariable(RenderGuard.ForceEmptyVar, "1");
                var drew = WithOnePixel(3, 4);
                if (RenderGuard.AnyPixelWritten(drew) != true)
                    return false; // fixture must genuinely be non-empty, or this proves nothing
                try
                {
                    RenderGuard.EnsureDrewSomething(drew, "TestWorld", allowEmpty: false, "render_view");
                    return false; // override did not fire
                }
                catch (InvalidOperationException e)
                {
                    // and it must SAY it was forced, so nobody debugs a phantom renderer bug
                    return e.Message.Contains(RenderGuard.ForceEmptyVar);
                }
            }
            finally { Environment.SetEnvironmentVariable(RenderGuard.ForceEmptyVar, prior); }
        });

        Check("with the override UNSET, the same drawn bitmap passes", () =>
        {
            string? prior = Environment.GetEnvironmentVariable(RenderGuard.ForceEmptyVar);
            try
            {
                Environment.SetEnvironmentVariable(RenderGuard.ForceEmptyVar, null);
                RenderGuard.EnsureDrewSomething(WithOnePixel(3, 4), "TestWorld", allowEmpty: false, "render_view");
                return true;
            }
            finally { Environment.SetEnvironmentVariable(RenderGuard.ForceEmptyVar, prior); }
        });

        // ---- the tools actually declare the opt-out they tell you to use ----
        // The refusal message instructs the caller to pass allowEmpty. If the schema omits it,
        // that advice is a dead end.
        foreach (var tool in new[] { "render_view", "orbit_render" })
        {
            string name = tool;
            Check($"{name} schema declares allowEmpty", () =>
                SchemaProperties(name)?["allowEmpty"] != null);
        }

        // Control for the two checks above: the same lookup must return NO for a property that
        // does not exist. Otherwise a bug making SchemaProperties always non-null would pass them.
        Check("control: schema lookup returns nothing for a fabricated property", () =>
            SchemaProperties("render_view")?["xyzzyNotAProperty"] == null
            && SchemaProperties("render_view") != null);
    }
}
