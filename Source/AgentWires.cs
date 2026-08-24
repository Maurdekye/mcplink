using Elements.Core;
using FrooxEngine;
using FrooxEngine.ProtoFlux;

namespace McpLink;

/// <summary>
/// In-world wires between Prompt Wizard agent panels that are related in the orgtree hierarchy —
/// the panels embody agents, so their org relationships get drawn in 3D space:
///
/// - superior → subordinate: a SOLID curved wire (the ProtoFlux StripeWireMesh, sharing the flux
///   wire material) leaving the superior panel's BOTTOM edge and arriving into the subordinate
///   panel's TOP edge, vertex-colored as a gradient from the superior's tier color to the
///   subordinate's.
/// - coworkers (same direct superior, including two top-level agents): a DASHED grey line
///   between the panels' facing side edges — literal geometry, a row of shared-mesh box
///   segments (texture-dashing a thin wire reads as solid; boxes never do).
///
/// Only DIRECT relations are drawn, and only among panels of the same org in the same world.
/// Geometry follows the panels live: a per-world once-per-update tick recomputes endpoints,
/// writing sync fields only when something actually moved (the flux wire manager's own
/// epsilon idiom, so parked panels cost no network traffic).
///
/// All entry points must run on the world thread. Static registries — a hot reload tears the
/// wires down (Teardown) and existing panels re-register never (their mod state is gone), so
/// wires only span panels created since the last reload.
/// </summary>
internal static class AgentWires
{
    private const float HalfPanelPx = 575f;    // canvas is 1150×1150, centered on the root slot
    private const float SolidWidth = 0.014f;
    private const float DashThickness = 0.011f;
    private const int DashCount = 11;          // coworker dashes: fixed count, length-adaptive
    private const float DashFill = 0.55f;      // fraction of each segment that is dash (rest gap)
    private static readonly colorX CoworkerGrey = new(0.62f, 0.65f, 0.70f, 1f);

    // ---- hierarchy curve shape ----
    // WireMeshBase evaluates the curve as lerp(P0 + T0·t, P1 + T1·(1−t)) — so BOTH tangents are
    // handles pointing OUT of their OWN endpoint. Tangent1 is NOT the direction of travel through
    // P1. The wire therefore has to leave the superior's bottom edge going DOWN and leave the
    // subordinate's top edge going UP, which is what makes it descend into that top edge from
    // above. Giving Tangent1 the panel's Down instead sags the wire below the subordinate and
    // hooks it back up into the top edge from underneath (reported in-world 2026-08-24).
    internal static readonly float3 SuperiorHandle = float3.Down;      // out of the superior's bottom
    internal static readonly float3 SubordinateHandle = float3.Up;     // out of the subordinate's top

    /// <summary>Hermite handle length for a hierarchy wire whose endpoints are <paramref name="span"/> apart.</summary>
    internal static float HandleLength(float span) => MathX.Clamp(span * 0.5f, 0.1f, 0.8f);

    // ---- hierarchy wire texture ----
    // The shared flux wire material's texture is the ProtoFlux wire ATLAS: WIRE_ATLAS_IMAGE_COUNT
    // stacked wire styles, one per datatype width. StripeWireMesh sets SwapUV, so UV.y is the
    // ACROSS-THE-WIDTH axis — left at its default scale of 1 the strip samples the whole atlas and
    // renders all five styles crushed into the wire's thin width (reported in-world 2026-08-24).
    // A real flux wire picks exactly one cell; ProtoFluxWireManager.Setup computes that rect from
    // an atlasOffset, and DatatypeColorHelper.GetWireAtlasOffset returns 0 for any non-IVector
    // type — i.e. cell 0 is the SINGLE-VALUE style, which is the one an org relationship wants.
    internal const int SingleValueAtlasOffset = 0;

    /// <summary>UVScale selecting one atlas cell across the strip's width (length still tiles).</summary>
    internal static float2 AtlasUVScale =>
        new(1f, ProtoFluxWireManager.WIRE_ATLAS_RATIO);

    /// <summary>UVOffset placing that cell on <see cref="SingleValueAtlasOffset"/>, as Setup does.</summary>
    internal static float2 AtlasUVOffset =>
        new(0f, (ProtoFluxWireManager.WIRE_ATLAS_IMAGE_COUNT - 1 - SingleValueAtlasOffset)
                * ProtoFluxWireManager.WIRE_ATLAS_RATIO);

    internal sealed class PanelLink
    {
        public Slot Root = null!;
        public string Org = "";
        public string NodeId = "";
        public string? ParentId;               // null = top level
        public colorX Tier;
        public bool Gone;                      // retired or destroyed — drops out of the graph
    }

    private sealed class Wire
    {
        public Slot Slot = null!;
        public StripeWireMesh? Mesh;           // hierarchy curve (coworker wires use Dashes)
        public readonly List<Slot> Dashes = new();
        public PanelLink A = null!;            // hierarchy: the superior · coworker: either
        public PanelLink B = null!;
        public bool Hierarchy;
        public float3 LastP0 = float3.MaxValue;
        public float3 LastP1 = float3.MaxValue;
    }

