using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>On-demand examples keep the full scripting reference out of tool discovery.</summary>
internal static class McpHelp
{
    public static readonly ApiMethod Method = new("help", "Mapping help", "Get an index or one focused example.",
        """{"type":"object","properties":{"kind":{"type":"string","enum":["method","op","query"]},"name":{"type":"string"}},"dependentRequired":{"name":["kind"]}}""");

    private static readonly Dictionary<string, string> s_ops = new(StringComparer.Ordinal)
    {
        ["create"] = """{"id":"$block","brush":{"min":[0,0,0],"max":[128,128,96]},"csg":"add"}""",
        ["update"] = """{"id":"Block","note":"Vault practice"}""",
        ["delete"] = """{"id":"Block"}""",
        ["move"] = """{"id":"Block","by":[64,0,0]}""",
        ["rotate"] = """{"id":"Block","byDeg":[0,90,0]}""",
        ["scale"] = """{"id":"Block","by":[2,1,1]}""",
        ["duplicate"] = """{"id":"Block","by":[256,0,0],"count":2}""",
        ["brush"] = """{"id":"$wall","brush":{"size":[32,256,256]},"at":[192,0,128],"csg":"add"}""",
        ["stair"] = """{"id":"MainStair","at":[0,0,0],"set":{"layout":"straight","width":192,"rise":256,"run":512,"overhang":2,"edgeRadius":2}}""",
        ["surface"] = """{"id":"Block","faces":"walls","align":"wall"}""",
        ["ge"] = """{"id":"$ledge","kind":"LG","points":[[0,0,96],[0,128,96]]}""",
        ["conveyor"] = """{"points":[[0,0,16],[256,0,16]],"width":96,"speed":90}""",
        ["link"] = """{"from":"Switch","to":"Door"}""",
        ["layer"] = """{"name":"Traversal","color":"#54C58C","add":["Block"]}""",
        ["folder"] = """{"path":"Practice/Traversal","ids":["Block","Ledge"]}""",
        ["settings"] = """{"title":"Training rooftop"}""",
        ["identity"] = """{"name":"Training rooftop","description":"A traversal practice map."}""",
        ["select"] = """{"id":"Block","mode":"set"}""",
        ["workspace"] = """{"grid":16}""",
        ["storey.duplicate"] = """{"storey":"Ground","by":[0,0,512]}""",
        ["bookmark"] = """{"name":"Overview","at":[-256,0,192],"look":[0,0,64]}""",
        ["reference"] = """{"path":"C:/maps/plan.png","view":"top","unitsPerPixel":2}""",
        ["prefab.place"] = """{"name":"ladder","at":[0,0,0],"id":"$ladder"}""",
        ["prefab.save"] = """{"name":"vault-block","id":"Block","to":"project"}""",
        ["prefab.update"] = """{"name":"vault-block"}""",
        ["prefab.break"] = """{"name":"vault-block"}""",
        ["mover"] = """{"id":"Door","keys":[{"at":[0,0,0]},{"at":[0,0,192]}],"moveTime":1}""",
        ["magic"] = """{"at":[0,0,64],"tag":"Relay","groups":[{"actions":[{"event":"Door","delay":1,"type":"trigger"}]}]}""",
        ["camnet"] = """{"order":["CameraA","CameraB"],"loop":true}""",
        ["camera"] = """{"id":"CameraA","name":"Roof","yaw":[8192,8192]}""",
        ["props.paste"] = """{"from":"CameraA","id":"CameraB","keys":["bInitiallyOn"]}""",
        ["propset"] = """{"set":"SCamera.blinde","id":"CameraA"}""",
        ["tip"] = """{"at":[0,0,64],"spy":"Vault the low wall."}""",
        ["zone.action"] = """{"mission":"Objective","message":{"spy":"Done","merc":"Breach","seconds":3}}""",
        ["match"] = """{"id":"Door","players":[2,8],"outside":{"key":0}}""",
        ["outfits"] = """{"team":"spy","cosmetics":"player"}""",
        ["lights.recover"] = """{"mode":"exact"}""",
    };

    private sealed record Query(string Fields = "", string Example = "{}");

