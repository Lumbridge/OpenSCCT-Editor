using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// <c>ui/identity.json</c>, <c>ui/pictures/</c> and <c>ui/text/</c> (SVMap 1.2): the name (the project title), the
/// identity fields, one file per picture slot and one file per translation. File pictures are cropped at their focal point to the slot's shape (square, 16:9 for the
/// banner) and kept at most 2048 pixels and 4 MiB; camera pictures come from the project's render cache
/// (<c>ose mapshot</c>); the plan is drawn through the strategic map camera, so the game may mark the places on it.
/// What a baked source map brought is replaced, except the parts of its translations the project does not hold (see
/// <see cref="WriteText"/>).
/// </summary>
public static class IdentityBuild
{
    public const string IdentityFile = "ui/identity.json";
    public const string PictureFolder = "ui/pictures/";
    public const string TextFolder = "ui/text/";
    public const int MaxPictureSide = 2048;
    public const int MaxPictureBytes = 4 * 1024 * 1024;

    /// <summary>The smallest side the spec asks of a menu or loading picture (1024 recommended).</summary>
    public const int MinPictureSide = 512;

    /// <summary>The banner's shape, width over height (the spec's 16:9).</summary>
    public const double BannerAspect = 16.0 / 9.0;

    /// <summary>The shape a slot's picture is cropped to, width over height.</summary>
    public static double AspectOf(string slot) => slot == "banner" ? BannerAspect : 1.0;

    public static void Write(CompileContext ctx)
    {
        var sourceTexts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string stale in ctx.Files.Keys.Where(k => k == IdentityFile || k.StartsWith(PictureFolder, StringComparison.Ordinal)
            || k.StartsWith(TextFolder, StringComparison.Ordinal)).ToList())
        {
            if (stale.StartsWith(TextFolder, StringComparison.Ordinal) && stale.EndsWith(".json", StringComparison.Ordinal))
            {
                sourceTexts[stale[TextFolder.Length..^".json".Length]] = ctx.Files[stale];
            }

            ctx.Files.Remove(stale);
        }

        MapProject project = ctx.Project;
        ProjectIdentity id = project.Identity;
        var root = new SvJsonObject();
        root.Add("format", SvJsonNode.String("svm.identity/1"));
        root.Add("name", SvJsonNode.String(NameOf(ctx)));
        AddText(root, "family", id.Family, ProjectIdentity.MaxName);
        AddText(root, "variant", id.Variant, ProjectIdentity.MaxName);

        AddText(root, "description", id.Description, ProjectIdentity.MaxDescription);
        if (!id.Dossier.IsEmpty)
        {
            var d = new SvJsonObject();
            AddText(d, "when", id.Dossier.When, ProjectIdentity.MaxDossierLine);
            AddText(d, "where", id.Dossier.Where, ProjectIdentity.MaxDossierLine);
            AddText(d, "summary", id.Dossier.Summary, ProjectIdentity.MaxDescription);
            root.Add("dossier", d);
        }

        List<string> authors = id.Authors.Select(a => a.Trim()).Where(a => a.Length is > 0 and <= ProjectIdentity.MaxName).Distinct().Take(64).ToList();
        if (authors.Count > 0)
        {
            root.Add("authors", new SvJsonArray(authors.Select(SvJsonNode.String)));
        }

        AddText(root, "version", id.Version, 32);
        root.Add("language", SvJsonNode.String(ProjectIdentity.IsLanguage(id.Language) ? id.Language : "en"));
        if (id.RecommendedPlayers is { } players)
        {
            var p = new SvJsonObject();
            p.Add("recommended", SvJsonNode.Integer(Math.Clamp(players, 1, ProjectIdentity.MaxPlayers)));
            root.Add("players", p);
        }

        List<string> tags = id.Tags.Where(ProjectIdentity.IsTag).Distinct().Take(ProjectIdentity.MaxTags).ToList();
        if (tags.Count > 0)
        {
            root.Add("tags", new SvJsonArray(tags.Select(SvJsonNode.String)));
        }

        if (Places(ctx, id) is { Count: > 0 } places)
        {
            root.Add("places", places);
        }

