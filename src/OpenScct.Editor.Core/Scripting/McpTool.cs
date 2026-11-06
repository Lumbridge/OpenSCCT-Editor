using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>Compact discovery metadata for the MCP view of the scripting API.</summary>
internal sealed record McpTool(ApiMethod Method)
{
    private sealed record Info(string Description, string Example, bool ReadOnly = false);

    private static readonly Dictionary<string, Info> s_info = new(StringComparer.Ordinal)
    {
        ["apply"] = new("Apply one undoable change document. Use dryRun first.", """{"dryRun":true,"ops":[{"op":"create","class":"SCamera","at":[0,0,128]}]}"""),
        ["query"] = new("Read map data. Use fields, rows and limit, or batch with queries.", """{"q":"find","kind":"brush","fields":["id","bounds"],"rows":true,"limit":5}""", true),
        ["summary"] = new("Start here for map bounds, objects, starts and findings.", """{"classes":5}""", true),
        ["open"] = new("Open a project or import a map into the current session.", """{"path":"C:/maps/Example.osmap"}"""),
        ["new"] = new("Create a map, with a starter room by default.", """{"id":"DemoD","title":"Demo"}"""),
        ["save"] = new("Save the current project to its folder or to path.", """{"path":"C:/maps/Example.osmap"}"""),
        ["undo"] = new("Undo the last change document or editor step.", """{"steps":1}"""),
        ["redo"] = new("Redo an undone change.", """{"steps":1}"""),
        ["build"] = new("Compile the map to an OpenSCCT SVMap folder.", """{"fast":true}"""),
        ["export"] = new("Export for the original game as T3D, SDC, or a RE+ folder.", """{"format":"t3d"}"""),
        ["playtest"] = new("Build and run a headless round or a scripted route.", """{"side":"spy","bots":0,"seconds":10}"""),
        ["check"] = new("Find mapping problems and suggested fixes. Deep checks compile the map.", """{"limit":10}""", true),
        ["propose"] = new("Show a ghost edit for a user to accept or reject.", """{"label":"Preview camera","ops":[{"op":"create","class":"SCamera","at":[0,0,128]}]}"""),
        ["proposal"] = new("Read the latest proposal status or look up an id.", "{}", true),
        ["editor"] = new("Read the active map, selection, view and proposal state.", "{}", true),
        ["ui.shot"] = new("Capture live map viewports with views:true, or the full UI in an isolated background editor.", """{"name":"overview","views":true,"lint":false}"""),
        ["ui.input"] = new("Send synthetic input to an isolated background editor.", """{"steps":[{"op":"escape"}]}"""),
        ["ui.tree"] = new("Inspect controls and target paths in a background editor.", """{"limit":20}""", true),
        ["help"] = new("List available names, or get one method, op or query example.", """{"kind":"op","name":"create"}""", true),
    };

    private Info Metadata => s_info[Method.Name];

    public static bool Supports(string method) => s_info.ContainsKey(method);

    public string Name => Method.Name switch
    {
        "editor" => "editor_state",
        "ui.shot" => "ui_shot",
        "ui.input" => "ui_input",
        "ui.tree" => "ui_tree",
        _ => "map_" + Method.Name,
    };

    public SvJsonObject Describe() => ApiJson.Obj(
        ("name", ApiJson.Str(Name)),
        ("title", ApiJson.Str(Method.Title)),
        ("description", ApiJson.Str(Metadata.Description)),
        ("inputSchema", Method.SchemaJson),
        ("outputSchema", Method.Name == "query" ? null : ApiJson.Obj(("type", ApiJson.Str("object")))),
        ("annotations", ApiJson.Obj(("readOnlyHint", ApiJson.Bool(Metadata.ReadOnly)), ("openWorldHint", SvJsonNode.False))));

    public SvJsonObject Help() => McpHelp.Entry(Name, Method.SchemaJson, ApiJson.Parse(Metadata.Example),
        Method.Name == "ui.input" ? Method.Description : Metadata.Description);
}