    private static readonly Dictionary<World, List<PanelLink>> Panels = new();
    private static readonly Dictionary<World, List<Wire>> Wires = new();
    private static readonly Dictionary<World, Slot> WireRoots = new();
    private static readonly HashSet<World> Ticking = new();

    /// <summary>Add a hired panel to the world's relationship graph and redraw. World thread.</summary>
    public static PanelLink Register(World world, Slot root, string org, string node, string? parent, colorX tier)
    {
        if (!Panels.TryGetValue(world, out var list))
        {
            Panels[world] = list = new List<PanelLink>();
            world.WorldDestroyed += Forget;
        }
        var link = new PanelLink { Root = root, Org = org, NodeId = node, ParentId = parent, Tier = tier };
        list.Add(link);
        Rebuild(world);
        return link;
    }

    /// <summary>The panel's agent is gone (retired, or the panel died) — drop it and redraw.
    /// Safe to call from destroy handlers: no world reads beyond scheduling.</summary>
    public static void Drop(PanelLink? link)
    {
        if (link == null || link.Gone)
            return;
        link.Gone = true;
        var world = link.Root.World;
        if (world.IsDestroyed)
            return;
        world.RunSynchronously(() =>
        {
            if (!world.IsDestroyed)
                Rebuild(world);
        });
    }

    /// <summary>Hot reload: destroy every wire slot and clear the registries.</summary>
    public static void Teardown()
    {
        foreach (var (world, root) in WireRoots.ToArray())
        {
            if (world.IsDestroyed)
                continue;
            world.RunSynchronously(() =>
            {
                if (!root.IsDestroyed)
                    root.Destroy();
            });
        }
        Panels.Clear();
        Wires.Clear();
        WireRoots.Clear();
        Ticking.Clear();
    }

    private static void Forget(World world)
    {
        Panels.Remove(world);
        Wires.Remove(world);
        WireRoots.Remove(world);
        Ticking.Remove(world);
    }

    // ======================= graph → wires =======================

    private static void Rebuild(World world)
    {
        if (Wires.TryGetValue(world, out var old))
            foreach (var w in old)
                if (!w.Slot.IsDestroyed)
                    w.Slot.Destroy();
        var wires = Wires[world] = new List<Wire>();

        if (!Panels.TryGetValue(world, out var panels))
            return;
        panels.RemoveAll(p => p.Gone || p.Root.IsDestroyed);
        if (panels.Count < 2)
        {
            if (WireRoots.TryGetValue(world, out var emptyRoot) && !emptyRoot.IsDestroyed)
                emptyRoot.Destroy();
            WireRoots.Remove(world);
            return;
        }

        for (int i = 0; i < panels.Count; i++)
            for (int j = i + 1; j < panels.Count; j++)
            {
                var a = panels[i];
                var b = panels[j];
                if (a.Org != b.Org)
                    continue;
                if (a.NodeId == b.ParentId)
                    wires.Add(BuildWire(world, a, b, hierarchy: true));
                else if (b.NodeId == a.ParentId)
                    wires.Add(BuildWire(world, b, a, hierarchy: true));
                else if (a.ParentId == b.ParentId)
                    wires.Add(BuildWire(world, a, b, hierarchy: false));
            }

        if (wires.Count > 0)
        {
            foreach (var w in wires)
                UpdateGeometry(w);
            EnsureTick(world);
        }
    }

    private static Slot WireRoot(World world)
    {
        if (WireRoots.TryGetValue(world, out var root) && !root.IsDestroyed)
            return root;
        root = world.RootSlot.AddSlot("McpLink Agent Wires", persistent: false);
        WireRoots[world] = root;
        return root;
    }

    private static Wire BuildWire(World world, PanelLink from, PanelLink to, bool hierarchy)
    {
        var slot = WireRoot(world).AddSlot(hierarchy ? $"{from.NodeId} ▸ {to.NodeId}" : $"{from.NodeId} ~ {to.NodeId}",
            persistent: false);
        var wire = new Wire { Slot = slot, A = from, B = to, Hierarchy = hierarchy };
        if (hierarchy)
        {
            var mesh = slot.AttachMesh<StripeWireMesh>(SolidMaterial(world), out MeshRenderer _);
            mesh.Steps.Value = 16;
            mesh.Width0.Value = SolidWidth;
            mesh.Width1.Value = SolidWidth;
            mesh.Color0.Value = from.Tier;
            mesh.Color1.Value = to.Tier;
            mesh.UVScale.Value = AtlasUVScale;      // one atlas cell, not all five
            mesh.UVOffset.Value = AtlasUVOffset;
            wire.Mesh = mesh;
        }
        else
        {
            // dashes are literal geometry — a row of shared-mesh boxes laid along the line
            // (texture-based dashing on a thin wire reads as solid; boxes never do)
            var box = world.GetSharedComponentOrCreate("McpLink_AgentWireDashBox", delegate (BoxMesh b)
            {
                b.Size.Value = float3.One;
            });
            var material = DashMaterial(world);
            for (int i = 0; i < DashCount; i++)
            {
                var dash = slot.AddSlot($"Dash{i}", persistent: false);
                var renderer = dash.AttachComponent<MeshRenderer>();
                renderer.Mesh.Target = box;
                renderer.Materials.Add(material);
                wire.Dashes.Add(dash);
            }
        }
        return wire;
    }