    private static readonly Dictionary<string, Query> s_queries = new(StringComparer.Ordinal)
    {
        ["find"] = new("kind ids class exact tag event near radius box layer folder text has props csg author fields rows limit offset sort",
            """{"kind":"brush","fields":["id","bounds"],"rows":true,"limit":5}"""),
        ["count"] = new("kind ids class exact tag event near radius box layer folder text has props csg author by", """{"by":"class"}"""),
        ["get"] = new("ids fields", """{"ids":["Room0"],"fields":["id","bounds"]}"""),
        ["summary"] = new("classes", """{"classes":5}"""),
        ["schema"] = new("class text all own", """{"class":"SCamera","own":true}"""),
        ["classes"] = new("under text all limit", """{"text":"camera","limit":8}"""),
        ["check"] = new("target only limit deep", """{"limit":10}"""),
        ["edges"] = new("id depth limit", """{"id":"Door","depth":1,"limit":10}"""),
        ["raycast"] = new("from to dir max", """{"from":[0,0,128],"dir":[0,0,-1],"max":256}"""),
        ["trace"] = new("from to dir max", """{"from":[0,0,128],"to":[0,0,-32]}"""),
        ["pointInfo"] = new("at radius", """{"at":[0,0,128],"radius":192}"""),
        ["history"] = new("limit", """{"limit":5}"""),
        ["identity"] = new(),
        ["importReport"] = new(),
        ["workspace"] = new(),
        ["storeys"] = new(),
        ["backups"] = new(),
        ["prefabs"] = new("name", """{"name":"ladder"}"""),
        ["assets"] = new("kind text origin limit", """{"kind":"mesh","text":"bench","limit":5}"""),
        ["asset"] = new("path kind", """{"path":"MyLevel.Meshes.Bench","kind":"mesh"}"""),
        ["mover"] = new("id", """{"id":"Door"}"""),
        ["magic"] = new("id play at", """{"id":"Relay","at":2}"""),
        ["camnets"] = new(),
        ["propsets"] = new("class id", """{"class":"SCamera"}"""),
        ["tips"] = new(),
        ["zone"] = new("id", """{"id":"Objective"}"""),
        ["modes"] = new("mode"),
        ["stealth"] = new("lights off on stance spacing mercSight night near radius cells limit",
            """{"near":[0,0,64],"radius":256,"cells":true,"limit":10}"""),
        ["reach"] = new("drops mask limit", """{"drops":"unhurt","limit":10}"""),
        ["route"] = new("from to side drops mask", """{"from":[0,0,0],"to":[256,0,0],"side":"spy"}"""),
        ["nav"] = new("side drops mask near radius limit", """{"side":"merc","near":[0,0,64],"radius":256,"limit":10}"""),
        ["match"] = new("players mode", """{"players":4}"""),
        ["outfits"] = new(),
        ["recoveredLights"] = new(),
    };

    public static ApiReply Read(IEnumerable<McpTool> tools, SvJsonObject? args)
    {
        string? kind = ApiJson.GetString(args, "kind");
        string? name = ApiJson.GetString(args, "name");
        if (args?["kind"] is not null && kind is not ("method" or "op" or "query"))
        {
            return ApiReply.Error(new ApiException("kind must be method, op or query", "kind"));
        }
        if (args?["name"] is not null && (kind is null || string.IsNullOrWhiteSpace(name)))
        {
            return ApiReply.Error(new ApiException("give both a kind and a name", "name"));
        }

        if (name is null)
        {
            return ApiReply.Ok(ApiJson.Obj(
                ("methods", kind is null or "method" ? ApiJson.Strings(tools.Select(tool => tool.Name)) : null),
                ("ops", kind is null or "op" ? ApiJson.Strings(ChangeApplier.OpNames) : null),
                ("queries", kind is null or "query" ? ApiJson.Strings(MapQuery.Names) : null)));
        }

        SvJsonObject? entry = kind switch
        {
            "method" => tools.FirstOrDefault(tool => tool.Name == name || tool.Method.Name == name)?.Help(),
            "op" when s_ops.TryGetValue(name, out string? op) => Entry("map_apply",
                ApiJson.Strings(ChangeApplier.OpFields(name)),
                ApiJson.Obj(("dryRun", SvJsonNode.True), ("ops", new SvJsonArray([With("op", name, op)])))),
            "query" when s_queries.TryGetValue(name, out Query? query) => Entry("map_query",
                ApiJson.Strings(query.Fields.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
                With("q", name, query.Example)),
            _ => null,
        };
        return entry is null
            ? ApiReply.Error(new ApiException($"unknown {kind} '{name}'; call map_help with just kind to list names", "name"))
            : ApiReply.Ok(entry);
    }

    private static SvJsonObject With(string field, string name, string example)
    {
        var args = (SvJsonObject)ApiJson.Parse(example);
        args.Add(field, ApiJson.Str(name));
        return args;
    }

    internal static SvJsonObject Entry(string tool, SvJsonNode fields, SvJsonNode arguments, string? description = null) =>
        ApiJson.Obj(("tool", ApiJson.Str(tool)), ("arguments", fields), ("example", arguments),
            ("description", description is null ? null : ApiJson.Str(description)));
}
