using System.Text.Json.Nodes;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Interop.Original;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>Collects map dependencies into a portable project library and reports each failed dependency.</summary>
public static class SdcAssets
{
    public static string? ProjectFolder(MapProject project, string? folder) => folder ??
        (project.Extra[HandoffFile.AssetsKey] is SvJsonString s ? Path.GetDirectoryName(s.Value) : null);

    public static void Import(MapProject project, string map, string game, string? addons, string work,
        SvmbuildRunner runner, Action<string>? progress, List<Finding> findings)
    {
        string script = Path.Combine(work, "import_assets.py");
        using (Stream source = typeof(SdcAssets).Assembly.GetManifestResourceStream("OpenScct.Editor.Core.ImportAssets.py")!)
        using (FileStream target = File.Create(script)) source.CopyTo(target);
        string root = Path.Combine(work, "assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string report = Path.Combine(root, "assets-report.json");
        // Never read a successful report left by an earlier attempt if this pass fails.
        progress?.Invoke("collecting the map's textures, meshes, sounds and their dependencies");
        var args = new List<string> { "--map", map, "--game", game, "--content", Path.Combine(work, "content"), "--project", root, "--report", report };
        if (addons is not null) args.AddRange(["--addons", addons]);
        var log = new List<string>();
        int code = runner.RunScript(script, args, work, line => { lock (log) log.Add(line); progress?.Invoke(line); });
        if (File.Exists(Path.Combine(root, "assets", "assets.json")))
            project.Extra.TryAdd(HandoffFile.AssetsKey, SvJsonNode.String(Path.Combine(root, "assets")));
        if (code != 0 || !File.Exists(report))
        {
            findings.Add(Finding.Warning("import.assetsFailed", "The asset dependency pass did not finish; the baked map is kept, but its custom asset library may be incomplete. "
                + string.Join(" ", log.TakeLast(3))));
            return;
        }
        var doc = JsonNode.Parse(File.ReadAllBytes(report))!.AsObject();
        foreach (JsonObject row in doc["assets"]!.AsArray().OfType<JsonObject>())
        {
            string status = row["status"]!.GetValue<string>();
            if (status == "imported") continue;
            string path = row["path"]!.GetValue<string>();
            string detail = row["detail"]?.GetValue<string>() ?? "";
            string uses = string.Join(", ", row["usedBy"]?.AsArray().Select(x => x!.GetValue<string>()) ?? []);
            ObjectRef[] objects = project.Actors.Where(a => row["usedBy"]?.AsArray().Any(x => x!.GetValue<string>().StartsWith(a.Id + ".", StringComparison.OrdinalIgnoreCase)) == true)
                .Select(a => ObjectRef.Actor(a.Id)).ToArray();
            findings.Add(Finding.Warning(status == "missing" ? "import.assetMissing" : "import.assetUnsupported",
                $"{status}: {path} ({row["class"]}) — {detail}" + (uses.Length > 0 ? $"; used by {uses}" : "")
                + (status == "missing" ? ". Restore its package or sound bank beside the map or in its RE+ installation, then import again." : ""), objects));
        }
        findings.Add(Finding.Info("import.assets", $"Asset dependencies: {doc["imported"]} imported, {doc["missing"]} missing, {doc["unsupported"]} unsupported. "
            + "The imported asset files are copied into the project when it is saved; see the Assets sections for anything requiring attention."));
    }
}
