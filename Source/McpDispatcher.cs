using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpLink;

/// <summary>JSON-RPC 2.0 dispatcher for the MCP methods a tools-only server must speak.</summary>
internal sealed class McpDispatcher
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    private static readonly string[] KnownProtocolVersions = ["2024-11-05", "2025-03-26", "2025-06-18"];

    /// <returns>(response JSON or null when the body held only notifications, was this an initialize)</returns>
    public (string? json, bool isInitialize) HandlePost(string body)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (Exception e)
        {
            return (ErrorResponse(null, -32700, $"Parse error: {e.Message}").ToJsonString(), false);
        }

        bool isInitialize = false;
        if (parsed is JsonArray batch)
        {
            var results = new JsonArray();
            foreach (var item in batch)
            {
                var r = HandleSingle(item as JsonObject, ref isInitialize);
                if (r != null)
                    results.Add(r);
            }
            return (results.Count == 0 ? null : results.ToJsonString(), isInitialize);
        }

        var single = HandleSingle(parsed as JsonObject, ref isInitialize);
        return (single?.ToJsonString(), isInitialize);
    }

    private JsonObject? HandleSingle(JsonObject? message, ref bool isInitialize)
    {
        if (message == null)
            return ErrorResponse(null, -32600, "Invalid request");

        JsonNode? id = message["id"]?.DeepClone();
        bool isNotification = message["id"] == null;
        string method = message["method"]?.GetValue<string>() ?? "";
        var p = message["params"] as JsonObject;

        try
        {
            switch (method)
            {
                case "initialize":
                {
                    isInitialize = true;
                    string requested = p?["protocolVersion"]?.GetValue<string>() ?? "2025-03-26";
                    string negotiated = KnownProtocolVersions.Contains(requested) ? requested : "2025-06-18";
                    return ResultResponse(id, new JsonObject
                    {
                        ["protocolVersion"] = negotiated,
                        ["capabilities"] = new JsonObject
                        {
                            ["tools"] = new JsonObject { ["listChanged"] = false },
                        },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "McpLink",
                            ["title"] = "McpLink — in-process Resonite MCP server",
                            ["version"] = McpLinkMod.VERSION,
                        },
                        ["instructions"] =
                            "In-process Resonite access with full reflection: no ResoniteLink needed, works in any " +
                            "world including Userspace. Elements are addressed by real engine RefIDs (\"ID1A2B...\", " +
                            "or \"Root\" for the world root); RefIDs are shown in in-game inspectors and stay valid " +
                            "for the world's lifetime. Every tool takes an optional world parameter: \"focused\" " +
                            "(default), \"userspace\", or a world name. Reads run on the world's update thread; " +
                            "writes and method calls mutate the live world directly — no undo.",
                    });
                }

                case "notifications/initialized":
                case "notifications/cancelled":
                case "notifications/roots/list_changed":
                    return null;

                case "ping":
                    return ResultResponse(id, new JsonObject());

                case "tools/list":
                    return ResultResponse(id, new JsonObject { ["tools"] = ToolRegistry.DescribeTools() });

                case "tools/call":
                {
                    string name = p?["name"]?.GetValue<string>()
                                  ?? throw new ArgumentException("missing tool name");
                    var args = p?["arguments"] as JsonObject ?? new JsonObject();

                    string resultText;
                    bool isError = false;
                    try
                    {
                        resultText = ToolRegistry.Call(name, args);
                    }
                    catch (Exception e)
                    {
                        var inner = Unwrap(e);
                        resultText = JsonSerializer.Serialize(new
                        {
                            error = inner.GetType().Name,
                            message = inner.Message,
                        });
                        isError = true;
                    }

                    return ResultResponse(id, new JsonObject
                    {
                        ["content"] = ComposeContent(resultText),
                        ["isError"] = isError,
                    });
                }

                default:
                    return isNotification ? null : ErrorResponse(id, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception e)
        {
            var inner = Unwrap(e);
            return isNotification ? null : ErrorResponse(id, -32603, inner.Message);
        }
    }

    // ======================= tool-result content blocks (2.11.0) =======================

    /// <summary>The sentinel key a tool sets to emit images. Deliberately underscore-prefixed and
    /// namespaced: it is transport plumbing, not part of any tool's documented result.</summary>
    internal const string ImagesKey = "_mcpImages";

    /// <summary>
    /// Shape a tool's result string into MCP content blocks.
    ///
    /// Until 2.11.0 this was one line — every result became a single {"type":"text"} block — which
    /// is why "load a texture into your context" was impossible. The export was never the hard
    /// part; THE PIPE COULD ONLY CARRY TEXT.
    ///
    /// ⚠ THE COMPATIBILITY GUARANTEE, AND WHY IT IS WRITTEN THIS WAY. Every tool's output flows
    /// through here, so the failure mode of getting this wrong is "everything, subtly". A result
    /// without the sentinel is therefore returned BYTE-FOR-BYTE in a single text block — the same
    /// string object, never re-serialized — because a JSON round-trip would silently renormalize
    /// key order, number formatting and escaping across all 97 tools. The substring test runs
    /// before any parse so the untouched path does not even pay for one.
    ///
    /// Pure and internal so the suite can pin the passthrough rather than trust it.
    /// </summary>
    internal static JsonArray ComposeContent(string resultText)
    {
        // no sentinel anywhere in the payload ⇒ nothing to lift, and nothing to risk
        if (resultText.IndexOf(ImagesKey, StringComparison.Ordinal) < 0)
            return [TextBlock(resultText)];

        JsonObject? obj = null;
        try { obj = JsonNode.Parse(resultText) as JsonObject; }
        catch { /* not an object, or not JSON at all — fall through to passthrough */ }
        // the substring can also appear inside ordinary content (a tool reporting a file listing,
        // say). Only a real top-level array of images counts.
        if (obj?[ImagesKey] is not JsonArray images)
            return [TextBlock(resultText)];

        obj.Remove(ImagesKey);
        var content = new JsonArray { TextBlock(obj.ToJsonString()) };
        foreach (var entry in images)
        {
            if (entry is not JsonObject image)
                continue;
            string? data = image["data"]?.GetValue<string>();
            if (string.IsNullOrEmpty(data))
                continue; // an image block with no payload is worse than no block at all
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["data"] = data,
                ["mimeType"] = image["mimeType"]?.GetValue<string>() ?? "image/png",
            });
        }
        return content;
    }

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    private static Exception Unwrap(Exception e) =>
        e is AggregateException { InnerExceptions.Count: 1 } agg ? Unwrap(agg.InnerExceptions[0])
        : e is System.Reflection.TargetInvocationException { InnerException: not null } tie ? Unwrap(tie.InnerException!)
        : e;

    private static JsonObject ResultResponse(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject ErrorResponse(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
