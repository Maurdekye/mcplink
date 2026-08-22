using System.Text.Json.Nodes;
using Elements.Core;

namespace McpLink;

/// <summary>
/// Describes the display transform the engine's import pipeline applies to a spawned root,
/// as a DELTA from what the caller actually asked for.
///
/// Why this exists: <c>spawn_import</c> passes the caller's position/rotation to
/// <c>UniversalImporter</c>, which then applies its own normalisation on top — a scale factor,
/// a 180° Y rotation, and a Y offset — and reports none of it. Every consumer had to already
/// know to read the root transform back and reset it; one that didn't got a silently skewed
/// bake. That is the fail-silently-and-plausibly shape this toolkit exists to remove.
///
/// ⚠ The scale is NOT a constant. It was folklore as "≈1.135" until someone measured
/// 0.671 / 0.923 / 1.062 on three garments from a single folder. Never hardcode it; read it.
///
/// Everything here is pure so it can be tested without a running engine.
/// </summary>
internal static class ImportShape
{
    /// Tolerance for "the importer left this alone". Import scales differ from 1 by tenths,
    /// and the rotation by 180°, so this only has to clear float noise — not near-misses.
    public const float Epsilon = 1e-4f;

    public static bool Approximately(float a, float b) => MathX.Abs(a - b) <= Epsilon;

    public static bool Approximately(float3 a, float3 b) =>
        Approximately(a.x, b.x) && Approximately(a.y, b.y) && Approximately(a.z, b.z);

    /// Quaternion equality up to double-cover: q and -q are the SAME rotation, so compare
    /// |dot| against 1. Comparing componentwise would report a spurious deviation.
    public static bool Approximately(floatQ a, floatQ b) =>
        MathX.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w) >= 1f - Epsilon;

    /// <summary>
    /// Builds the <c>appliedTransform</c> block. <paramref name="actual"/> values are the
    /// spawned root's LOCAL TRS after the hierarchy settled; the requested values are what the
    /// caller passed to spawn_import.
    /// </summary>
    public static JsonObject DescribeTransform(
        float3 actualPosition, floatQ actualRotation, float3 actualScale,
        float3 requestedPosition, floatQ requestedRotation)
    {
        bool positionKept = Approximately(actualPosition, requestedPosition);
        bool rotationKept = Approximately(actualRotation, requestedRotation);
        bool scaleIsOne = Approximately(actualScale, float3.One);

        // POSITIVE markers: say what the importer DID, not what it didn't. An empty list read as
        // "nothing happened" whether or not we had actually looked.
        var deviations = new JsonArray();
        if (!scaleIsOne)
            deviations.Add((JsonNode)
                $"scale {Format(actualScale)} — the importer normalised the model's size. This factor is " +
                "NOT a constant (measured 0.671 / 0.923 / 1.062 across three garments in one folder); " +
                "read it per import, never assume a value.");
        if (!rotationKept)
        {
            float degrees = AngleBetweenDegrees(actualRotation, requestedRotation);
            deviations.Add((JsonNode)
                $"rotation differs from the requested rotation by {degrees:0.##}° " +
                $"(applied local rotation euler {Format(actualRotation.EulerAngles)}).");
        }
        if (!positionKept)
            deviations.Add((JsonNode)
                $"position {Format(actualPosition)} rather than the requested {Format(requestedPosition)} " +
                $"— offset by {Format(actualPosition - requestedPosition)}.");

        return new JsonObject
        {
            ["position"] = Encode.Value(actualPosition),
            ["rotation"] = Encode.Value(actualRotation),
            ["scale"] = Encode.Value(actualScale),
            ["rotationEulerDegrees"] = Encode.Value(actualRotation.EulerAngles),
            ["requestedPosition"] = Encode.Value(requestedPosition),
            ["requestedRotation"] = Encode.Value(requestedRotation),
            // the one field a consumer can branch on
            ["matchesRequest"] = positionKept && rotationKept && scaleIsOne,
            ["deviations"] = deviations,
        };
    }

    /// Smallest angle between two rotations, in degrees, honouring double-cover.
    public static float AngleBetweenDegrees(floatQ a, floatQ b)
    {
        float dot = MathX.Clamp(MathX.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w), -1f, 1f);
        return 2f * MathX.Acos(dot) * (180f / MathX.PI);
    }

    private static string Format(float3 v) => $"[{v.x:0.####}, {v.y:0.####}, {v.z:0.####}]";
}
