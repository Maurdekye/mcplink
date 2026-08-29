using Elements.Assets;
using Elements.Core;
using FrooxEngine;

namespace McpLink;

/// <summary>
/// Refuses a render whose target was never written.
///
/// THE DEFECT THIS EXISTS TO KILL (measured live 2026-08-29, McpLink 2.11.2): render_view against
/// the `Local` background world returned a perfectly normal success result — path, width, height,
/// position, rotation, even `isolated: 1` — for a PNG in which EVERY pixel was (0,0,0,0). Nothing
/// in the response distinguished it from a render that drew the whole world. Four independent
/// renders (near/far, both faces of the subject, with and without isolate) were 100% zero, while
/// userspace rendered 44,630 distinct colours and the focused world 513 at the same moment — so
/// the renderer was working and the response was lying by omission.
///
/// It fools you twice, which is why a warning field would not have been enough:
///   1. A FULLY TRANSPARENT PNG DISPLAYS AS WHITE. Open it and you see a clean white frame, which
///      reads as "correct render, the world is just empty or brightly lit". It looks like an answer.
///   2. The tool's success criterion was `Save` not throwing — never "something was drawn".
///
/// This is the house abstention shape (a check that produces no observation and reports it in the
/// exact format it uses for a successful one) sitting inside a tool we use to VERIFY OTHER THINGS.
///
/// ---
///
/// WHAT THIS CHECK CLAIMS, AND WHAT IT DELIBERATELY DOES NOT.
///
/// It measures the produced bitmap. It does NOT predict which worlds "can" be rendered: a
/// predicate over renderable worlds is a claim about the engine that goes stale silently and
/// without anyone noticing, whereas a scan of the pixels is a measurement that is true whenever
/// it runs. If backgrounded worlds become renderable in some future engine build, this check
/// simply stops firing — it needs no maintenance to stay correct.
///
/// ⚠ THE ONE CONFLATION IT CANNOT RESOLVE, STATED PLAINLY. "The target was never written" and
/// "every pixel of a genuine render is legitimately fully transparent" produce byte-identical
/// bitmaps. Nothing in the buffer can separate them, so this ships as the NARROWER check —
/// it detects "no pixel was ever written" and says exactly that, rather than guessing at cause.
/// The evidence says a real render writes a background (both live controls came back opaque,
/// alpha 255, while the never-written target came back uniformly zero), so refusing by default is
/// right; `allowEmpty: true` exists for the caller who genuinely wants an all-transparent image.
/// An empty render must not return the same shape as a full one — but the caller who knowingly
/// wants one should not be permanently locked out.
/// </summary>
internal static class RenderGuard
{
    /// <summary>
    /// Set to "1" to force every render to be treated as never-written, driving the failure path
    /// on demand against a real, working render.
    ///
    /// This exists for the same reason MCPLINK_GAME / MCPLINK_BUILT exist in
    /// tools/dev/verify-deploy-artifact.sh: A CHECK NOBODY CAN DRIVE INTO FAILURE IS A CHECK
    /// NOBODY HAS EVIDENCE WORKS. Without this you can only observe the guard staying quiet, which
    /// is indistinguishable from a guard that is wired up wrong and can never fire at all.
    ///
    /// ⚠ THE GAP THIS CLOSES, AND IT IS THE ONE THE OFFLINE SUITE CANNOT REACH. The suite tests
    /// this class directly; it does NOT prove ToolsRender actually CALLS it. Delete the
    /// EnsureDrewSomething line from render_view and the whole suite stays green. So the wiring is
    /// verified live, and this variable is how:
    ///
    ///   1. eval: Environment.SetEnvironmentVariable("MCPLINK_RENDER_FORCE_EMPTY", "1")
    ///   2. render_view on a world KNOWN to render (userspace) — it must now REFUSE, and the
    ///      refusal must name MCPLINK_RENDER_FORCE_EMPTY. That is the guard firing from inside
    ///      the live tool, which is the thing the offline suite cannot show.
    ///   3. eval: Environment.SetEnvironmentVariable("MCPLINK_RENDER_FORCE_EMPTY", null)
    ///   4. the same render_view must now SUCCEED with real pixels — the known-positive control,
    ///      without which step 2 only proves render_view can fail for some reason.
    ///
    /// The property is read per call rather than cached precisely so this needs no game restart.
    /// </summary>
    internal const string ForceEmptyVar = "MCPLINK_RENDER_FORCE_EMPTY";