        if (Pictures(ctx) is { Count: > 0 } pictures)
        {
            root.Add("pictures", pictures);
        }

        ctx.Files[IdentityFile] = CanonicalJson.ToFileBytes(root);
        foreach (string lang in id.Texts.Keys.Union(sourceTexts.Keys).Order(StringComparer.Ordinal))
        {
            WriteText(ctx, lang, id.Texts.GetValueOrDefault(lang), sourceTexts.TryGetValue(lang, out byte[]? source) ? Parse(ctx, lang, source) : null);
        }
    }

    /// <summary>A source map's translation, or null (with a warning) when it is not a JSON object.</summary>
    private static SvJsonObject? Parse(CompileContext ctx, string lang, byte[] source)
    {
        try
        {
            if (SvJson.Parse(source, TextFolder + lang + ".json") is SvJsonObject o)
            {
                return o;
            }
        }
        catch (SvMapFormatException)
        {
        }

        ctx.Warn("identity.translation", $"the source map's '{lang}' texts are not a JSON object: left out");
        return null;
    }

    /// <summary>
    /// <c>ui/text/&lt;lang&gt;.json</c>: the project's translated identity texts and the two legends made from them, as
    /// the base language's are (<see cref="SideFiles.LegendOf(Dossier)"/>), with the map's own text wherever the
    /// translation has none, so the game can show either legend whole. The rest of a source map's file for the language
    /// (its other <c>mapinfo</c> strings, <c>mapinfo.modes</c>, <c>actors</c>), which the project does not hold, is
    /// kept; its <c>identity</c> is the project's (an import reads it in).
    /// </summary>
    private static void WriteText(CompileContext ctx, string lang, IdentityText? translation, SvJsonObject? source)
    {
        ProjectIdentity id = ctx.Project.Identity;
        if (!ProjectIdentity.IsLanguage(lang) || lang == id.Language)
        {
            if (lang == id.Language && translation is { IsEmpty: false })
            {
                ctx.Warn("identity.translation", $"the texts in '{lang}' are in the map's own language: they are left out (edit the map's texts instead)");
            }

            return;
        }

        IdentityText text = translation ?? new IdentityText();
        foreach ((string what, string value, int max) in text.Fields().Where(f => f.Value.Trim().Length > f.Max))
        {
            ctx.Warn("identity.translation", $"the '{lang}' {what} has {value.Trim().Length} characters: only the first {max} are kept");
        }

        var identity = new SvJsonObject();
        AddText(identity, "name", text.Name, ProjectIdentity.MaxName);
        AddText(identity, "description", text.Description, ProjectIdentity.MaxDescription);
        var dossier = new SvJsonObject();
        AddText(dossier, "when", text.Dossier.When, ProjectIdentity.MaxDossierLine);
        AddText(dossier, "where", text.Dossier.Where, ProjectIdentity.MaxDossierLine);
        AddText(dossier, "summary", text.Dossier.Summary, ProjectIdentity.MaxDescription);
        if (dossier.Count > 0)
        {
            identity.Add("dossier", dossier);
        }

        var places = new SvJsonObject();
        foreach ((int number, string name) in text.Places.Where(p => p.Value.Trim().Length > 0))
        {
            if (id.Places.Any(p => p.Number == number))
            {
                AddText(places, number.ToString(System.Globalization.CultureInfo.InvariantCulture), name, ProjectIdentity.MaxName);
            }
            else
            {
                ctx.Warn("identity.translation", $"the '{lang}' texts name place {number}, which the map does not have: left out");
            }
        }

        if (places.Count > 0)
        {
            identity.Add("places", places);
        }

        var mapInfo = new SvJsonObject();
        if (dossier.Count > 0)
        {
            Dossier merged = new()
            {
                When = Or(text.Dossier.When, id.Dossier.When),
                Where = Or(text.Dossier.Where, id.Dossier.Where),
                Summary = Or(text.Dossier.Summary, id.Dossier.Summary),
            };
            mapInfo.Add(SideFiles.LegendMap, SvJsonNode.String(SideFiles.LegendOf(merged)));
        }

        if (places.Count > 0)
        {
            IEnumerable<Place> named = id.Places.Select(p => new Place
            {
                Number = p.Number,
                Name = text.Places.TryGetValue(p.Number, out string? name) && name.Trim().Length > 0 ? name.Trim() : p.Name,
            });
            mapInfo.Add(SideFiles.LegendMapInBrief, SvJsonNode.String(SideFiles.LegendOf(named)));
        }

        if (source?["mapinfo"] is SvJsonObject sourceInfo)
        {
            foreach ((string key, SvJsonNode value) in sourceInfo)
            {
                mapInfo.TryAdd(key, value);
            }
        }

        var root = new SvJsonObject();
        root.Add("format", SvJsonNode.String("svm.text/1"));
        if (identity.Count > 0)
        {
            root.Add("identity", identity);
        }

        if (mapInfo.Count > 0)
        {
            root.Add("mapinfo", mapInfo);
        }

        foreach ((string key, SvJsonNode value) in source?.Where(m => m.Key is not ("format" or "identity" or "mapinfo")) ?? [])
        {
            root.Add(key, value);
        }

        if (root.Count > 1)
        {
            ctx.Files[TextFolder + lang + ".json"] = CanonicalJson.ToFileBytes(root);
        }
    }

    private static string Or(string text, string fallback) => text.Trim() is { Length: > 0 } t ? t : fallback;

    /// <summary>The map's name: the project title, else its map code; a title past the spec's limit is cut.</summary>
    private static string NameOf(CompileContext ctx)
    {
        string name = ctx.Project.Title.Trim();
        if (name.Length == 0)
        {
            ctx.Warn("identity.name", $"the map has no title: menus show its map code, {ctx.Project.Id}");
            return ctx.Project.Id;
        }

        if (name.Length > ProjectIdentity.MaxName)
        {
            ctx.Warn("identity.name", $"the title has {name.Length} characters: menus show its first {ProjectIdentity.MaxName}");
            return name[..ProjectIdentity.MaxName].TrimEnd();
        }

        return name;
    }

    /// <summary>The places with a position, by number (places known only by name stay in the legend text).</summary>
    private static SvJsonArray Places(CompileContext ctx, ProjectIdentity id)
    {
        var list = new SvJsonArray();
        var seen = new HashSet<int>();
        foreach (Place place in id.Places.Where(p => p.At is not null && p.Number is >= 1 and <= 99).OrderBy(p => p.Number))
        {
            if (seen.Add(place.Number))
            {
                var o = new SvJsonObject();
                o.Add("number", SvJsonNode.Integer(place.Number));
                AddText(o, "name", place.Name, ProjectIdentity.MaxName);
                Vec3 at = place.At!.Value;
                o.Add("at", new SvJsonArray([SvJsonNode.Float((float)at.X), SvJsonNode.Float((float)at.Y), SvJsonNode.Float((float)at.Z)]));
                if (place.Floor is int floor and >= 0)
                {
                    if (ctx.StrategicMap is { } map && floor >= map.Floors.Count)
                    {
                        // Automatic floors are only known now: a place set on a floor the map does not have uses its height.
                        ctx.Warn("identity.places",
                            $"place {place.Number} is on floor {floor} and the map has {map.Floors.Count}: it goes on the floor of its height");
                    }
                    else
                    {
                        o.Add("floor", SvJsonNode.Integer(floor));
                    }
                }

                list.Add(o);
            }
        }

        return list;
    }

    private static SvJsonObject Pictures(CompileContext ctx)
    {
        IdentityPictures slots = ctx.Project.Identity.Pictures;
        var o = new SvJsonObject();
        var generated = new SortedSet<string>(StringComparer.Ordinal);
        string? mapKey = null;
        byte[]? plan = null;
        (byte[], string, bool)? Plan(string slot)
        {
            if (ctx.StrategicMap is not { Camera: not null } map)
            {
                ctx.Warn("identity.picture", $"picture {slot}: the plan needs a strategic map, and this map has none");
                return null;
            }

            ctx.Progress("plan picture");
            plan ??= StrategicMapBuild.Draw(map, floor: null).EncodePng();
            return (plan, ".png", true);
        }

        string? Make(string slot, PictureRecipe recipe, bool menu = false)
        {
            if (recipe.From == PictureSource.Camera)
            {
                mapKey ??= MapShots.MapKey(ctx.Files);
            }

            if (recipe.From == PictureSource.Plan && AspectOf(slot) != 1)
            {
                ctx.Warn("identity.picture", $"picture {slot}: the plan is square and this picture is 16:9; give it a file or a camera");
                return null;
            }

            (byte[] Data, string Ext, bool Plan)? made = recipe.From switch
            {
                PictureSource.File => FromFile(ctx, slot, recipe),
                PictureSource.Camera => FromCache(ctx, slot, recipe, mapKey),
                _ => Plan(slot),
            };
            if (made is null && menu && recipe.From == PictureSource.Camera)
            {
                // Without a render the menu shows the plan rather than nothing.
                ctx.Warn("identity.picture-render", "picture menu: the generated plan stands in until the camera picture is rendered");
                made = Plan(slot);
            }

            if (made is not { } m)
            {
                return null;
            }

            if (m.Plan)
            {
                generated.Add(slot.StartsWith("loading", StringComparison.Ordinal) ? "loading" : slot);
            }

            string rel = $"pictures/{slot}{m.Ext}";
            ctx.Files["ui/" + rel] = m.Data;
            return rel;
        }

        if (slots.Menu is { } menuRecipe && Make("menu", menuRecipe, menu: true) is { } menuFile)
        {
            o.Add("menu", SvJsonNode.String(menuFile));
        }

        var spy = Loading(slots.LoadingSpy, "loading-spy", Make);
        var merc = Loading(slots.LoadingMerc, "loading-merc", Make);
        if (spy.Count > 0 || merc.Count > 0)
        {
            var loading = new SvJsonObject();
            loading.Add("spy", spy);
            loading.Add("merc", merc);
            o.Add("loading", loading);
        }

        if (slots.Briefing is { } briefingRecipe && Make("briefing", briefingRecipe) is { } briefing)
        {
            o.Add("briefing", SvJsonNode.String(briefing));
            o.Add("briefingProjection", briefingRecipe.From == PictureSource.Plan ? SvJsonNode.String("snapshot") : SvJsonNode.Null);
        }

        if (slots.Banner is { } bannerRecipe && Make("banner", bannerRecipe) is { } banner)
        {
            o.Add("banner", SvJsonNode.String(banner));
        }

        if (generated.Count > 0)
        {
            o.Add("generated", new SvJsonArray(generated.Select(SvJsonNode.String)));
        }

        ctx.Count("identity.pictures", ctx.Files.Keys.Count(k => k.StartsWith(PictureFolder, StringComparison.Ordinal)));
        return o;
    }

    private static SvJsonArray Loading(List<PictureRecipe> recipes, string stem, Func<string, PictureRecipe, bool, string?> make)
    {
        var list = new SvJsonArray();
        for (int i = 0; i < recipes.Count && i < ProjectIdentity.MaxPictures; i++)
        {
            if (make($"{stem}-{i + 1}", recipes[i], false) is { } file)
            {
                list.Add(SvJsonNode.String(file));
            }
        }

        return list;
    }

    /// <summary>A JPEG that already has the slot's shape and is within the limits is kept as it is (re-encoding would
    /// lose quality or grow it); every other picture (PNG, JPEG, BMP, WebP) is cropped and scaled as needed and stored as
    /// PNG.</summary>
    private static (byte[], string, bool)? FromFile(CompileContext ctx, string slot, PictureRecipe recipe)
    {
        if (recipe.File is not { Length: > 0 } file || ProjectPictures.Resolve(ctx.Options.ProjectFolder, file) is not { } path || !File.Exists(path))
        {
            ctx.Warn("identity.picture", $"picture {slot}: the file {recipe.File} is missing");
            return null;
        }

        byte[] data = File.ReadAllBytes(path);
        double aspect = AspectOf(slot);
        bool original = Interop.Svmap.IdentityImport.CameWithMap(ctx.Project, path);
        if (RgbaImage.JpegSize(data) is { } jpeg && HasShape(jpeg.Width, jpeg.Height, aspect) && Math.Max(jpeg.Width, jpeg.Height) <= MaxPictureSide
            && data.Length <= MaxPictureBytes)
        {
            WarnIfSmall(ctx, slot, jpeg.Width, jpeg.Height, original);
            return (data, ".jpg", false);
        }

        try
        {
            RgbaImage image = RgbaImage.Decode(data).Crop(recipe.FocusX, recipe.FocusY, aspect).FitWithin(MaxPictureSide);
            byte[] png = image.EncodePng();
            bool shrunk = false;
            while (png.Length > MaxPictureBytes && Math.Max(image.Width, image.Height) > 256)
            {
                image = image.FitWithin(Math.Max(image.Width, image.Height) / 2);
                png = image.EncodePng();
                shrunk = true;
            }

            if (shrunk)
            {
                ctx.Warn("identity.picture", $"picture {slot}: {Path.GetFileName(path)} passes {MaxPictureBytes / (1024 * 1024)} MiB as PNG, so it is "
                    + $"scaled to {image.Width}x{image.Height}; a simpler or smaller picture keeps more detail");
            }

            WarnIfSmall(ctx, slot, image.Width, image.Height, original);
            return (png, ".png", false);
        }
        catch (InvalidDataException e)
        {
            ctx.Warn("identity.picture", $"picture {slot}: {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    /// <summary>True when a picture already has the shape: within a pixel of it, as an even crop leaves it.</summary>
    private static bool HasShape(int width, int height, double aspect) => Math.Abs(width - height * aspect) <= 1;

    /// <summary>Warns about a picture under the menus' size, unless the map came with it (stock pictures are 256 wide).</summary>
    private static void WarnIfSmall(CompileContext ctx, string slot, int width, int height, bool original)
    {
        if (!original && slot != "banner" && Math.Min(width, height) < MinPictureSide)
        {
            ctx.Warn("identity.picture", $"picture {slot} is {width}x{height}; menus ask for at least {MinPictureSide}x{MinPictureSide} (1024 looks best)");
        }
    }

    /// <summary>A camera picture from the render cache: a render of this version of the map, else an earlier one (said
    /// to be out of date), else none (said to be missing).</summary>
    private static (byte[], string, bool)? FromCache(CompileContext ctx, string slot, PictureRecipe recipe, string? mapKey)
    {
        if (ctx.Project.Workspace.Bookmarks.FirstOrDefault(b => b.Name == recipe.Bookmark) is not { } bookmark)
        {
            ctx.Warn("identity.picture", $"picture {slot}: there is no bookmark named '{recipe.Bookmark}'");
            return null;
        }

        (int width, int height) = MapShots.SizeOf(slot);
        string key = MapShots.Key(recipe, bookmark, width, height);
        ShotState state = ctx.Options.ProjectFolder is { } folder ? MapShots.StateOf(folder, key, mapKey) : ShotState.Missing;
        if (state == ShotState.Missing)
        {
            ctx.Warn("identity.picture-render", $"picture {slot}: the camera picture from bookmark '{bookmark.Name}' is not rendered yet; "
                + "run ose mapshot (or Render now in the Identity panel)");
            return null;
        }

        if (state == ShotState.Stale)
        {
            ctx.Warn("identity.picture-render", $"picture {slot}: the camera picture from bookmark '{bookmark.Name}' was rendered from an earlier "
                + "version of the map; run ose mapshot (or Render now in the Identity panel) to bring it up to date");
        }

        return (File.ReadAllBytes(MapShots.CachePath(ctx.Options.ProjectFolder!, key)), ".png", false);
    }

    private static void AddText(SvJsonObject o, string key, string? value, int max)
    {
        if (value?.Trim() is { Length: > 0 } text)
        {
            o.Add(key, SvJsonNode.String(text.Length > max ? text[..max] : text));
        }
    }
}
