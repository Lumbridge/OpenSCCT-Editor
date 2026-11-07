using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;

namespace OpenScctEditor.Panels;

internal static class BeginnerEdits
{
    public static void Apply(EditorContext context, string label, IEnumerable<SvJsonObject> ops)
    {
        ChangeResult result = ChangeApplier.Apply(context.Session, ApiJson.Obj(("format", ApiJson.Str(ChangeApplier.Format)),
            ("label", ApiJson.Str(label)), ("ops", ApiJson.Arr(ops))), writeFiles: false);
        if (!result.Ok) { context.Log(ApiJson.Write(result.Json), Severity.Error); return; }
        var made = ((result.Json["created"] as SvJsonObject)?.Select(kv => kv.Key) ?? [])
            .Select(id => MapObjects.Find(context.Project, id)).OfType<ObjectRef>().ToList();
        if (made.Count > 0) { context.Selection.Set(made); context.Focus(made); }
    }
}
