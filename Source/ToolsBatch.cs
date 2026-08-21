using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>
/// run_batch — execute several tool calls in ONE update-thread hop, so the world (and other
/// users) never observe a half-applied state. Stronger than the ResoniteLink version: any tool
/// can be batched, and later ops can reference earlier results with "$N.path" placeholders.
/// </summary>
internal static class ToolsBatch
{
    private static readonly Regex ResultRef = new(@"^\$(\d+)(?:\.(.+))?$", RegexOptions.Compiled);

    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("run_batch",
            "Run several tool calls atomically in one world-update hop. ops = [{tool, args}, ...]; all ops run in " +
            "the batch's world. String argument values of the form \"$N.path\" are substituted with the value at " +
            "'path' in op N's result (e.g. \"$0.$ref\" = the RefID returned by op 0). stopOnError defaults true. " +
            "Check each op's response — batch success ≠ every op succeeded. transactional:true (requires " +
            "stopOnError) additionally UNDOES the already-applied ops when one fails, in the same update tick — " +
            "the world never shows a half-applied batch. The rollback is one step on the local user's undo stack " +
            "(same stack as the 'undo' tool; a 'redo' right after would re-apply the reverted prefix). It covers " +
            "world mutations only — non-world side effects (file writes, renders) are NOT rolled back. On failure " +
            "the result gains rolledBack (and rollbackError if the rollback itself failed).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"ops\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
            "\"tool\":{\"type\":\"string\"},\"args\":{\"type\":\"object\"}},\"required\":[\"tool\"]}}," +
            "\"stopOnError\":{\"type\":\"boolean\",\"default\":true}," +
            "\"transactional\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Undo applied ops if any op fails (needs stopOnError:true).\"}}," +
            "\"required\":[\"ops\"]}",
            args =>
            {
                // cheap argument validation before any engine access
                var ops = args["ops"] as JsonArray
                          ?? throw new ArgumentException("Missing 'ops' array");
                bool stopOnError = OptBool(args, "stopOnError", true);
                bool transactional = OptBool(args, "transactional", false);
                if (transactional && !stopOnError)
                    throw new ArgumentException(
                        "transactional:true requires stopOnError:true — rollback triggers on the first failed op");
                var world = GetWorld(args);
                string? worldSpec = OptString(args, "world");

                // One Run for the whole batch; nested tool handlers run inline (WorldRunner reentrancy).
                // One UNDO batch for the whole batch too: N mutations = one Ctrl+Z in-game.
                return WorldRunner.Run(world, () =>
                {
                    bool anyFailed = false;
                    JsonObject RunOps()
                    {
                        var results = new JsonArray();
                        var resultValues = new List<JsonNode?>();
                        foreach (var opNode in ops)
                        {
                            if (opNode is not JsonObject op)
                            {
                                results.Add(new JsonObject { ["success"] = false, ["error"] = "op is not an object" });
                                resultValues.Add(null);
                                anyFailed = true;
                                if (stopOnError) break;
                                continue;
                            }
                            string tool = op["tool"]?.GetValue<string>()
                                          ?? throw new ArgumentException("op missing 'tool'");
                            var opArgs = (op["args"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
                            if (opArgs["world"] == null && worldSpec != null)
                                opArgs["world"] = worldSpec;

                            try
                            {
                                var substituted = (JsonObject)Substitute(opArgs, resultValues)!;
                                var resultJson = JsonNode.Parse(ToolRegistry.Call(tool, substituted));
                                results.Add(new JsonObject { ["success"] = true, ["tool"] = tool, ["result"] = resultJson });
                                resultValues.Add(results[^1]!["result"]);
                            }
                            catch (Exception e)
                            {
                                results.Add(new JsonObject { ["success"] = false, ["tool"] = tool, ["error"] = e.Message });
                                resultValues.Add(null);
                                anyFailed = true;
                                if (stopOnError)
                                    break;
                            }
                        }
                        return new JsonObject
                        {
                            ["completed"] = results.Count,
                            ["requested"] = ops.Count,
                            ["results"] = results,
                        };
                    }

                    if (!transactional)
                        return UndoUtil.Batch(world, $"batch of {ops.Count} ops", () => (JsonNode)RunOps());

                    // Transactional: still ONE Run tick — the batch is applied, ended, and (on
                    // failure) undone before the world advances a frame, so no other user or
                    // client ever observes the half-applied prefix.
                    var result = UndoUtil.BatchTransactional(world, $"batch of {ops.Count} ops (transactional)",
                        RunOps, () => anyFailed, out bool rolledBack, out string? rollbackError);
                    if (anyFailed)
                    {
                        result["rolledBack"] = rolledBack;
                        if (rollbackError != null)
                            result["rollbackError"] = rollbackError;
                    }
                    return (JsonNode)result;
                }, timeoutMs: 60000);
            }));
    }

    /// <summary>Recursively replace "$N.path" strings with values from earlier op results.</summary>
    private static JsonNode? Substitute(JsonNode? node, List<JsonNode?> results)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var replaced = new JsonObject();
                foreach (var (key, value) in obj)
                    replaced[key] = Substitute(value, results);
                return replaced;
            }
            case JsonArray array:
            {
                var replaced = new JsonArray();
                foreach (var item in array)
                    replaced.Add(Substitute(item, results));
                return replaced;
            }
            case JsonValue value when value.TryGetValue<string>(out var text):
            {
                var match = ResultRef.Match(text);
                if (!match.Success)
                    return value.DeepClone();
                int index = int.Parse(match.Groups[1].Value);
                if (index >= results.Count || results[index] == null)
                    throw new ArgumentException($"'{text}' references op {index}, which has no result");
                JsonNode? current = results[index];
                if (match.Groups[2].Success)
                {
                    foreach (string segment in match.Groups[2].Value.Split('.'))
                    {
                        current = current switch
                        {
                            JsonObject o => o[segment],
                            JsonArray a when int.TryParse(segment, out int i) && i < a.Count => a[i],
                            _ => null,
                        };
                        if (current == null)
                            throw new ArgumentException($"'{text}': path segment '{segment}' not found in op {index}'s result");
                    }
                }
                return current?.DeepClone();
            }
            default:
                return node?.DeepClone();
        }
    }
}