    /// <summary>The exact ProtoFlux wire material (shared world-wide with real flux wires).</summary>
    private static FresnelMaterial SolidMaterial(World world) =>
        world.GetSharedComponentOrCreate("ProtoFlux_WireMaterial", delegate (FresnelMaterial fresnel)
        {
            fresnel.NearColor.Value = new colorX(0.8f);
            fresnel.FarColor.Value = new colorX(1.4f);
            fresnel.Sidedness.Value = Sidedness.Double;
            fresnel.UseVertexColors.Value = true;
            fresnel.BlendMode.Value = BlendMode.Alpha;
            fresnel.ZWrite.Value = ZWrite.On;
            StaticTexture2D target = fresnel.Slot.AttachTexture(OfficialAssets.Graphics.ProtoFlux.Wire_Atlas);
            fresnel.NearTexture.Target = target;
            fresnel.FarTexture.Target = target;
        });

    private static UnlitMaterial DashMaterial(World world) =>
        world.GetSharedComponentOrCreate("McpLink_AgentWireDashed", delegate (UnlitMaterial unlit)
        {
            unlit.TintColor.Value = CoworkerGrey;
        });

    // ======================= per-update geometry =======================

    private static void EnsureTick(World world)
    {
        if (!Ticking.Add(world))
            return;
        void Tick()
        {
            if (world.IsDestroyed || !Wires.TryGetValue(world, out var wires) || wires.Count == 0)
            {
                Ticking.Remove(world);
                return;
            }
            bool anyDead = false;
            foreach (var w in wires)
            {
                if (w.Slot.IsDestroyed || w.A.Root.IsDestroyed || w.B.Root.IsDestroyed)
                {
                    anyDead = true;
                    continue;
                }
                UpdateGeometry(w);
            }
            if (anyDead)
                Rebuild(world);
            world.RunInUpdates(1, Tick);
        }
        world.RunInUpdates(1, Tick);
    }

    private static void UpdateGeometry(Wire w)
    {
        if (w.Hierarchy)
        {
            float3 p0 = w.A.Root.LocalPointToGlobal(new float3(0f, -HalfPanelPx, 0f));
            float3 p1 = w.B.Root.LocalPointToGlobal(new float3(0f, HalfPanelPx, 0f));
            if ((p0 - w.LastP0).Magnitude < 0.001f && (p1 - w.LastP1).Magnitude < 0.001f)
                return; // parked — no sync writes
            w.LastP0 = p0;
            w.LastP1 = p1;
            float handle = HandleLength((p1 - p0).Magnitude);
            float3 t0 = w.A.Root.LocalDirectionToGlobal(SuperiorHandle).Normalized * handle;   // out the bottom
            float3 t1 = w.B.Root.LocalDirectionToGlobal(SubordinateHandle).Normalized * handle; // out the top
            var wireSlot = w.Slot;
            w.Mesh!.Point0.Value = wireSlot.GlobalPointToLocal(p0);
            w.Mesh.Point1.Value = wireSlot.GlobalPointToLocal(p1);
            w.Mesh.Tangent0.Value = wireSlot.GlobalDirectionToLocal(t0);
            w.Mesh.Tangent1.Value = wireSlot.GlobalDirectionToLocal(t1);
        }
        else
        {
            float3 aPos = w.A.Root.GlobalPosition;
            float3 bPos = w.B.Root.GlobalPosition;
            float3 aRight = w.A.Root.LocalDirectionToGlobal(float3.Right).Normalized;
            float3 bRight = w.B.Root.LocalDirectionToGlobal(float3.Right).Normalized;
            float aSide = MathX.Dot(bPos - aPos, aRight) >= 0f ? 1f : -1f;
            float bSide = MathX.Dot(aPos - bPos, bRight) >= 0f ? 1f : -1f;
            float3 p0 = w.A.Root.LocalPointToGlobal(new float3(aSide * HalfPanelPx, 0f, 0f));
            float3 p1 = w.B.Root.LocalPointToGlobal(new float3(bSide * HalfPanelPx, 0f, 0f));
            if ((p0 - w.LastP0).Magnitude < 0.001f && (p1 - w.LastP1).Magnitude < 0.001f)
                return;
            w.LastP0 = p0;
            w.LastP1 = p1;
            float length = (p1 - p0).Magnitude;
            if (length < 0.01f)
                return;
            float3 direction = (p1 - p0) / length;
            floatQ rotation = floatQ.LookRotation(direction, float3.Up);
            float segment = length / w.Dashes.Count;
            for (int i = 0; i < w.Dashes.Count; i++)
            {
                var dash = w.Dashes[i];
                if (dash.IsDestroyed)
                    continue;
                dash.GlobalPosition = p0 + direction * segment * (i + 0.5f);
                dash.GlobalRotation = rotation;
                dash.GlobalScale = new float3(DashThickness, DashThickness, segment * DashFill);
            }
        }
    }
}