    internal static bool ForceEmpty =>
        Environment.GetEnvironmentVariable(ForceEmptyVar) == "1";

    /// <summary>
    /// True as soon as ANY pixel differs from (0,0,0,0). Early-exits on the first one, so the
    /// healthy case normally costs a single pixel read and only a genuinely empty target is
    /// scanned in full — cheap when fine, exact when suspicious.
    ///
    /// Sampling a grid instead would be cheaper still and WRONG in the dangerous direction: a
    /// render that drew only a few pixels would be declared empty and refused. A false FAIL here
    /// destroys a valid render, so the scan is exhaustive before it will say "nothing".
    /// </summary>
    internal static bool AnyPixelWritten(int width, int height, Func<int, int, color32> getPixel)
    {
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var p = getPixel(x, y);
                if (p.r != 0 || p.g != 0 || p.b != 0 || p.a != 0)
                    return true;
            }
        return false;
    }

    /// <summary>
    /// THE ONLY WAY A RENDER REACHES DISK. Renders, waits, guards, saves — one operation.
    ///
    /// This is deliberately a funnel rather than a convenience. The offline suite can prove the
    /// guard WORKS but cannot prove ToolsRender CALLS it: delete an EnsureDrewSomething line and
    /// every check still passes, which is this project's signature failure (a check that abstains
    /// reads exactly like a pass) one level out from the bug it was written to fix.
    ///
    /// A test asserting "the call is present" would not fix that — it would be a source grep, and
    /// a grep for whether a check exists abstains and reads like the check existing. So the
    /// possibility is removed structurally instead: `Bitmap2D.Save` is not called anywhere else in
    /// the render path, so FORGETTING THE GUARD IS NOT AN EDIT ANYONE CAN MAKE BY OMISSION.
    /// Saving an unchecked bitmap now requires writing a new save path on purpose, which is a
    /// conscious act rather than a tidy-up.
    ///
    /// Both render tools had the identical never-inspected-bitmap defect at their own
    /// RenderToBitmap call. That is the argument for one shared path rather than two correct ones.
    /// </summary>
    internal static void RenderGuardedToFile(
        World world, RenderTask task, int timeoutMs, string path, bool allowEmpty, string what)
    {
        var render = world.Render.RenderToBitmap(task);
        if (!render.Wait(timeoutMs))
            throw new TimeoutException($"{what} did not complete within {timeoutMs} ms");
        var bitmap = render.GetAwaiter().GetResult();

        EnsureDrewSomething(bitmap, world.Name, allowEmpty, what);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!bitmap.Save(path, 95, preserveColorInAlpha: false))
            throw new InvalidOperationException($"Bitmap save failed for '{path}'");
    }

    internal static bool AnyPixelWritten(Bitmap2D bitmap) =>
        AnyPixelWritten(bitmap.Size.x, bitmap.Size.y, (x, y) => bitmap.GetPixel32(x, y));

    /// <summary>
    /// Throws unless the render actually drew something. <paramref name="what"/> names the render
    /// for the message ("render_view", "orbit frame 3/12").
    /// </summary>
    internal static void EnsureDrewSomething(Bitmap2D bitmap, string worldName, bool allowEmpty, string what)
    {
        if (allowEmpty)
            return;

        bool forced = ForceEmpty;
        if (!forced && AnyPixelWritten(bitmap))
            return;

        // Name the forcing explicitly. A forced failure that reads like a real one would send
        // someone hunting a renderer bug that isn't there.
        string cause = forced
            ? $"FORCED by {ForceEmptyVar}=1 (this render was not actually inspected)"
            : $"every one of the {bitmap.Size.x}x{bitmap.Size.y} pixels is exactly (0,0,0,0)";

        throw new InvalidOperationException(
            $"{what} drew nothing in world '{worldName}': {cause}. The render target was never " +
            "written — no subject, no background, no skybox. Refused rather than returned, " +
            "because a fully transparent PNG DISPLAYS AS WHITE and is otherwise indistinguishable " +
            "from a legitimate render of an empty-looking scene. Known causes: the world is not " +
            "currently renderable (non-focused background worlds have produced exactly this, while " +
            "userspace and the focused world render fine), or an 'isolate' target that is entirely " +
            "out of frame. If you genuinely want a fully transparent image, pass allowEmpty: true.");
    }
}
