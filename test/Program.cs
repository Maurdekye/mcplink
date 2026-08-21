// Offline smoke test for McpLink: exercises everything that doesn't need a RUNNING engine —
// the JSON-RPC dispatcher, tool registry and all hand-written schemas, type resolution
// (including the exact lookups the fire tool performs), and world-free value decoding.
// Engine assemblies are loaded for metadata only.

using System.Reflection;
using System.Text.Json.Nodes;
using McpLink;

const string ResonitePath = @"C:\Program Files (x86)\Steam\steamapps\common\Resonite";

AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    string fileName = new AssemblyName(e.Name).Name + ".dll";
    foreach (var dir in new[] { ResonitePath, Path.Combine(ResonitePath, "Libraries"), Path.Combine(ResonitePath, "rml_libs") })
    {
        string candidate = Path.Combine(dir, fileName);
        if (File.Exists(candidate))
            return Assembly.LoadFrom(candidate);
    }
    return null;
};

// preload so short-name / bindings type searches see them
Assembly.LoadFrom(Path.Combine(ResonitePath, "FrooxEngine.dll"));
Assembly.LoadFrom(Path.Combine(ResonitePath, "ProtoFluxBindings.dll"));
Assembly.LoadFrom(Path.Combine(ResonitePath, "Renderite.Shared.dll"));
Assembly.LoadFrom(Path.Combine(ResonitePath, "ProtoFlux.Nodes.FrooxEngine.dll")); // DynamicImpulseHelper

int failed = 0, passed = 0;
void Check(string name, Func<bool> test)
{
    try
    {
        if (test())
        {
            passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            failed++;
            Console.WriteLine($"! FAIL  {name}");
        }
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"! FAIL  {name} — {e.GetType().Name}: {e.Message}");
    }
}

var dispatcher = new McpDispatcher();

