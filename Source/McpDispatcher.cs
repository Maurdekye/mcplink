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
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = resultText },
                        },
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
