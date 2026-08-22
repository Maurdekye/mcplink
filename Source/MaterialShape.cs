using System.Text.Json.Nodes;
using Elements.Core;

namespace McpLink;

/// <summary>
/// Pure classification and diagnosis of material values for <c>renderer_info</c>.
///
/// The point of the tool is not just to save calls — it is to make the two commonest clothing
/// defects visible at a glance, because both of them LOOK like something else:
///   • the untextured 0.8 grey albedo, which reads as "a material" rather than "a material that
///     never got its texture";
///   • a white EmissiveColor, which renders as a white silhouette that is almost indistinguishable
///     from a failed albedo load — so people spend the debugging time on the wrong member.
///
/// Kept free of engine state so the offline suite can test it for real.
/// </summary>
internal static class MaterialShape
{
    /// The engine's untextured default albedo: 0.8 grey.
    public const float DefaultGrey = 0.8f;

    /// Colour-channel tolerance. Loose enough to catch a value that came back through a colour
    /// profile conversion, tight enough not to swallow a deliberate mid-grey.
    public const float ColorEpsilon = 0.02f;

    public static bool NearlyEqual(float a, float b) => MathX.Abs(a - b) <= ColorEpsilon;

    /// True for the engine's untextured 0.8 grey default (opaque, all channels equal).
    public static bool IsDefaultGrey(colorX c) =>
        NearlyEqual(c.r, DefaultGrey) && NearlyEqual(c.g, DefaultGrey) &&
        NearlyEqual(c.b, DefaultGrey) && NearlyEqual(c.a, 1f);

    /// True when emission is bright enough to wash the submesh out to a silhouette.
    public static bool IsWashedOutEmissive(colorX c) =>
        c.r >= 0.5f && c.g >= 0.5f && c.b >= 0.5f;

    /// <summary>
    /// Returns POSITIVE findings — each entry names something that IS the case. An empty array
    /// means "we looked and found none of these", never "we did not look"; the caller can tell
    /// the difference because the sibling fields carry the values that were examined.
    /// </summary>
    public static JsonArray Diagnose(colorX? albedo, colorX? emissive, bool hasAlbedoTexture)
    {
        var findings = new JsonArray();

        if (albedo is colorX a && IsDefaultGrey(a) && !hasAlbedoTexture)
            findings.Add((JsonNode)
                $"albedo is the untextured {DefaultGrey} grey default and NO albedo texture is bound — " +
                "this material almost certainly never received its texture. It renders as a plausible " +
                "flat grey garment rather than as an error.");

        if (emissive is colorX e && IsWashedOutEmissive(e))
            findings.Add((JsonNode)
                $"EmissiveColor is bright ({Format(e)}) — the submesh renders as a WHITE SILHOUETTE, " +
                "which looks almost exactly like a failed albedo load. Check this before concluding " +
                "the albedo texture is broken.");

        return findings;
    }

    public static string Format(colorX c) => $"[{c.r:0.###}, {c.g:0.###}, {c.b:0.###}, {c.a:0.###}]";
}