Console.WriteLine("== dispatcher ==");
Check("initialize returns negotiated version + serverInfo", () =>
{
    var (json, isInit) = dispatcher.HandlePost(
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}""");
    var result = JsonNode.Parse(json!)!["result"]!;
    return isInit
           && result["protocolVersion"]!.GetValue<string>() == "2025-06-18"
           && result["serverInfo"]!["name"]!.GetValue<string>() == "McpLink"
           && result["capabilities"]!["tools"] != null;
});
Check("unknown protocol version falls back", () =>
{
    var (json, _) = dispatcher.HandlePost(
        """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"9999-01-01"}}""");
    return JsonNode.Parse(json!)!["result"]!["protocolVersion"]!.GetValue<string>() == "2025-06-18";
});
Check("notifications/initialized produces no response", () =>
{
    var (json, _) = dispatcher.HandlePost("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
    return json == null;
});
Check("ping", () =>
{
    var (json, _) = dispatcher.HandlePost("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");
    return JsonNode.Parse(json!)!["result"] is JsonObject;
});
Check("malformed JSON → -32700", () =>
{
    var (json, _) = dispatcher.HandlePost("{not json");
    return JsonNode.Parse(json!)!["error"]!["code"]!.GetValue<int>() == -32700;
});
Check("unknown method with id → -32601", () =>
{
    var (json, _) = dispatcher.HandlePost("""{"jsonrpc":"2.0","id":4,"method":"nope"}""");
    return JsonNode.Parse(json!)!["error"]!["code"]!.GetValue<int>() == -32601;
});
Check("unknown notification is swallowed", () =>
{
    var (json, _) = dispatcher.HandlePost("""{"jsonrpc":"2.0","method":"notifications/whatever"}""");
    return json == null;
});

Console.WriteLine("== tools/list & schemas ==");
JsonArray tools = null!;
Check("tools/list returns tools", () =>
{
    var (json, _) = dispatcher.HandlePost("""{"jsonrpc":"2.0","id":5,"method":"tools/list"}""");
    tools = (JsonArray)JsonNode.Parse(json!)!["result"]!["tools"]!;
    Console.WriteLine($"        ({tools.Count} tools registered)");
    return tools.Count >= 30;
});
Check("every tool has a valid object schema + description", () =>
{
    bool ok = true;
    foreach (var tool in tools)
    {
        string name = tool!["name"]!.GetValue<string>();
        var schema = tool["inputSchema"] as JsonObject;
        if (schema?["type"]?.GetValue<string>() != "object" ||
            string.IsNullOrWhiteSpace(tool["description"]?.GetValue<string>()))
        {
            Console.WriteLine($"        bad schema/description: {name}");
            ok = false;
        }
        // required properties must exist in properties
        if (schema?["required"] is JsonArray required)
        {
            var properties = schema["properties"] as JsonObject;
            foreach (var requiredName in required)
            {
                if (properties?[requiredName!.GetValue<string>()] == null)
                {
                    Console.WriteLine($"        {name}: required '{requiredName}' missing from properties");
                    ok = false;
                }
            }
        }
    }
    return ok;
});
Check("tools/call with unknown tool → isError result", () =>
{
    var (json, _) = dispatcher.HandlePost(
        """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"no_such_tool","arguments":{}}}""");
    var result = JsonNode.Parse(json!)!["result"]!;
    return result["isError"]!.GetValue<bool>();
});
Check("tools/call with missing args → isError, structured message", () =>
{
    var (json, _) = dispatcher.HandlePost(
        """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"describe_type","arguments":{}}}""");
    var result = JsonNode.Parse(json!)!["result"]!;
    string text = result["content"]![0]!["text"]!.GetValue<string>();
    return result["isError"]!.GetValue<bool>() && text.Contains("type");
});

Console.WriteLine("== engine-free tool calls ==");
Check("describe_type resolves Slot (sync members listed)", () =>
{
    string json = ToolRegistry.Call("describe_type", new JsonObject { ["type"] = "Slot" });
    var result = JsonNode.Parse(json)!;
    return result["fullName"]!.GetValue<string>() == "FrooxEngine.Slot"
           && result["syncMembers"] is JsonObject members && members.Count > 3;
});
Check("describe_type enum values", () =>
{
    string json = ToolRegistry.Call("describe_type", new JsonObject { ["type"] = "Renderite.Shared.TextureFilterMode" });
    return JsonNode.Parse(json)!["values"] is JsonObject values && values.Count > 2;
});
Check("get_enum_definition alias redirects", () =>
{
    string json = ToolRegistry.Call("get_enum_definition", new JsonObject { ["type"] = "Renderite.Shared.TextureFilterMode" });
    return JsonNode.Parse(json)!["values"] is JsonObject;
});
Check("list_component_types finds ValueField", () =>
{
    string json = ToolRegistry.Call("list_component_types", new JsonObject { ["pattern"] = "ValueField", ["limit"] = 20 });
    return JsonNode.Parse(json)!["count"]!.GetValue<int>() > 0;
});

Console.WriteLine("== strict arg validation ==");
Check("unknown argument is rejected with the accepted list", () =>
{
    try
    {
        ToolRegistry.Call("find_slots", new JsonObject { ["rootId"] = "Root", ["root"] = "Root", ["namePattern"] = "x" });
        return false;
    }
    catch (ArgumentException e)
    {
        return e.Message.Contains("'root'") && e.Message.Contains("rootId");
    }
});
Check("universal args (maxBytes, world) pass validation on any tool", () =>
{
    // engine-free tool: reaches the handler (and succeeds) despite extra universal args
    string json = ToolRegistry.Call("describe_type",
        new JsonObject { ["type"] = "Slot", ["maxBytes"] = 100000, ["world"] = "focused" });
    return JsonNode.Parse(json)!["fullName"]!.GetValue<string>() == "FrooxEngine.Slot";
});
Check("RequireAny aliases pass validation (import_file filePath)", () =>
{
    try
    {
        ToolRegistry.Call("import_file", new JsonObject { ["filePath"] = @"Z:\definitely\missing.png" });
        return false; // should have failed on the missing file, not on the arg name
    }
    catch (ArgumentException e) when (e.Message.Contains("Unknown argument"))
    {
        return false;
    }
    catch (Exception)
    {
        return true; // FileNotFound / engine-not-ready = validation was passed
    }
});
Check("declared undoable arg on destroy passes validation", () =>
{
    try
    {
        ToolRegistry.Call("destroy", new JsonObject { ["id"] = "ID1", ["undoable"] = false });
        return false;
    }
    catch (ArgumentException e) when (e.Message.Contains("Unknown argument"))
    {
        return false;
    }
    catch (Exception)
    {
        return true; // failed at world resolution (no engine), not at validation
    }
});

Console.WriteLine("== TypeUtil ==");
Check("alias: float3", () => TypeUtil.Resolve("float3").FullName == "Elements.Core.float3");
Check("short name: DynamicVariableSpace", () =>
    TypeUtil.Resolve("DynamicVariableSpace").FullName == "FrooxEngine.DynamicVariableSpace");
Check("C# generics: ValueField<float3>", () =>
{
    var type = TypeUtil.Resolve("ValueField<float3>");
    return type.IsGenericType && type.GetGenericArguments()[0].Name == "float3";
});
Check("resomcp bracket style: [FrooxEngine]FrooxEngine.DynamicReferenceVariable<[FrooxEngine]FrooxEngine.Slot>", () =>
{
    var type = TypeUtil.Resolve("[FrooxEngine]FrooxEngine.DynamicReferenceVariable<[FrooxEngine]FrooxEngine.Slot>");
    return type.GetGenericArguments()[0].Name == "Slot";
});
Check("fire's lookup: ...Nodes.ValueInput`1 + MakeGenericType(bool)", () =>
{
    var def = TypeUtil.Resolve("FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.ValueInput`1");
    var closed = def.MakeGenericType(typeof(bool));
    return typeof(FrooxEngine.Component).IsAssignableFrom(closed);
});
Check("fire's lookup: ...Nodes.Actions.FireOnTrue", () =>
    typeof(FrooxEngine.Component).IsAssignableFrom(
        TypeUtil.Resolve("FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.Actions.FireOnTrue")));
Check("FriendlyName renders generics", () =>
    TypeUtil.FriendlyName(TypeUtil.Resolve("ValueField<float3>")) == "ValueField<float3>");

Console.WriteLine("== Encode.Decode (world-free) ==");
Check("float3 from {x,y,z}", () =>
{
    var v = (Elements.Core.float3)Encode.Decode(JsonNode.Parse("""{"x":1,"y":2,"z":3}"""), typeof(Elements.Core.float3), null!)!;
    return v.x == 1 && v.y == 2 && v.z == 3;
});
Check("float3 from [x,y,z]", () =>
{
    var v = (Elements.Core.float3)Encode.Decode(JsonNode.Parse("[4,5,6]"), typeof(Elements.Core.float3), null!)!;
    return v.x == 4 && v.y == 5 && v.z == 6;
});
Check("typed literal {$type:float3}", () =>
{
    var v = (Elements.Core.float3)Encode.Decode(
        JsonNode.Parse("""{"$type":"float3","value":{"x":7,"y":8,"z":9}}"""), typeof(object), null!)!;
    return v.y == 8;
});
Check("colorX from [r,g,b,a] (1.0 constructor-arity fallback)", () =>
{
    var c = (Elements.Core.colorX)Encode.Decode(JsonNode.Parse("[1,0.5,0.25,1]"), typeof(Elements.Core.colorX), null!)!;
    var c3 = (Elements.Core.colorX)Encode.Decode(JsonNode.Parse("[0.1,0.2,0.3]"), typeof(Elements.Core.colorX), null!)!;
    return Math.Abs(c.g - 0.5f) < 1e-5 && Math.Abs(c3.b - 0.3f) < 1e-5 && c.a == 1f;
});
Check("floatQ from {x,y,z,w}", () =>
{
    var q = (Elements.Core.floatQ)Encode.Decode(JsonNode.Parse("""{"x":0,"y":0,"z":0,"w":1}"""), typeof(Elements.Core.floatQ), null!)!;
    return q.w == 1;
});
Check("enum by name", () =>
{
    var v = Encode.Decode(JsonNode.Parse("\"Point\""), TypeUtil.Resolve("Renderite.Shared.TextureFilterMode"), null!)!;
    return v.ToString() == "Point";
});
Check("int coercion from JSON number", () =>
    (int)Encode.Decode(JsonNode.Parse("42"), typeof(int), null!)! == 42);
Check("bool passthrough", () =>
    (bool)Encode.Decode(JsonNode.Parse("true"), typeof(bool), null!)!);
Check("encode round-trip: float3 → {x,y,z}", () =>
{
    var node = Encode.Value(new Elements.Core.float3(1, 2, 3))!;
    return node["x"]!.GetValue<float>() == 1 && node["z"]!.GetValue<float>() == 3;
});
// Uri regressions (atelier session): bare strings rejected; the {"$type":"Uri","$string":...}
// form (Encode.Value's own output shape!) silently decoded to null and nulled a mesh URL.
Check("Uri from bare string (local:// scheme)", () =>
{
    var v = (Uri)Encode.Decode(JsonNode.Parse("\"local://machine/asset.meshx\""), typeof(Uri), null!)!;
    return v.Scheme == "local";
});
Check("Uri from {$type:Uri, value}", () =>
{
    var v = (Uri)Encode.Decode(
        JsonNode.Parse("""{"$type":"Uri","value":"resdb:///abc123"}"""), typeof(Uri), null!)!;
    return v.Scheme == "resdb";
});
Check("Uri from {$type:Uri, $string} (encode-output round-trip)", () =>
{
    var v = (Uri)Encode.Decode(
        JsonNode.Parse("""{"$type":"Uri","$string":"local://machine/asset.meshx"}"""), typeof(Uri), null!)!;
    return v.Host == "machine";
});

Console.WriteLine("== v0.6 features (engine-free) ==");
Check("new tools registered", () =>
{
    string[] expected = ["logs", "watch_changes", "changes", "unwatch", "save_object", "load_object",
        "undo", "redo", "dynamic_impulse", "user_pointer", "marker", "notify", "export_asset", "jump_user",
        "eval", "inventory", "find_assets",
        "mv", "diff", "top", "history", "at", "jobs", "cancel_job", "xargs", "orbit_render",
        "bookmark", "bookmarks",
        "impulse_watch", "impulse_events", "impulse_unwatch"];
    var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
    var missing = expected.Where(e => !names.Contains(e)).ToList();
    if (missing.Count > 0)
        Console.WriteLine($"        missing: {string.Join(", ", missing)}");
    return missing.Count == 0;
});
Check("render_view/orbit_render expose isolate + exclude (v1.6)", () =>
{
    foreach (string toolName in new[] { "render_view", "orbit_render" })
    {
        var tool = tools.FirstOrDefault(t => t!["name"]!.GetValue<string>() == toolName);
        var properties = tool?["inputSchema"]?["properties"] as JsonObject;
        if (properties?["isolate"] == null || properties["exclude"] == null)
        {
            Console.WriteLine($"        {toolName} missing isolate/exclude");
            return false;
        }
    }
    return true;
});
Check("logs captures UniLog output with level + seq", () =>
{
    McpLink.LogCapture.Start();
    Elements.Core.UniLog.Log("mcplink smoke log line");
    Elements.Core.UniLog.Warning("mcplink smoke warning line");
    string json = ToolRegistry.Call("logs", new JsonObject { ["pattern"] = "mcplink smoke", ["level"] = "all" });
    var result = JsonNode.Parse(json)!;
    var entries = (JsonArray)result["entries"]!;
    return entries.Count >= 2
           && entries.Any(e => e!["level"]!.GetValue<string>() == "warning")
           && result["lastSeq"]!.GetValue<long>() >= 2;
});
Check("logs level filter narrows results", () =>
{
    string json = ToolRegistry.Call("logs", new JsonObject { ["pattern"] = "mcplink smoke", ["level"] = "warning" });
    var entries = (JsonArray)JsonNode.Parse(json)!["entries"]!;
    return entries.Count >= 1 && entries.All(e => e!["level"]!.GetValue<string>() == "warning");
});
Check("logs sinceSeq pagination", () =>
{
    string first = ToolRegistry.Call("logs", new JsonObject { ["pattern"] = "mcplink smoke" });
    long lastSeq = JsonNode.Parse(first)!["lastSeq"]!.GetValue<long>();
    string second = ToolRegistry.Call("logs", new JsonObject { ["pattern"] = "mcplink smoke", ["sinceSeq"] = lastSeq });
    return JsonNode.Parse(second)!["count"]!.GetValue<int>() == 0;
});
Check("changes with unknown watch id → helpful error", () =>
{
    try
    {
        ToolRegistry.Call("changes", new JsonObject { ["watchId"] = "nope" });
        return false;
    }
    catch (ArgumentException e)
    {
        return e.Message.Contains("No watch");
    }
});
Check("unwatch all with no watches", () =>
{
    string json = ToolRegistry.Call("unwatch", new JsonObject { ["watchId"] = "all" });
    return JsonNode.Parse(json)!["active"]!.GetValue<int>() == 0;
});
Check("DataTree round-trip through .brson (save_object's serializer)", () =>
{
    var dict = new Elements.Core.DataTreeDictionary();
    dict.Add("hello", new Elements.Core.DataTreeValue("world"));
    string path = Path.Combine(Path.GetTempPath(), "McpLink", "smoke_roundtrip.brson");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    Elements.Core.DataTreeConverter.Save(dict, path, Elements.Core.DataTreeConverter.Compression.Brotli);
    var loaded = Elements.Core.DataTreeConverter.Load(path, (string?)null);
    return loaded["hello"] is Elements.Core.DataTreeValue value && value.Extract<string>() == "world";
});
Check("DynamicImpulseHelper: all four trigger paths resolve + instance created", () =>
{
    var voidSync = ToolsInteract.HelperMethod("TriggerDynamicImpulse", 4, generic: false);
    var voidAsync = ToolsInteract.HelperMethod("TriggerAsyncDynamicImpulse", 4, generic: false);
    var withArg = ToolsInteract.HelperMethod("TriggerDynamicImpulseWithArgument", 5, generic: true);
    var asyncWithArg = ToolsInteract.HelperMethod("TriggerAsyncDynamicImpulseWithArgument", 5, generic: true);
    // the convenient overloads are instance methods — the resolver must supply a target
    bool targetsOk = (voidSync.method.IsStatic || voidSync.target != null)
                     && (withArg.method.IsStatic || withArg.target != null);
    // generic definitions must close over a payload type
    var closed = withArg.method.MakeGenericMethod(typeof(Elements.Core.float3));
    return targetsOk && voidAsync.method != null && asyncWithArg.method != null && !closed.IsGenericMethodDefinition;
});
Check("dynamic_impulse payload type inference", () =>
    ToolsInteract.InferPayloadType(JsonNode.Parse("true")!, null!) == typeof(bool)
    && ToolsInteract.InferPayloadType(JsonNode.Parse("42")!, null!) == typeof(int)
    && ToolsInteract.InferPayloadType(JsonNode.Parse("4.5")!, null!) == typeof(float)
    && ToolsInteract.InferPayloadType(JsonNode.Parse("\"hi\"")!, null!) == typeof(string)
    && ToolsInteract.InferPayloadType(JsonNode.Parse("[1,2,3]")!, null!) == typeof(Elements.Core.float3));
Check("NotificationPanel.ShowNotification signature + NotificationType values", () =>
{
    var method = typeof(FrooxEngine.NotificationPanel).GetMethod("ShowNotification",
        BindingFlags.Public | BindingFlags.Static);
    if (method == null || method.GetParameters().Length != 5)
        return false;
    var enumType = method.GetParameters()[4].ParameterType;
    return enumType.IsEnum
           && Enum.GetNames(enumType).Contains("Full")
           && Enum.GetNames(enumType).Contains("ToastOnly");
});
Check("notify tool no-ops safely without a dash (engine-free)", () =>
{
    string json = ToolRegistry.Call("notify", new JsonObject { ["message"] = "smoke" });
    return JsonNode.Parse(json)!["shown"]!.GetValue<bool>();
});
Check("UndoManagerExtensions.GetUndoManager(World, bool) present", () =>
{
    var method = typeof(FrooxEngine.Undo.UndoManagerExtensions).GetMethod("GetUndoManager");
    var parameters = method?.GetParameters();
    return parameters is { Length: 2 }
           && parameters[0].ParameterType == typeof(FrooxEngine.World)
           && parameters[1].ParameterType == typeof(bool);
});
Check("Engine.AssetManager.GatherAssetFile present (export_asset)", () =>
{
    var method = typeof(FrooxEngine.AssetManager).GetMethod("GatherAssetFile");
    return method != null && method.GetParameters()[0].ParameterType == typeof(Uri);
});
Check("DependencyHandling parses save_object's default", () =>
    Enum.TryParse<FrooxEngine.DependencyHandling>("CollectAssets", true, out _));
Check("save_object rejects a bad dependencies mode with the valid list", () =>
{
    try
    {
        ToolRegistry.Call("save_object", new JsonObject { ["id"] = "ID1", ["dependencies"] = "Whatever" });
        return false;
    }
    catch (ArgumentException e)
    {
        return e.Message.Contains("CollectAssets");
    }
});
Check("load_object requires an existing file", () =>
{
    try
    {
        ToolRegistry.Call("load_object", new JsonObject { ["path"] = @"Z:\definitely\missing.brson" });
        return false;
    }
    catch (Exception e)
    {
        return e is FileNotFoundException || e.Message.Contains("No file");
    }
});

Console.WriteLine("== v1.3 features (engine-free) ==");
Check("v1.3 tools registered (flux_ports, flux_splice)", () =>
{
    var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
    return names.Contains("flux_ports") && names.Contains("flux_splice");
});
Check("fire's primary arg is 'id'; 'operationId' still accepted as alias", () =>
{
    var schema = tools.First(t => t!["name"]!.GetValue<string>() == "fire")!["inputSchema"]!;
    bool schemaOk = ((JsonArray)schema["required"]!).Any(r => r!.GetValue<string>() == "id");
    try
    {
        ToolRegistry.Call("fire", new JsonObject { ["operationId"] = "ID1" });
        return false; // no engine — must fail past arg handling
    }
    catch (ArgumentException e) when (e.Message.Contains("Unknown argument") || e.Message.Contains("Missing required"))
    {
        return false;
    }
    catch (Exception)
    {
        return schemaOk; // reached world resolution — the alias was rewritten to 'id'
    }
    finally { }
});
Check("passing both an alias and its canonical name is rejected", () =>
{
    try
    {
        ToolRegistry.Call("fire", new JsonObject { ["id"] = "ID1", ["operationId"] = "ID2" });
        return false;
    }
    catch (ArgumentException e)
    {
        return e.Message.Contains("'id'") && e.Message.Contains("'operationId'");
    }
});
Check("'id' alias accepted on rooty tools (get_protoflux_subgraph, find_referrers, flux_ports)", () =>
{
    foreach (var (tool, argName) in new[]
             { ("get_protoflux_subgraph", "id"), ("find_referrers", "id"), ("flux_ports", "nodeId") })
    {
        try
        {
            ToolRegistry.Call(tool, new JsonObject { [argName] = "ID1" });
            return false; // no engine — must fail later than validation
        }
        catch (ArgumentException e) when (e.Message.Contains("Unknown argument") || e.Message.Contains("Missing required"))
        {
            Console.WriteLine($"        alias rejected on {tool}: {e.Message}");
            return false;
        }
        catch (Exception)
        {
            // reached world resolution — alias worked
        }
    }
    return true;
});
Check("save_object dependencies:false no longer explodes in bool→string conversion", () =>
{
    try
    {
        ToolRegistry.Call("save_object", new JsonObject { ["id"] = "ID1", ["dependencies"] = false });
        return false; // no engine — must fail at world resolution
    }
    catch (Exception e)
    {
        // the v1.2 bug: InvalidOperationException "An element of type 'False' cannot be converted..."
        return !e.Message.Contains("'False'") && !e.Message.Contains("System.String");
    }
});
Check("save_object dependencies:true parses as CollectAssets (no enum error)", () =>
{
    try
    {
        ToolRegistry.Call("save_object", new JsonObject { ["id"] = "ID1", ["dependencies"] = true });
        return false;
    }
    catch (ArgumentException e) when (e.Message.Contains("Unknown dependencies"))
    {
        return false;
    }
    catch (Exception)
    {
        return true; // got past the dependencies decode to world resolution
    }
});
Check("flux_connect without toId demands toId or disconnect:true", () =>
{
    try
    {
        ToolRegistry.Call("flux_connect", new JsonObject { ["nodeId"] = "ID1", ["port"] = "Condition" });
        return false;
    }
    catch (ArgumentException e)
    {
        return e.Message.Contains("toId") && e.Message.Contains("disconnect");
    }
});
Check("flux_connect disconnect:true passes validation without toId", () =>
{
    try
    {
        ToolRegistry.Call("flux_connect", new JsonObject
        {
            ["nodeId"] = "ID1", ["port"] = "Condition", ["disconnect"] = true,
        });
        return false; // no engine — must fail at world resolution
    }
    catch (ArgumentException)
    {
        return false;
    }
    catch (Exception)
    {
        return true;
    }
});
Check("flux_splice validates its required args before touching the world", () =>
{
    try
    {
        ToolRegistry.Call("flux_splice", new JsonObject { ["nodeId"] = "ID1", ["port"] = "Next" });
        return false;
    }
    catch (ArgumentException e)
    {
        return e.Message.Contains("insertId");
    }
});
Check("GlobalProxyValueType extracts T from IGlobalValueProxy<T> (flux_build globals)", () =>
    ToolsFlux.GlobalProxyValueType(typeof(FrooxEngine.ProtoFlux.IGlobalValueProxy<string>)) == typeof(string)
    && ToolsFlux.GlobalProxyValueType(typeof(FrooxEngine.Slot)) == null);
Check("GlobalValue<T> closes and implements IGlobalValueProxy<T> (the engine idiom)", () =>
{
    var closed = typeof(FrooxEngine.ProtoFlux.GlobalValue<>).MakeGenericType(typeof(string));
    return typeof(FrooxEngine.Component).IsAssignableFrom(closed)
           && typeof(FrooxEngine.ProtoFlux.IGlobalValueProxy<string>).IsAssignableFrom(closed);
});
Check("FindFreePosition avoids occupied spots and matches neighbor spacing", () =>
{
    var origin = new Elements.Core.float3(0, 0, 0);
    // neighbor 0.3 below → step derives to 0.3; below is taken, above is free
    var occupied = new List<Elements.Core.float3> { new(0, -0.3f, 0) };
    var spot = ToolsFlux.FindFreePosition(origin, occupied);
    bool avoided = (spot - occupied[0]).Magnitude > 0.12f;
    bool spacing = Math.Abs(spot.y - 0.3f) < 1e-4 && spot.x == 0; // (0, +0.3, 0)
    // fully free space → first candidate is one step below
    var free = ToolsFlux.FindFreePosition(origin, new List<Elements.Core.float3>());
    return avoided && spacing && Math.Abs(free.y + 0.18f) < 1e-4;
});
Check("ENGINE DRIFT GUARD: eval_output evaluation path members all present", () =>
{
    var groupType = typeof(FrooxEngine.ProtoFlux.ProtoFluxNodeGroup);
    var runtimeField = groupType.GetField("executionRuntime", BindingFlags.NonPublic | BindingFlags.Instance);
    bool fieldOk = runtimeField != null
        && typeof(ProtoFlux.Runtimes.Execution.ExecutionRuntime<FrooxEngine.ProtoFlux.FrooxEngineContext>)
            .IsAssignableFrom(runtimeField.FieldType);
    bool mappedOk = typeof(FrooxEngine.ProtoFlux.INodeOutput).GetProperty("MappedOutput") != null;
    bool evalOk = typeof(ProtoFlux.Runtimes.Execution.IExecutionRuntime).GetMethod("EvaluateValue") is { IsGenericMethod: true }
                  && typeof(ProtoFlux.Runtimes.Execution.IExecutionRuntime).GetMethod("EvaluateObject") is { IsGenericMethod: true };
    var controllerType = typeof(FrooxEngine.ProtoFlux.ProtoFluxController);
    bool contextOk = controllerType.GetMethod("BorrowContext") != null
                     && controllerType.GetMethod("ReturnContext") != null
                     && typeof(ProtoFlux.Runtimes.Execution.ExecutionContext).GetMethod("PinFrame") != null
                     && typeof(ProtoFlux.Runtimes.Execution.ExecutionContext).GetMethod("UnpinFrame") != null;
    bool flowErrorOk = groupType.GetProperty("LastImpulseFlowError") != null; // fire feedback
    if (!fieldOk) Console.WriteLine("        executionRuntime field drifted");
    if (!mappedOk) Console.WriteLine("        INodeOutput.MappedOutput drifted");
    if (!evalOk) Console.WriteLine("        IExecutionRuntime.Evaluate* drifted");
    if (!contextOk) Console.WriteLine("        context borrow/pin API drifted");
    if (!flowErrorOk) Console.WriteLine("        LastImpulseFlowError drifted");
    return fieldOk && mappedOk && evalOk && contextOk && flowErrorOk;
});

Console.WriteLine("== shell tools (engine-free) ==");
Check("shell aliases redirect (rm/cat/ps/schedule)", () =>
{
    foreach (var (alias, requiredArg) in new[] { ("rm", "id"), ("cat", "id") })
    {
        try
        {
            ToolRegistry.Call(alias, new JsonObject { [requiredArg] = "ID1" });
            return false; // no engine — must fail past the alias, not on 'Unknown tool'
        }
        catch (ArgumentException e) when (e.Message.Contains("Unknown tool"))
        {
            return false;
        }
        catch (Exception)
        {
            // reached the real handler (failed at world resolution) — alias works
        }
    }
    return true;
});
Check("jobs lists empty registry", () =>
{
    string json = ToolRegistry.Call("jobs", new JsonObject());
    return JsonNode.Parse(json)!["count"]!.GetValue<int>() == 0;
});
Check("cancel_job unknown id → helpful error", () =>
{
    try
    {
        ToolRegistry.Call("cancel_job", new JsonObject { ["jobId"] = "nope" });
        return false;
    }
    catch (ArgumentException e)
    {
        return e.Message.Contains("No job");
    }
});
Check("xargs requires a filter before touching the world", () =>
{
    try
    {
        ToolRegistry.Call("xargs", new JsonObject
        {
            ["tool"] = "update_slot",
            ["argsTemplate"] = new JsonObject { ["id"] = "$id" },
            ["dryRun"] = true,
        });
        return false;
    }
    catch (ArgumentException e)
    {
        return e.Message.Contains("namePattern");
    }
});
Check("bookmark set/list/resolve-chain/delete round-trip", () =>
{
    ToolRegistry.Call("bookmark", new JsonObject { ["name"] = "gun", ["id"] = "ID1A2B00" });
    ToolRegistry.Call("bookmark", new JsonObject { ["name"] = "same", ["id"] = "@gun" }); // chains resolve
    string listJson = ToolRegistry.Call("bookmarks", new JsonObject());
    var list = JsonNode.Parse(listJson)!;
    bool listed = list["count"]!.GetValue<int>() == 2
                  && list["bookmarks"]!["@same"]!.GetValue<string>() == "ID1A2B00";
    bool resolved = ToolsShell.ResolveBookmark("GUN") == "ID1A2B00"; // case-insensitive
    ToolRegistry.Call("bookmark", new JsonObject { ["name"] = "gun", ["delete"] = true });
    ToolRegistry.Call("bookmark", new JsonObject { ["name"] = "same", ["delete"] = true });
    bool gone;
    try { ToolsShell.ResolveBookmark("gun"); gone = false; }
    catch (ArgumentException) { gone = true; }
    return listed && resolved && gone;
});
Check("bookmark rejects a non-RefID id", () =>
{
    try
    {
        ToolRegistry.Call("bookmark", new JsonObject { ["name"] = "bad", ["id"] = "not-a-refid" });
        return false;
    }
    catch (ArgumentException e)
    {
        return e.Message.Contains("not a RefID");
    }
});
Check("mv validates its argument combinations", () =>
{
    try
    {
        ToolRegistry.Call("mv", new JsonObject { ["id"] = "ID1" }); // neither parentId nor name
        return false;
    }
    catch (ArgumentException e)
    {
        return e.Message.Contains("parentId");
    }
});
Check("edit_list requires exactly one of ops/values", () =>
{
    try
    {
        ToolRegistry.Call("edit_list", new JsonObject { ["id"] = "ID1" }); // neither
        return false;
    }
    catch (ArgumentException e)
    {
        if (!e.Message.Contains("'ops' or 'values'"))
            return false;
    }
    try
    {
        ToolRegistry.Call("edit_list", new JsonObject // both
        {
            ["id"] = "ID1",
            ["ops"] = new JsonArray(),
            ["values"] = new JsonArray(),
        });
        return false;
    }
    catch (ArgumentException e)
    {
        return e.Message.Contains("'ops' or 'values'");
    }
});

Console.WriteLine("== impulse streams (real Harmony, engine-free) ==");
Check("all patch targets resolve (API-drift guard)", () =>
{
    var targets = ImpulseHooks.ResolvePatchTargets();
    foreach (var target in targets)
        Console.WriteLine($"        {target.Name} -> {(target.IsPostfix ? "postfix" : "prefix")} {target.HookMethod}");
    return targets.Count >= 4
           && targets.Any(t => t.Name.Contains("TriggerDynamicImpulse"))
           && targets.Any(t => t.Name.Contains("ExecuteImmediatelly"))
           && targets.Any(t => t.Name.Contains("RunNodeEvents"));
});
Check("SAFETY INVARIANT: no patch target is generic (the 2026-07-07 crash lesson)", () =>
{
    // detouring constructed generics is inert for organic calls AND executing the detoured
    // stub crashes the process — every target must be non-generic on a non-generic type
    foreach (var target in ImpulseHooks.ResolvePatchTargets())
    {
        var method = target.Method;
        if (method.IsGenericMethod || method.ContainsGenericParameters
            || (method.DeclaringType?.IsGenericType ?? false))
        {
            Console.WriteLine($"        GENERIC TARGET: {target.Name}");
            return false;
        }
    }
    return true;
});
Check("REAL Harmony patch + unpatch cycle succeeds", () =>
{
    ImpulseHooks.EnsurePatched();
    bool patched = ImpulseHooks.IsPatched;
    ImpulseHooks.Unpatch();
    return patched && !ImpulseHooks.IsPatched;
});
Check("patched method EXECUTES through the detour (null hierarchy, no crash)", () =>
{
    ImpulseHooks.EnsurePatched();
    try
    {
        // this is now the PATCHED non-generic instance method — invoking it runs the
        // detoured body itself, proving the detour executes (unlike a generic stub)
        var (method, target) = ToolsInteract.HelperMethod("TriggerDynamicImpulse", 4, generic: false);
        int count = (int)method.Invoke(target, [null, "smoke-tag", true, null])!;
        return count == 0 && ImpulseHooks.HookErrors == 0;
    }
    finally
    {
        ImpulseHooks.Unpatch();
    }
});
Check("impulse_events unknown watch → helpful error", () =>
{
    try
    {
        ToolRegistry.Call("impulse_events", new JsonObject { ["watchId"] = "nope" });
        return false;
    }
    catch (ArgumentException e)
    {
        return e.Message.Contains("No impulse watch");
    }
});
Check("impulse_unwatch all with none active reports unpatched", () =>
{
    string json = ToolRegistry.Call("impulse_unwatch", new JsonObject { ["watchId"] = "all" });
    var result = JsonNode.Parse(json)!;
    return result["active"]!.GetValue<int>() == 0 && !result["patched"]!.GetValue<bool>();
});

Console.WriteLine("== eval (real Roslyn, engine-free) ==");
// point the lazy loader at the local eval build output
Environment.SetEnvironmentVariable("MCPLINK_EVAL_PATH",
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "eval", "bin", "Release")));
Check("eval evaluates an expression off the world thread", () =>
{
    string json = ToolRegistry.Call("eval", new JsonObject
    {
        ["code"] = "1 + 41",
        ["onWorldThread"] = false,
    });
    return JsonNode.Parse(json)!["result"]!.GetValue<int>() == 42;
});
Check("eval uses engine types + LINQ imports", () =>
{
    string json = ToolRegistry.Call("eval", new JsonObject
    {
        ["code"] = "new float3(1,2,3).Magnitude > 3.7f && Enumerable.Range(1,3).Sum() == 6",
        ["onWorldThread"] = false,
    });
    return JsonNode.Parse(json)!["result"]!.GetValue<bool>();
});
Check("eval log() output is captured", () =>
{
    string json = ToolRegistry.Call("eval", new JsonObject
    {
        ["code"] = "log(\"first\"); log(new float3(1,0,0)); \"done\"",
        ["onWorldThread"] = false,
    });
    var result = JsonNode.Parse(json)!;
    var output = (JsonArray)result["output"]!;
    return result["result"]!.GetValue<string>() == "done"
           && output.Count == 2
           && output[0]!.GetValue<string>() == "first"
           && output[1]!["x"]!.GetValue<float>() == 1f;
});
Check("eval vars persist across calls", () =>
{
    ToolRegistry.Call("eval", new JsonObject
    {
        ["code"] = "vars[\"counter\"] = 7; null",
        ["onWorldThread"] = false,
    });
    string json = ToolRegistry.Call("eval", new JsonObject
    {
        ["code"] = "(int)vars[\"counter\"]! + 1",
        ["onWorldThread"] = false,
    });
    return JsonNode.Parse(json)!["result"]!.GetValue<int>() == 8;
});
Check("eval compile error reports diagnostics", () =>
{
    try
    {
        ToolRegistry.Call("eval", new JsonObject
        {
            ["code"] = "int x = \"not an int\";",
            ["onWorldThread"] = false,
        });
        return false;
    }
    catch (Exception e)
    {
        var inner = e;
        while (inner.InnerException != null)
            inner = inner.InnerException;
        return inner.Message.Contains("compilation failed") && inner.Message.Contains("CS0029");
    }
});
Check("eval runtime exception surfaces the real error", () =>
{
    try
    {
        ToolRegistry.Call("eval", new JsonObject
        {
            ["code"] = "throw new InvalidOperationException(\"boom from script\");",
            ["onWorldThread"] = false,
        });
        return false;
    }
    catch (Exception e)
    {
        var inner = e;
        while (inner.InnerException != null)
            inner = inner.InnerException;
        return inner.Message.Contains("boom from script");
    }
});
Check("eval await is supported", () =>
{
    string json = ToolRegistry.Call("eval", new JsonObject
    {
        ["code"] = "await Task.Delay(10); \"awaited\"",
        ["onWorldThread"] = false,
    });
    return JsonNode.Parse(json)!["result"]!.GetValue<string>() == "awaited";
});

// ---------- v1.4 wave ----------

Check("v1.4 new tools registered (flux_trace, wait_for)", () =>
{
    var (json, _) = dispatcher.HandlePost("""{"jsonrpc":"2.0","id":140,"method":"tools/list"}""");
    var names = JsonNode.Parse(json)!["result"]!["tools"]!.AsArray()
        .Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
    return names.Contains("flux_trace") && names.Contains("wait_for");
});
Check("v1.4 new args present in schemas", () =>
{
    var (json, _) = dispatcher.HandlePost("""{"jsonrpc":"2.0","id":141,"method":"tools/list"}""");
    var toolsArr = JsonNode.Parse(json)!["result"]!["tools"]!.AsArray();
    var schemas = toolsArr.ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!["inputSchema"]!.ToJsonString());
    var descs = toolsArr.ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!["description"]!.GetValue<string>());
    return schemas["run_batch"].Contains("transactional")
        && schemas["destroy"].Contains("\"ids\"")
        && schemas["get_component"].Contains("includeMemberIds")
        && schemas["get_slot_transform"].Contains("\"space\"")
        && schemas["find_slots"].Contains("pathPattern") && schemas["find_slots"].Contains("nameExact")
        && schemas["find_components"].Contains("pathPattern")
        && schemas["grep"].Contains("pathPattern")
        && schemas["flux_ports"].Contains("resolveRelays")
        && descs["flux_build"].Contains("inputs:{"); // per-node spec key, documented in the description
});
// world-touching tools can't run offline — "Engine is not ready" (or any non-arg error) proves
// the alias passed validation; an "Unknown argument" rejection is the failure being tested for.
static bool AliasValidates(string tool, JsonObject args)
{
    try { ToolRegistry.Call(tool, args); return true; }
    catch (ArgumentException e) when (e.Message.Contains("Unknown argument")) { return false; }
    catch (Exception) { return true; }
}
Check("v1.4 alias sweep: slotId passes validation on ls_components", () =>
    AliasValidates("ls_components", new JsonObject { ["slotId"] = "ID100" }));
Check("v1.4 alias sweep: rootSlotId on find_slots, namePattern on find_components", () =>
    AliasValidates("find_slots", new JsonObject { ["rootSlotId"] = "Root", ["namePattern"] = "x" })
    && AliasValidates("find_components", new JsonObject { ["rootSlotId"] = "Root", ["namePattern"] = "x" }));
Check("v1.4 alias + canonical together is an error (get_component)", () =>
{
    try
    {
        ToolRegistry.Call("get_component", new JsonObject { ["componentId"] = "ID1", ["id"] = "ID1" });
        return false;
    }
    catch (ArgumentException e)
    {
        return e.Message.Contains("alias", StringComparison.OrdinalIgnoreCase);
    }
});
Check("v1.4 transactional requires stopOnError", () =>
{
    try
    {
        ToolRegistry.Call("run_batch", new JsonObject
        {
            ["ops"] = new JsonArray(),
            ["transactional"] = true,
            ["stopOnError"] = false,
        });
        return false;
    }
    catch (Exception e)
    {
        return e.Message.Contains("stopOnError");
    }
});

// expression renderer — pure JSON in, string out (the session's audit formula shape)
static JsonObject Producer(string type, JsonObject? inputs = null, string? member = null)
{
    var p = new JsonObject { ["$ref"] = "IDX", ["type"] = type, ["nodeType"] = type };
    if (member != null) p["member"] = member;
    var entry = new JsonObject { ["producer"] = p, ["viaRelays"] = 0 };
    if (inputs != null) entry["inputs"] = inputs;
    return entry;
}
Check("v1.4 renderer: dynvar + literal infix", () =>
{
    var trace = new JsonObject
    {
        ["node"] = new JsonObject { ["type"] = "ValueAdd<float>" },
        ["inputs"] = new JsonObject
        {
            ["A"] = new JsonObject { ["variableName"] = "user/q" },
            ["B"] = new JsonObject { ["literal"] = 0.5 },
        },
    };
    return ToolsFlux.RenderExpression(trace) == "⟦user/q⟧ + 0.5";
});
Check("v1.4 renderer: audit-formula composition (floor/div/sub/add/inc)", () =>
{
    var divA = Producer("Div_Double3_Double");
    var divB = Producer("Div_Double3_Double");
    var add = Producer("ValueAdd<double3>", new JsonObject { ["A"] = divA, ["B"] = divB });
    var sub = Producer("Sub_Double3_Double", new JsonObject
    {
        ["A"] = add,
        ["B"] = new JsonObject { ["literal"] = 0.5 },
    });
    var floor = Producer("Floor_Double3", new JsonObject { ["N"] = sub });
    var cast = Producer("Cast_double3_To_long3", new JsonObject { ["Input"] = floor });
    var q = Producer("Pack_Long3");
    var topAdd = Producer("ValueAdd<long3>", new JsonObject { ["A"] = q, ["B"] = cast });
    var trace = new JsonObject
    {
        ["node"] = new JsonObject { ["type"] = "ValueInc<long3>" },
        ["inputs"] = new JsonObject { ["N"] = topAdd },
    };
    string expr = ToolsFlux.RenderExpression(trace);
    return expr.Contains("floor(") && expr.Contains("- 0.5") && expr.Contains("+ 1")
        && expr.Contains("Pack_Long3 +") && !expr.Contains("Cast_double3_To_long3");
});
Check("v1.4 renderer: min/unwired/cycle forms", () =>
{
    var trace = new JsonObject
    {
        ["node"] = new JsonObject { ["type"] = "ValueMin<float>" },
        ["inputs"] = new JsonObject
        {
            ["A"] = new JsonObject { ["producer"] = null },
            ["B"] = new JsonObject
            {
                ["producer"] = new JsonObject { ["$ref"] = "ID42" },
                ["cycle"] = true,
            },
        },
    };
    return ToolsFlux.RenderExpression(trace) == "min(∅, @ID42)";
});

Console.WriteLine();
Console.WriteLine("== prompt wizard presence + status lines (2.3.0) ==");
OrgtreeClient.NodeStatus NS(bool busy = false, string? actPhase = null, string? actTool = null,
    string? phase = null, int queued = 0, int tasks = 0) =>
    new("live", busy, "haiku", null, actPhase, actTool, phase, queued, tasks, null, null, null, null);
Check("presence: idle is a quiet grey chip", () =>
    PromptWizard.ComposePresence(NS()) == "<color=#777>○ idle</color>");
Check("presence: idle with queued mail says so", () =>
    PromptWizard.ComposePresence(NS(queued: 2)) == "<color=#777>○ idle · 2 queued</color>");
Check("presence: busy with no activity detail = thinking", () =>
    PromptWizard.ComposePresence(NS(busy: true)).Contains(">thinking</color>"));
Check("presence: tool phase carries the tool name", () =>
    PromptWizard.ComposePresence(NS(busy: true, actPhase: "tool", actTool: "Bash")).Contains("tool: Bash"));
Check("presence: tool names are angle-escaped, never raw tags", () =>
{
    string line = PromptWizard.ComposePresence(NS(busy: true, actPhase: "tool", actTool: "Sync<bool> probe"));
    return line.Contains("Sync‹bool› probe") && !line.Contains("Sync<bool>");
});
Check("presence: long tool names truncate with an ellipsis", () =>
{
    string line = PromptWizard.ComposePresence(NS(busy: true, actPhase: "tool", actTool: new string('x', 80)));
    return line.Contains("…") && !line.Contains(new string('x', 60));
});
Check("presence: writing phase renders as writing", () =>
    PromptWizard.ComposePresence(NS(busy: true, actPhase: "writing")).Contains(">writing<"));
Check("presence: compacting overrides the activity phase", () =>
    PromptWizard.ComposePresence(NS(busy: true, actPhase: "tool", actTool: "Bash", phase: "compacting"))
        .Contains("compacting"));
Check("presence: subagent + queue counts ride as extras", () =>
{
    string line = PromptWizard.ComposePresence(NS(busy: true, tasks: 2, queued: 1));
    return line.Contains("2 subagents") && line.Contains("1 queued");
});
Check("presence: a single subagent is singular", () =>
    PromptWizard.ComposePresence(NS(busy: true, tasks: 1)).Contains("1 subagent</color>"));
Check("status line: blocked is loud even without a summary", () =>
    PromptWizard.FormatStatusLine("blocked", "")!.Contains("⚠ blocked"));
Check("status line: blocked carries its summary", () =>
    PromptWizard.FormatStatusLine("blocked", "need a decision")!.Contains("⚠ blocked</b> — need a decision"));
Check("status line: working renders as the gear", () =>
    PromptWizard.FormatStatusLine("working", "tracing the bug")!.Contains("⚙ tracing the bug"));
Check("status line: a done report (stored idle + summary) is the checkmark", () =>
    PromptWizard.FormatStatusLine("idle", "fixed and verified")!.Contains("✓ fixed and verified"));
Check("status line: idle with nothing to say is no line at all", () =>
    PromptWizard.FormatStatusLine("idle", "  ") == null && PromptWizard.FormatStatusLine(null, "x") == null);
Check("status line: HTML entities decode, then angle-escape for UIX", () =>
{
    string line = PromptWizard.FormatStatusLine("idle", "wired the DynamicValueVariable&lt;colorX&gt;")!;
    return line.Contains("DynamicValueVariable‹colorX›") && !line.Contains("&lt;");
});

Console.WriteLine();
Console.WriteLine("== prompt wizard question cards (2.4.0) ==");
OrgtreeClient.AskCard? ParseAskJson(string json) => OrgtreeClient.ParseAsk(JsonNode.Parse(json));
Check("ask parse: open batch — tabs, options, multi, rev from revs.ask", () =>
{
    var card = ParseAskJson("""
        {"id":"q1","node":"a","kind":"batch","status":"open","at":"t",
         "tabs":[{"kind":"question","question":"Which?","header":"Approach",
                  "options":[{"label":"A","description":"go left"},{"label":"B"}]},
                 {"kind":"question","question":"Keep?","multi":true,
                  "options":[{"label":"x"},{"label":"y"}]}],
         "revs":{"ask":3},"question":"Which?","rev":3}
        """)!;
    return card.Status == "open" && card.Rev == 3 && card.Tabs.Count == 2 && card.OtherTabs == 0
        && card.QuestionsOnly && card.Key == "q1:3:2:0"
        && card.Tabs[0].Header == "Approach" && card.Tabs[0].Options.Count == 2
        && card.Tabs[0].Options[0].Description == "go left" && card.Tabs[0].Options[1].Description == null
        && card.Tabs[1].Multi && !card.Tabs[0].Multi;
});
Check("ask parse: a credits tab makes it a full request (not questions-only)", () =>
{
    var card = ParseAskJson("""
        {"id":"q1","status":"open","tabs":[
            {"kind":"question","question":"Which?"},
            {"kind":"credits","id":"c1","old":10,"new":20,"reason":"r"}],
         "revs":{"ask":1,"credits":1}}
        """)!;
    return card.Tabs.Count == 1 && card.OtherTabs == 1 && !card.QuestionsOnly && card.Key == "q1:1:1:1";
});
Check("ask parse: pre-batch single entry (no tabs) synthesizes one tab from the mirror", () =>
{
    var card = ParseAskJson("""
        {"id":"q2","status":"open","question":"Solo?","multi":false,
         "options":[{"label":"yes","description":"do it"}],"at":"t"}
        """)!;
    return card.Tabs.Count == 1 && card.QuestionsOnly && card.Rev == 1
        && card.Tabs[0].Question == "Solo?" && card.Tabs[0].Options.Count == 1;
});
Check("ask parse: resolved linger carries reason + the desk-given answer", () =>
{
    var card = ParseAskJson("""
        {"id":"q3","status":"answered","reason":"answered","rev":2,
         "answer":{"selected":["a","b"],"text":"extra"}}
        """)!;
    return card.Status == "answered" && card.Reason == "answered"
        && card.AnswerSummary == "a · b — extra" && card.Tabs.Count == 0;
});
Check("ask parse: null and non-object payloads degrade to no card", () =>
    OrgtreeClient.ParseAsk(null) == null && ParseAskJson("\"nope\"") == null
    && ParseAskJson("{\"status\":\"open\"}") == null);
Check("ask parse: rev falls back to the top-level stamp when revs is absent", () =>
    ParseAskJson("""{"id":"q9","status":"open","rev":4,"tabs":[{"kind":"question","question":"?"}]}""")!
        .Rev == 4);
Check("ask answer: single tab, picks only → selected + rev, no text", () =>
{
    var (body, err) = PromptWizard.ComposeAskAnswer(
        new() { ("Q1", false, new List<string> { "A" }, "") }, 2);
    return err == null && body!["rev"]!.GetValue<int>() == 2 && body["text"] == null
        && body["selected"]!.AsArray().Count == 1 && body["selected"]![0]!.GetValue<string>() == "A";
});
Check("ask answer: single tab, free text only → text, no selected", () =>
{
    var (body, err) = PromptWizard.ComposeAskAnswer(
        new() { ("Q1", false, new List<string>(), "my own words") }, 1);
    return err == null && body!["selected"] == null && body["text"]!.GetValue<string>() == "my own words";
});
Check("ask answer: single tab sends pick AND text together (the backend composes Also:)", () =>
{
    var (body, err) = PromptWizard.ComposeAskAnswer(
        new() { ("Q1", true, new List<string> { "A", "B" }, "and note this") }, 1);
    return err == null && body!["selected"]!.AsArray().Count == 2
        && body["text"]!.GetValue<string>() == "and note this";
});
Check("ask answer: single tab with nothing → a naming error, no body", () =>
{
    var (body, err) = PromptWizard.ComposeAskAnswer(
        new() { ("Deploy", false, new List<string>(), "") }, 1);
    return body == null && err != null && err.Contains("Deploy");
});
Check("ask answer: batch is positional — string per single tab, list per multi tab, rev rides", () =>
{
    var (body, err) = PromptWizard.ComposeAskAnswer(new()
    {
        ("Q1", false, new List<string> { "A" }, ""),
        ("Q2", true, new List<string> { "x", "y" }, ""),
    }, 5);
    var sel = body!["selected"]!.AsArray();
    return err == null && body["rev"]!.GetValue<int>() == 5 && sel.Count == 2
        && sel[0]!.GetValue<string>() == "A"
        && sel[1]!.AsArray().Count == 2 && sel[1]![1]!.GetValue<string>() == "y";
});
Check("ask answer: batch free text replaces a single-select pick, joins a multi tab's picks", () =>
{
    var (body, err) = PromptWizard.ComposeAskAnswer(new()
    {
        ("Q1", false, new List<string> { "A" }, "actually C"),
        ("Q2", true, new List<string> { "x" }, "plus z"),
    }, 1);
    var sel = body!["selected"]!.AsArray();
    return err == null && sel[0]!.GetValue<string>() == "actually C"
        && sel[1]!.AsArray().Count == 2 && sel[1]![1]!.GetValue<string>() == "plus z";
});
Check("ask answer: a hole in the batch errors, naming the empty tab", () =>
{
    var (body, err) = PromptWizard.ComposeAskAnswer(new()
    {
        ("Q1", false, new List<string> { "A" }, ""),
        ("Deploy", true, new List<string>(), ""),
    }, 1);
    return body == null && err != null && err.Contains("Deploy");
});
Check("ask echo: single question is question + bold answer", () =>
    PromptWizard.ComposeAskEcho(new() { ("Q1", "Which way?", "left") })
        == "Which way?\n→ **left**");
Check("ask echo: batch carries each tab's label", () =>
{
    string echo = PromptWizard.ComposeAskEcho(new()
        { ("Approach", "Which?", "A"), ("Q2", "Keep?", "x · y") });
    return echo.Contains("Approach — Which?") && echo.Contains("→ **x · y**");
});
Check("ask resolution lines: answered/dismissed/withdrawn/moot each say why", () =>
    PromptWizard.FormatAskResolution("answered", null, "a · b").Contains("answered from the desk — a · b")
    && PromptWizard.FormatAskResolution("answered", null, null).Contains("answered from the desk")
    && PromptWizard.FormatAskResolution("dismissed", null, null).Contains("dismissed")
    && PromptWizard.FormatAskResolution("withdrawn", null, null).Contains("withdrew")
    && PromptWizard.FormatAskResolution("moot", "the asking agent was retired", null).Contains("retired"));
OrgtreeClient.AskCard OpenAsk(int others = 0) => new("q1", "open", 1,
    new List<OrgtreeClient.AskTab> { new("Which?", null, false, new List<OrgtreeClient.AskOption>()) },
    others, null, null);
OrgtreeClient.NodeStatus NSAsk(bool busy, int others = 0) =>
    new("live", busy, "haiku", null, null, null, null, 0, 0, null, null, null, null, OpenAsk(others));
Check("presence: an open question takes over the idle line", () =>
    PromptWizard.ComposePresence(NSAsk(busy: false)).Contains("❓ waiting on your answer"));
Check("presence: a full request points at the desk instead", () =>
    PromptWizard.ComposePresence(NSAsk(busy: false, others: 1)).Contains("request waiting at the desk"));
Check("presence: busy with an open question keeps the activity and appends the pending flag", () =>
{
    string line = PromptWizard.ComposePresence(NSAsk(busy: true));
    return line.Contains(">thinking</color>") && line.Contains("question pending");
});

Console.WriteLine("== export_skinned_gltf writer ==");
// Synthetic rigged mesh: 8-vert strip, 2 submeshes, 2 UV channels, 2 bones
// (identity + T(0,-2,0.5) bind), split weights, 2 blendshapes (ShapeA with
// normal deltas, ShapeB without). Every value below is asserted against the
// written .gltf/.bin pair, including z-flip handedness and winding reversal.
// (Lives in a local function so Elements.Assets JITs after the resolver is up.)
RunGltfExporterChecks();
void RunGltfExporterChecks()
{
    var mesh = new Elements.Assets.MeshX();
    mesh.SetVertexCount(8);
    Elements.Core.float3[] positions =
    [
        new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0),
        new(0, 2, 0), new(1, 2, 0), new(0, 3, 1), new(1, 3, 0),
    ];
    mesh.HasNormals = true;
    mesh.HasTangents = true;
    mesh.HasUV0s = true;
    mesh.HasUV1s = true;
    mesh.HasBoneBindings = true;
    for (int i = 0; i < 8; i++)
    {
        mesh.SetVertex(i, in positions[i]);
        var normal = new Elements.Core.float3(0, 0, 1);
        mesh.SetNormal(i, in normal);
        var tangent = new Elements.Core.float4(1, 0, 0, 1);
        mesh.SetTangent(i, in tangent);
        var uv0 = new Elements.Core.float2(positions[i].x, positions[i].y / 3f);
        mesh.SetUV(i, 0, in uv0);
        var uv1 = new Elements.Core.float2(0.5f, 0.25f);
        mesh.SetUV(i, 1, in uv1);
    }
    var boneA = mesh.AddBone("Bone_A");
    boneA.BindPose = Elements.Core.float4x4.Identity;
    var boneB = mesh.AddBone("Bone_B");
    var bindTranslation = new Elements.Core.float3(0, -2, 0.5f);
    boneB.BindPose = Elements.Core.float4x4.Translation(in bindTranslation);
    var bindings = mesh.RawBoneBindings;
    for (int i = 0; i < 8; i++)
    {
        bindings[i].ClearBones();
        if (i <= 3) bindings[i].AddBone(0, 1f);
        else if (i <= 5) { bindings[i].AddBone(0, 0.25f); bindings[i].AddBone(1, 0.75f); }
        else bindings[i].AddBone(1, 1f);
    }
    var sub0 = mesh.AddSubmesh<Elements.Assets.TriangleSubmesh>();
    sub0.AddTriangle(0, 1, 3); sub0.AddTriangle(0, 3, 2); sub0.AddTriangle(2, 3, 5);
    var sub1 = mesh.AddSubmesh<Elements.Assets.TriangleSubmesh>();
    sub1.AddTriangle(2, 5, 4); sub1.AddTriangle(4, 5, 7); sub1.AddTriangle(4, 7, 6);
    var shapeA = mesh.AddBlendShape("ShapeA");
    shapeA.HasNormals = true;
    var frameA = shapeA.AddFrame(1f);
    var deltasA = new Elements.Core.float3[8];
    var normalDeltasA = new Elements.Core.float3[8];
    for (int i = 0; i <= 3; i++) { deltasA[i] = new(0, 0, 0.5f); normalDeltasA[i] = new(0, 0.1f, 0); }
    frameA.SetPositionDeltas(deltasA, null, 0, 0);
    frameA.SetNormalDeltas(normalDeltasA, null, 0, 0);
    var shapeB = mesh.AddBlendShape("ShapeB");
    var frameB = shapeB.AddFrame(1f);
    var deltasB = new Elements.Core.float3[8];
    for (int i = 6; i <= 7; i++) deltasB[i] = new(0.3f, 0, 0);
    frameB.SetPositionDeltas(deltasB, null, 0, 0);

    string dir = Path.Combine(Path.GetTempPath(), "mcplink-gltf-test");
    string gltfPath = Path.Combine(dir, "synthetic.gltf");
    JsonObject report = GltfSkinnedExport.Write(mesh,
        [new GltfSkinnedExport.BoneInfo("Bone_A", -1), new GltfSkinnedExport.BoneInfo("Bone_B", 0)],
        ["MatX"], "TestMesh", gltfPath);
    var doc = (JsonObject)JsonNode.Parse(File.ReadAllText(gltfPath))!;
    byte[] bin = File.ReadAllBytes(Path.Combine(dir, "synthetic.bin"));

    int AccessorOffset(int index)
    {
        var accessor = (JsonObject)doc["accessors"]![index]!;
        var view = (JsonObject)doc["bufferViews"]![accessor["bufferView"]!.GetValue<int>()]!;
        return view["byteOffset"]!.GetValue<int>();
    }
    float F(int byteOffset) => BitConverter.ToSingle(bin, byteOffset);

    Check("gltf: report counts + buffer byteLength matches .bin", () =>
        report["vertices"]!.GetValue<int>() == 8
        && report["triangles"]!.GetValue<int>() == 6
        && report["primitives"]!.GetValue<int>() == 2
        && report["bones"]!.GetValue<int>() == 2
        && report["rootBones"]!.GetValue<int>() == 1
        && doc["buffers"]![0]!["byteLength"]!.GetValue<int>() == bin.Length);

    var prim0 = (JsonObject)doc["meshes"]![0]!["primitives"]![0]!;
    var prim1 = (JsonObject)doc["meshes"]![0]!["primitives"]![1]!;
    Check("gltf: both primitives carry TEXCOORD_0+1, JOINTS_0, WEIGHTS_0, TANGENT", () =>
        new[] { prim0, prim1 }.All(p =>
        {
            var a = (JsonObject)p["attributes"]!;
            return a["TEXCOORD_0"] != null && a["TEXCOORD_1"] != null
                && a["JOINTS_0"] != null && a["WEIGHTS_0"] != null
                && a["NORMAL"] != null && a["TANGENT"] != null;
        }));

    Check("gltf: materials — given name then fallback", () =>
        doc["materials"]![0]!["name"]!.GetValue<string>() == "MatX"
        && doc["materials"]![1]!["name"]!.GetValue<string>() == "Material_1");

    Check("gltf: POSITION x negated (v1 x=1 → -1), z untouched (v6 z stays 1)", () =>
    {
        int posAcc = prim0["attributes"]!["POSITION"]!.GetValue<int>();
        int off1 = AccessorOffset(posAcc) + 1 * 12;
        int off6 = AccessorOffset(posAcc) + 6 * 12;
        var min = ((JsonObject)doc["accessors"]![posAcc]!)["min"]!.AsArray();
        return Math.Abs(F(off1) - -1f) < 1e-6
            && Math.Abs(F(off6 + 8) - 1f) < 1e-6
            && Math.Abs(min[0]!.GetValue<float>() - -1f) < 1e-6;
    });

    Check("gltf: winding reversed — first tri (0,1,3) emits 0,3,1", () =>
    {
        int offset = AccessorOffset(prim0["indices"]!.GetValue<int>());
        return BitConverter.ToUInt32(bin, offset) == 0
            && BitConverter.ToUInt32(bin, offset + 4) == 3
            && BitConverter.ToUInt32(bin, offset + 8) == 1;
    });

    Check("gltf: UV v flipped (uv1 0.25 → 0.75)", () =>
        Math.Abs(F(AccessorOffset(prim0["attributes"]!["TEXCOORD_1"]!.GetValue<int>()) + 4) - 0.75f) < 1e-6);

    Check("gltf: tangent x and w flip with handedness", () =>
        Math.Abs(F(AccessorOffset(prim0["attributes"]!["TANGENT"]!.GetValue<int>())) - -1f) < 1e-6
        && Math.Abs(F(AccessorOffset(prim0["attributes"]!["TANGENT"]!.GetValue<int>()) + 12) - -1f) < 1e-6);

    Check("gltf: per-bone weight sums A=4.5 B=3.5 read back from bin", () =>
    {
        int jointsOffset = AccessorOffset(prim0["attributes"]!["JOINTS_0"]!.GetValue<int>());
        int weightsOffset = AccessorOffset(prim0["attributes"]!["WEIGHTS_0"]!.GetValue<int>());
        float sumA = 0, sumB = 0;
        for (int v = 0; v < 8; v++)
            for (int k = 0; k < 4; k++)
            {
                int joint = BitConverter.ToUInt16(bin, jointsOffset + v * 8 + k * 2);
                float weight = F(weightsOffset + v * 16 + k * 4);
                if (joint == 0) sumA += weight; else if (joint == 1) sumB += weight;
            }
        return Math.Abs(sumA - 4.5f) < 1e-5 && Math.Abs(sumB - 3.5f) < 1e-5;
    });

    Check("gltf: skin joints=2, IBM MAT4 count 2, B translation x-mirrored to (0,-2,0.5)", () =>
    {
        var skin = (JsonObject)doc["skins"]![0]!;
        int ibmAcc = skin["inverseBindMatrices"]!.GetValue<int>();
        var accessor = (JsonObject)doc["accessors"]![ibmAcc]!;
        int offset = AccessorOffset(ibmAcc) + 64; // bone B, column-major, translation = elements 12..14
        return skin["joints"]!.AsArray().Count == 2
            && accessor["type"]!.GetValue<string>() == "MAT4"
            && accessor["count"]!.GetValue<int>() == 2
            && Math.Abs(F(offset + 12 * 4) - 0f) < 1e-6
            && Math.Abs(F(offset + 13 * 4) - -2f) < 1e-6
            && Math.Abs(F(offset + 14 * 4) - 0.5f) < 1e-6;
    });

    Check("gltf: joint node B is a child of A, local matrix = inv(bindA)·bindB z-flipped", () =>
    {
        var nodes = doc["nodes"]!.AsArray();
        int nodeA = -1, nodeB = -1;
        for (int i = 0; i < nodes.Count; i++)
        {
            string name = nodes[i]!["name"]!.GetValue<string>();
            if (name == "Bone_A") nodeA = i;
            if (name == "Bone_B") nodeB = i;
        }
        var childrenOfA = nodes[nodeA]!["children"]!.AsArray().Select(n => n!.GetValue<int>());
        var matrix = nodes[nodeB]!["matrix"]!.AsArray();
        // global bind of B = inverse(T(0,-2,0.5)) = T(0,2,-0.5); x-mirror keeps it
        return childrenOfA.Contains(nodeB)
            && Math.Abs(matrix[12]!.GetValue<float>() - 0f) < 1e-6
            && Math.Abs(matrix[13]!.GetValue<float>() - 2f) < 1e-6
            && Math.Abs(matrix[14]!.GetValue<float>() - -0.5f) < 1e-6;
    });

    Check("gltf: targetNames [ShapeA, ShapeB]; NORMAL deltas only on ShapeA", () =>
    {
        var names = doc["meshes"]![0]!["extras"]!["targetNames"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToArray();
        var targets = prim0["targets"]!.AsArray();
        return names.SequenceEqual(["ShapeA", "ShapeB"])
            && targets.Count == 2
            && targets[0]!["NORMAL"] != null
            && targets[1]!["NORMAL"] == null
            && report["blendshapes"]!.AsArray().Count == 2;
    });

    Check("gltf: ShapeA POSITION delta 0.5z stays +0.5 in the z lane (x-mirror untouched)", () =>
    {
        int acc = prim0["targets"]![0]!["POSITION"]!.GetValue<int>();
        return Math.Abs(F(AccessorOffset(acc) + 8) - 0.5f) < 1e-6;
    });

    Check("gltf: weight totals reported as 1.0 (faithful, no silent normalization)", () =>
        Math.Abs(report["weightTotalMin"]!.GetValue<float>() - 1f) < 1e-5
        && Math.Abs(report["weightTotalMax"]!.GetValue<float>() - 1f) < 1e-5
        && report["zeroWeightVertices"]!.GetValue<int>() == 0);
}

// Inch-scale twin: same rig but every bind global carries a uniform 0.0254 scale
// (as one live garment does). Normalization must cancel it exactly — IBM and node
// values byte-match the unscaled rig above, and the report carries the marker.
RunGltfScaleNormalizationCheck();
void RunGltfScaleNormalizationCheck()
{
    const float sigma = 0.0254f;
    var mesh = new Elements.Assets.MeshX();
    mesh.SetVertexCount(3);
    mesh.HasBoneBindings = true;
    for (int i = 0; i < 3; i++)
    {
        var p = new Elements.Core.float3(i, 0, 0);
        mesh.SetVertex(i, in p);
        mesh.RawBoneBindings[i].ClearBones();
        mesh.RawBoneBindings[i].AddBone(i < 2 ? 0 : 1, 1f);
    }
    var sub = mesh.AddSubmesh<Elements.Assets.TriangleSubmesh>();
    sub.AddTriangle(0, 1, 2);
    // S01-like structure: bind globals = T(meters) · S(sigma) — basis carries the
    // inch scale, bone rest positions stay in meters
    var sigmaScale = new Elements.Core.float3(sigma, sigma, sigma);
    var scaledA = mesh.AddBone("Bone_A");
    scaledA.BindPose = Elements.Core.float4x4.Scale(in sigmaScale).Inverse;
    var scaledB = mesh.AddBone("Bone_B");
    var childLocal = new Elements.Core.float3(0, 2, -0.5f);
    scaledB.BindPose = (Elements.Core.float4x4.Translation(in childLocal)
        * Elements.Core.float4x4.Scale(in sigmaScale)).Inverse;

    string dir = Path.Combine(Path.GetTempPath(), "mcplink-gltf-test");
    string gltfPath = Path.Combine(dir, "scaled.gltf");
    JsonObject report = GltfSkinnedExport.Write(mesh,
        [new GltfSkinnedExport.BoneInfo("Bone_A", -1), new GltfSkinnedExport.BoneInfo("Bone_B", 0)],
        [], "Scaled", gltfPath);
    var doc = (JsonObject)JsonNode.Parse(File.ReadAllText(gltfPath))!;
    byte[] bin = File.ReadAllBytes(Path.Combine(dir, "scaled.bin"));

    Check("gltf-scale: uniform bind scale is reported and normalized away", () =>
    {
        var skin = (JsonObject)doc["skins"]![0]!;
        int ibmAcc = skin["inverseBindMatrices"]!.GetValue<int>();
        var accessor = (JsonObject)doc["accessors"]![ibmAcc]!;
        var view = (JsonObject)doc["bufferViews"]![accessor["bufferView"]!.GetValue<int>()]!;
        int offset = view["byteOffset"]!.GetValue<int>() + 64; // bone B
        float tx = BitConverter.ToSingle(bin, offset + 12 * 4);
        float ty = BitConverter.ToSingle(bin, offset + 13 * 4);
        float tz = BitConverter.ToSingle(bin, offset + 14 * 4);
        float diag = BitConverter.ToSingle(bin, offset); // element [0,0] must be ~1 after normalization
        return Math.Abs(report["bindScaleNormalized"]!.GetValue<float>() - sigma) < 1e-4
            && Math.Abs(tx - 0f) < 1e-4 && Math.Abs(ty - -2f) < 1e-4 && Math.Abs(tz - 0.5f) < 1e-4
            && Math.Abs(diag - 1f) < 1e-4;
    });

    Check("gltf-scale: node B rest translation lands in meters (0,2,-0.5)", () =>
    {
        var nodes = doc["nodes"]!.AsArray();
        var nodeB = nodes.First(n => n!["name"]!.GetValue<string>() == "Bone_B")!;
        var matrix = nodeB["matrix"]!.AsArray();
        return Math.Abs(matrix[12]!.GetValue<float>() - 0f) < 1e-4
            && Math.Abs(matrix[13]!.GetValue<float>() - 2f) < 1e-4
            && Math.Abs(matrix[14]!.GetValue<float>() - -0.5f) < 1e-4;
    });
}

// Up-correction twin: the same rig exported with the FBX stand-up rotation
// (-90° X: meshZ-up -> glTF +Y-up). Verts must land height-on-Y, the root joint
// node must carry the rotation, and rest skinning must stay exactly identity
// (node chain × IBM = I) — the frame bug this guards against passed every
// orientation-invariant check while being 90° wrong for every consumer.
RunGltfUpRotationCheck();
void RunGltfUpRotationCheck()
{
    var mesh = new Elements.Assets.MeshX();
    mesh.SetVertexCount(3);
    mesh.HasBoneBindings = true;
    // strip along mesh +Z (the "height" axis of a Z-up asset)
    Elements.Core.float3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 0.2f, 3)];
    for (int i = 0; i < 3; i++)
    {
        mesh.SetVertex(i, in positions[i]);
        mesh.RawBoneBindings[i].ClearBones();
        mesh.RawBoneBindings[i].AddBone(i < 2 ? 0 : 1, 1f);
    }
    var sub = mesh.AddSubmesh<Elements.Assets.TriangleSubmesh>();
    sub.AddTriangle(0, 1, 2);
    var rootBone = mesh.AddBone("Bone_A");
    rootBone.BindPose = Elements.Core.float4x4.Identity;
    var childBone = mesh.AddBone("Bone_B");
    var childAt = new Elements.Core.float3(0, 0, 2); // 2 up the mesh-Z height axis
    childBone.BindPose = Elements.Core.float4x4.Translation(in childAt).Inverse;

    var standUp = Elements.Core.floatQ.Euler(-90f, 0f, 0f); // meshZ -> +Y up

    string dir = Path.Combine(Path.GetTempPath(), "mcplink-gltf-test");
    string gltfPath = Path.Combine(dir, "rotated.gltf");
    JsonObject report = GltfSkinnedExport.Write(mesh,
        [new GltfSkinnedExport.BoneInfo("Bone_A", -1), new GltfSkinnedExport.BoneInfo("Bone_B", 0)],
        [], "Rotated", gltfPath, standUp);
    var doc = (JsonObject)JsonNode.Parse(File.ReadAllText(gltfPath))!;
    byte[] bin = File.ReadAllBytes(Path.Combine(dir, "rotated.bin"));

    int Offset(int accessorIndex)
    {
        var accessor = (JsonObject)doc["accessors"]![accessorIndex]!;
        return ((JsonObject)doc["bufferViews"]![accessor["bufferView"]!.GetValue<int>()]!)["byteOffset"]!.GetValue<int>();
    }
    float F(int byteOffset) => BitConverter.ToSingle(bin, byteOffset);

    var prim = (JsonObject)doc["meshes"]![0]!["primitives"]![0]!;
    Check("gltf-up: mesh-Z height lands on glTF +Y (v2 (0,0.2,3) → (0,3,-0.2))", () =>
    {
        int off = Offset(prim["attributes"]!["POSITION"]!.GetValue<int>()) + 2 * 12;
        return Math.Abs(F(off) - 0f) < 1e-5
            && Math.Abs(F(off + 4) - 3f) < 1e-5
            && Math.Abs(F(off + 8) - -0.2f) < 1e-5
            && report["meshRotationApplied"] != null;
    });

    Check("gltf-up: bone B rests 2 up the glTF Y axis; rest skin (node·IBM) is identity", () =>
    {
        var nodes = doc["nodes"]!.AsArray();
        var nodeA = nodes.First(n => n!["name"]!.GetValue<string>() == "Bone_A")!;
        var nodeB = nodes.First(n => n!["name"]!.GetValue<string>() == "Bone_B")!;
        float[] ma = nodeA["matrix"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
        float[] mb = nodeB["matrix"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
        var skin = (JsonObject)doc["skins"]![0]!;
        int ibmOff = Offset(skin["inverseBindMatrices"]!.GetValue<int>()) + 64; // bone B
        float[] ibm = Enumerable.Range(0, 16).Select(i => F(ibmOff + i * 4)).ToArray();

        // column-major multiply: global(B) = A · B(local), then rest = global · IBM
        float[] Mul(float[] x, float[] y)
        {
            var r = new float[16];
            for (int c = 0; c < 4; c++)
                for (int rw = 0; rw < 4; rw++)
                    for (int k = 0; k < 4; k++)
                        r[c * 4 + rw] += x[k * 4 + rw] * y[c * 4 + k];
            return r;
        }
        float[] rest = Mul(Mul(ma, mb), ibm);
        bool identity = true;
        for (int c = 0; c < 4; c++)
            for (int rw = 0; rw < 4; rw++)
                identity &= Math.Abs(rest[c * 4 + rw] - (c == rw ? 1f : 0f)) < 1e-4;
        // bone B global rest position = A·B translation — must be (0,2,0) in glTF frame
        float[] globalB = Mul(ma, mb);
        return identity
            && Math.Abs(globalB[12] - 0f) < 1e-4
            && Math.Abs(globalB[13] - 2f) < 1e-4
            && Math.Abs(globalB[14] - 0f) < 1e-4;
    });
}

// Heading derivation: the anchor path must recover the importer-authored rotation
// EXACTLY — including any intrinsic facing yaw — while the yaw-strip fallback
// structurally cannot (it removes ALL world-Y twist, authored or user). Note: the
// live garments' authored rotation happens to carry no intrinsic yaw (their 180°
// bug was the handedness constant, negate-Z vs negate-X), but the anchor boundary
// is what keeps assets that DO have one from silently losing it.
RunUpRotationDerivationChecks();
void RunUpRotationDerivationChecks()
{
    // authored = what the FBX importer recorded below the model root (live-measured
    // slot euler on all three garments); userYaw = the scene placement above it
    var authored = Elements.Core.floatQ.Euler(-90f, 180f, 180f);
    var userYaw = Elements.Core.floatQ.Euler(0f, 18.1f, 0f);
    var rendererGlobal = userYaw * authored;

    float Dot(Elements.Core.floatQ a, Elements.Core.floatQ b) =>
        Math.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);

    Check("derive: anchor path recovers the authored rotation exactly (facing kept)", () =>
    {
        var derived = GltfSkinnedExport.DeriveUpRotation(rendererGlobal, userYaw);
        var up = derived * new Elements.Core.float3(0, 0, 1);
        return Dot(derived, authored) > 0.99999f && Math.Abs(up.y - 1f) < 1e-4;
    });

    Check("derive: an intrinsic facing yaw survives the anchor path, dies under yaw-strip", () =>
    {
        // an asset whose importer-authored rotation includes a 180° facing
        var facing = Elements.Core.floatQ.Euler(0f, 180f, 0f) * Elements.Core.floatQ.Euler(-90f, 0f, 0f);
        var global = userYaw * facing;
        var anchored = GltfSkinnedExport.DeriveUpRotation(global, userYaw);
        var stripped = GltfSkinnedExport.DeriveUpRotation(global, null);
        var strippedUp = stripped * new Elements.Core.float3(0, 0, 1);
        return Dot(anchored, facing) > 0.99999f            // anchor keeps the facing
            && Math.Abs(strippedUp.y - 1f) < 1e-3          // strip still stands it up...
            && Dot(stripped, facing) < 0.999f;             // ...but the facing is gone
    });

    Check("derive: user yaw never leaks (two placements, same derived rotation)", () =>
    {
        var otherYaw = Elements.Core.floatQ.Euler(0f, 297f, 0f);
        var a = GltfSkinnedExport.DeriveUpRotation(userYaw * authored, userYaw);
        var b = GltfSkinnedExport.DeriveUpRotation(otherYaw * authored, otherYaw);
        return Dot(a, b) > 0.99999f;
    });
}

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
