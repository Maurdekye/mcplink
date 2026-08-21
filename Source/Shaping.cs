using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FrooxEngine;

namespace McpLink;

/// <summary>Output-shaping helpers ported from resomcp's token discipline.</summary>
internal static class Shaping
{
    private static readonly Regex RichText = new("<[^<>]{1,64}?>", RegexOptions.Compiled);

    /// <summary>Strip rich-text markup (&lt;color=...&gt; etc.) from names.</summary>
    public static string? Strip(string? text) =>
        string.IsNullOrEmpty(text) || text.IndexOf('<') < 0 ? text : RichText.Replace(text, "");

    /// <summary>Path breadcrumb from an ancestor (exclusive) down to a slot.</summary>
    public static string Path(Slot slot, Slot? stopBelow = null)
    {
        var parts = new List<string>();
        for (var current = slot; current != null && current != stopBelow; current = current.Parent)
        {
            parts.Add(Strip(current.Name) ?? "");
            if (parts.Count > 32)
                break;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>
    /// Enforce a byte budget on a result: when exceeded, return the fallback (e.g. a summary)
    /// or a truncation notice with guidance instead of a huge payload.
    /// </summary>
    public static JsonNode Budget(JsonNode full, int maxBytes, Func<JsonNode>? fallback = null, string? hint = null)
    {
        if (maxBytes <= 0)
            return full;
        int bytes = System.Text.Encoding.UTF8.GetByteCount(full.ToJsonString());
        if (bytes <= maxBytes)
            return full;
        if (fallback != null)
        {
            var summary = fallback();
            summary["note"] = $"full result was {bytes} bytes (> maxBytes {maxBytes}); returned summary instead";
            return summary;
        }
        return new JsonObject
        {
            ["truncated"] = true,
            ["bytes"] = bytes,
            ["maxBytes"] = maxBytes,
            ["hint"] = hint ?? "Narrow the query (lower depth, add filters) or raise maxBytes.",
        };
    }
}
