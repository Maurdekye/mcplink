// Hierarchy-wire checks for AgentWires — the curve handles and the atlas cell.
//
// ⚠ WHY THESE LIVE IN THEIR OWN FILE RATHER THAN IN Program.cs's TOP-LEVEL STATEMENTS.
// Program.cs installs an AssemblyResolve hook as its very first statement, and that hook is the
// only reason the engine assemblies load at all (they are referenced with Private=false and are
// never copied next to the test binary). But the JIT compiles Main BEFORE its first statement
// runs, so any Elements.Core type appearing in one of Main's own locals — or in a local
// function's signature — has to resolve before the hook exists. Doing that crashes the entire
// suite with `FileNotFoundException: Elements.Core` and a minidump, before a single check
// executes (measured here 2026-08-24). Every engine-typed value in this suite is consequently
// kept inside a lazily-JITted body; Run's signature is deliberately engine-free so that Main can
// JIT without Elements.Core, and this body JITs only once it is called.

using Elements.Core;
using FrooxEngine.ProtoFlux;
using McpLink;

internal static class WireChecks
{
    /// <summary>Reported in-world 2026-08-24: the superior→subordinate wire left the superior's
    /// bottom edge pointing down and ALSO entered the subordinate's top edge pointing down (it
    /// should arrive from above), and its texture showed all five atlas styles crushed across the
    /// wire's thin width.
    ///
    /// Neither is checkable from a rendered frame here, so both are pinned against the ENGINE'S
    /// OWN definitions instead: the curve below is WireMeshBase.UpdateMeshData transcribed
    /// literally, and the atlas arithmetic is ProtoFluxWireManager.Setup's, cross-checked against
    /// DatatypeColorHelper.GetWireAtlasOffset. Appearance stays the user's call after deploy.</summary>
    public static void Run(Action<string, Func<bool>> Check)
    {
        // Two upright panels, superior above-left of its subordinate. Unrotated, so a panel-local
        // axis IS its world axis and LocalDirectionToGlobal drops out of the arithmetic.
        var superiorBottom = new float3(0f, -0.5f, 0f);
        var subordinateTop = new float3(2f, -1.5f, 0f);
        float handle = AgentWires.HandleLength(MathX.Distance(superiorBottom, subordinateTop));
        float3 supHandle = AgentWires.SuperiorHandle.Normalized * handle;
        float3 subHandle = AgentWires.SubordinateHandle.Normalized * handle;

        Check("hierarchy wire descends into the subordinate's TOP edge from ABOVE", () =>
            WireCurve(superiorBottom, subordinateTop, supHandle, subHandle, 0.95f).y > subordinateTop.y);
        Check("CONTROL: it still leaves the superior's BOTTOM edge going DOWN", () =>
            WireCurve(superiorBottom, subordinateTop, supHandle, subHandle, 0.05f).y < superiorBottom.y);
        Check("DISCRIMINATOR: a Down subordinate handle sags BELOW the top edge (the reported bug)", () =>
            WireCurve(superiorBottom, subordinateTop, supHandle, float3.Down * handle, 0.95f).y < subordinateTop.y);
        Check("CONTROL: the handle stays clamped to the documented 0.1–0.8 range", () =>
            AgentWires.HandleLength(0.001f) == 0.1f && AgentWires.HandleLength(1000f) == 0.8f);

        Check("atlas rect covers exactly ONE of the stacked cells, not the whole atlas", () =>
            MathX.Approximately(AgentWires.AtlasUVScale.y * ProtoFluxWireManager.WIRE_ATLAS_IMAGE_COUNT, 1f)
            && AgentWires.AtlasUVScale.y < 1f);
        Check("the chosen cell is the engine's SINGLE-VALUE (non-vector) style", () =>
            DatatypeColorHelper.GetWireAtlasOffset(typeof(float)) == AgentWires.SingleValueAtlasOffset
            && DatatypeColorHelper.GetWireAtlasOffset(typeof(int)) == AgentWires.SingleValueAtlasOffset);
        Check("DISCRIMINATOR: a vector datatype maps to a DIFFERENT cell (so cell 0 is not vacuous)", () =>
            DatatypeColorHelper.GetWireAtlasOffset(typeof(float3)) != AgentWires.SingleValueAtlasOffset);
        Check("atlas rect matches ProtoFluxWireManager.Setup's own arithmetic for that offset", () =>
            MathX.Approximately(AgentWires.AtlasUVOffset.y,
                (ProtoFluxWireManager.WIRE_ATLAS_IMAGE_COUNT - 1 - AgentWires.SingleValueAtlasOffset)
                * ProtoFluxWireManager.WIRE_ATLAS_RATIO));
        Check("atlas rect stays inside the texture", () =>
            AgentWires.AtlasUVOffset.y >= 0f
            && AgentWires.AtlasUVOffset.y + AgentWires.AtlasUVScale.y <= 1.0001f);
        Check("CONTROL: the along-the-length axis is left unscaled, so the style still tiles", () =>
            AgentWires.AtlasUVScale.x == 1f && AgentWires.AtlasUVOffset.x == 0f);
    }

    /// <summary>WireMeshBase.UpdateMeshData, transcribed: the curve point at parameter t. Note the
    /// shape — lerp(P0 + T0·t, P1 + T1·(1−t)) — which is why BOTH tangents are handles pointing OUT
    /// of their own endpoint, and why Tangent1 is not a direction of travel.</summary>
    private static float3 WireCurve(float3 p0, float3 p1, float3 t0, float3 t1, float t)
    {
        const float exp = 4f;                       // WireMeshBase.Exp default
        float span = MathX.Distance(p0, p1);        // engine clamps each handle to half the span
        if (t0.Magnitude > span * 0.5f) t0 = t0.Normalized * (span * 0.5f);
        if (t1.Magnitude > span * 0.5f) t1 = t1.Normalized * (span * 0.5f);
        float s = t < 0.5f
            ? MathX.Pow(t * 2f, exp) * 0.5f
            : 1f - MathX.Pow(1f - (t - 0.5f) * 2f, exp) * 0.5f;
        return MathX.LerpUnclamped(p0 + t0 * t, p1 + t1 * (1f - t), s);
    }
}
