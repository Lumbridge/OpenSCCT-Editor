using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Original;

/// <summary>The native settings and conventionally named menu/loading textures of a map's -i.utc package.</summary>
internal static class ReloadedInterface
{
    public static int ModeId(string name) => name.ToLowerInvariant() switch
    {
        "extraction" => 1, "sabotage" => 2, "neutralization" => 3, "diskhunt" => 4,
        "story" => 5, "deathmatch" => 6, "mercdeathmatch" => 7,
        _ => throw new InvalidDataException($"The mode '{name}' has no original-game GameModeId. Set its native mode id before exporting."),
    };

    public static byte[] Build(MapProject p, ExportOptions options, UeSchema? schema, List<Finding> findings)
    {
        var b = new UPackageBuilder(p.Id + "-i");
        UeSchema? textureSchema = schema;
        schema ??= SettingsSchema();
        var props = new PropSet();
        foreach (var value in p.MapInfo.Where(v => v.Value is SvJsonString)) props.Set(value.Key, value.Value);
        props.Set("MapName", SvJsonNode.String(p.Title.Length > 0 ? p.Title : p.Id));
        props.Set("IsAmateur", SvJsonNode.Integer(-1));
        if (!p.Identity.Dossier.IsEmpty) props.Set(SideFiles.LegendMap, SvJsonNode.String(SideFiles.LegendOf(p.Identity.Dossier)));
        if (p.Identity.Places.Count > 0) props.Set(SideFiles.LegendMapInBrief, SvJsonNode.String(SideFiles.LegendOf(p.Identity.Places)));
        var modes = new SvJsonArray(); var texts = new SvJsonArray();
        foreach (string mode in InterfaceTexts.ModeOrder(p).Where(p.MapSettings.ContainsKey))
        {
            modes.Add(p.MapSettings[mode].ToJson());
            texts.Add((p.MapInfo["modes"] as SvJsonObject)?[mode] ?? new SvJsonObject());
        }
        props.Set("Modes", modes); props.Set("ModesStrings", texts);
        var writer = new UPropertyWriter(schema, _ => 0);
        b.Exports.Add(new UExportSlot("MapSettings", b.ImportClass("SBase", "SMapSettings"), 0x000F0004)
        { Write = w => writer.WriteObject(w, "SBase.SMapSettings", "SBase.SMapSettings", props, "MapSettings") });
        foreach ((string slot, PictureRecipe recipe) in p.Identity.Pictures.Slots())
        {
            if (slot == "banner") continue; // Reloaded has no banner slot.
            string name = slot switch
            {
                "menu" => "Menu", "briefing" => "Briefing",
                _ when slot.StartsWith("loading-spy-", StringComparison.Ordinal) => "LoadingATT" + (int.Parse(slot[12..]) - 1),
                _ when slot.StartsWith("loading-merc-", StringComparison.Ordinal) => "LoadingDEF" + (int.Parse(slot[13..]) - 1),
                _ => throw new InvalidDataException("Unknown picture slot " + slot),
            };
            try
            {
                string? file = recipe.From == PictureSource.File && recipe.File is { } image ? ProjectPictures.Resolve(options.ProjectFolder, image) : null;
                if (recipe.From == PictureSource.Camera && options.ProjectFolder is { } projectFolder
                    && p.Workspace.Bookmarks.FirstOrDefault(k => k.Name == recipe.Bookmark) is { } bookmark)
                {
                    string key = MapShots.Key(recipe, bookmark, 1024, 1024);
                    file = MapShots.CachePath(projectFolder, key);
                }
                if (file is null || !File.Exists(file))
                {
                    findings.Add(Finding.Warning("export.picture", $"{slot}: render this picture or select an image file to include it in Reloaded."));
                    continue;
                }
                var picture = RgbaImage.Decode(File.ReadAllBytes(file)).Crop(recipe.FocusX, recipe.FocusY, 1).Resize(512, 512);
                b.Exports.Add(new UExportSlot(name, b.ImportClass("Engine", "Texture"), 0x000F0004)
                { Write = UTextureWriter.Prepare(picture, false, false, textureSchema) });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            { findings.Add(Finding.Warning("export.picture", slot + ": " + ex.Message)); }
        }
        byte[] result = b.Build();
        if (writer.Problems.Count > 0) throw new InvalidDataException(string.Join("; ", writer.Problems));
        return UPackageBuilder.FileBytes(p.Id + "-i.utc", result);
    }

    // Only these two structs are native SMapSettings fields. Keep them typed even without converted content.
    private static UeSchema SettingsSchema()
    {
        SvJsonObject Prop(string name, string type) { var o = new SvJsonObject(); o.Add("name", SvJsonNode.String(name)); o.Add("type", SvJsonNode.String(type)); return o; }
        var properties = new SvJsonArray(new[] { "MapName", "LegendMap", "LegendMapInBrief", "ExpliMapInBriefSpy", "ExpliMapInBriefSentry" }.Select(n => Prop(n, "string")));
        properties.Add(Prop("IsAmateur", "int"));
        foreach (string name in new[] { "Modes", "ModesStrings" })
        {
            var property = Prop(name, "array"); var inner = Prop(name, "struct");
            inner.Add("struct", SvJsonNode.String("SBase.SMapSettings." + (name == "Modes" ? "GameModeInfo" : "GameModeInfoStrings")));
            property.Add("inner", inner); properties.Add(property);
        }
        var cls = new SvJsonObject(); cls.Add("properties", properties);
        var classes = new SvJsonObject(); classes.Add("SBase.SMapSettings", cls);
        var modeFields = new SvJsonArray(new[] { "TimeLimit", "ScoreLimit", "SpiesLives", "RespawnTime", "SentriesLives", "Handicap", "NumDisksMax" }.Select(n => Prop(n, "int")));
        modeFields.Add(Prop("GameModeId", "byte"));
        var textFields = new SvJsonArray(new[] { "ExplicationModeSpy", "ExplicationModeSentry", "ExpliModeInBriefSpy", "LegendModeInBriefSpy", "ExpliModeInBriefSentry", "LegendModeInBriefSentry" }.Select(n => Prop(n, "string")));
        var structs = new SvJsonObject();
        foreach (var entry in new[] { ("GameModeInfo", modeFields), ("GameModeInfoStrings", textFields) })
        { var type = new SvJsonObject(); type.Add("properties", entry.Item2); structs.Add("SBase.SMapSettings." + entry.Item1, type); }
        var root = new SvJsonObject(); root.Add("classes", classes); root.Add("structs", structs);
        return UeSchema.Parse(root, "Reloaded SMapSettings");
    }
}
