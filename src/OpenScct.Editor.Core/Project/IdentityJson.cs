using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Project;

/// <summary>
/// The <c>identity</c> section of <c>map.json</c> (docs/FORMAT.md "Map identity"). Empty fields are left out, so a
/// project without an identity writes no section. Reading is forgiving: an unknown or broken field keeps its default.
/// </summary>
public static class IdentityJson
{
    public static SvJsonObject ToJson(ProjectIdentity id)
    {
        var o = new SvJsonObject();
        AddText(o, "family", id.Family);
        AddText(o, "variant", id.Variant);
        AddText(o, "description", id.Description);
        if (!id.Dossier.IsEmpty)
        {
            var d = new SvJsonObject();
            d.Add("when", SvJsonNode.String(id.Dossier.When));
            d.Add("where", SvJsonNode.String(id.Dossier.Where));
            d.Add("summary", SvJsonNode.String(id.Dossier.Summary));
            o.Add("dossier", d);
        }

        if (id.Authors.Count > 0)
        {
            o.Add("authors", new SvJsonArray(id.Authors.Select(SvJsonNode.String)));
        }

        AddText(o, "version", id.Version);
        if (id.Language != "en")
        {
            o.Add("language", SvJsonNode.String(id.Language));
        }

        if (id.Texts.Count > 0)
        {
            var texts = new SvJsonObject();
            foreach ((string lang, IdentityText text) in id.Texts)
            {
                texts.Add(lang, TextToJson(text));
            }

            o.Add("texts", texts);
        }

        if (id.RecommendedPlayers is { } players)
        {
            var p = new SvJsonObject();
            p.Add("recommended", SvJsonNode.Integer(players));
            o.Add("players", p);
        }

        if (id.Tags.Count > 0)
        {
            o.Add("tags", new SvJsonArray(id.Tags.Select(SvJsonNode.String)));
        }

        if (id.Places.Count > 0)
        {
            o.Add("places", new SvJsonArray(id.Places.Select(PlaceToJson)));
        }

        if (PicturesToJson(id.Pictures) is { Count: > 0 } pictures)
        {
            o.Add("pictures", pictures);
        }

        if (id.Floors is { } floors)
        {
            o.Add("floors", new SvJsonArray(floors.Select(z => SvJsonNode.Float((float)z))));
        }

        return o;
    }

    public static ProjectIdentity FromJson(SvJsonNode? node)
    {
        var id = new ProjectIdentity();
        if (node is not SvJsonObject o)
        {
            return id;
        }

        id.Family = ModelJson.Str(o, "family");
        id.Variant = ModelJson.Str(o, "variant");
        id.Description = ModelJson.Str(o, "description");
        if (o["dossier"] is SvJsonObject d)
        {
            id.Dossier = new Dossier { When = ModelJson.Str(d, "when") ?? "", Where = ModelJson.Str(d, "where") ?? "", Summary = ModelJson.Str(d, "summary") ?? "" };
        }

        id.Authors = Strings(o["authors"]);
        id.Version = ModelJson.Str(o, "version");
        id.Language = ModelJson.Str(o, "language") is { } lang && ProjectIdentity.IsLanguage(lang) ? lang : "en";
        if (o["texts"] is SvJsonObject texts)
        {
            foreach ((string code, SvJsonNode text) in texts)
            {
                if (ProjectIdentity.IsLanguage(code) && text is SvJsonObject t)
                {
                    id.Texts[code] = TextFromJson(t);
                }
            }
        }

        if (o["players"] is SvJsonObject p && p["recommended"] is SvJsonNumber n)
        {
            id.RecommendedPlayers = (int)ModelJson.Num(n);
        }

        id.Tags = Strings(o["tags"]);
        if (o["places"] is SvJsonArray places)
        {
            id.Places = places.OfType<SvJsonObject>().Select(PlaceFromJson).ToList();
        }

        if (o["pictures"] is SvJsonObject pictures)
        {
            id.Pictures = PicturesFromJson(pictures);
        }

        if (o["floors"] is SvJsonArray floors)
        {
            id.Floors = floors.OfType<SvJsonNumber>().Select(z => ModelJson.Num(z)).ToList();
        }

        return id;
    }

    /// <summary>A translation as map.json keeps it: only the texts it has.</summary>
    public static SvJsonObject TextToJson(IdentityText t)
    {
        var o = new SvJsonObject();
        AddText(o, "name", t.Name);
        AddText(o, "description", t.Description);
        if (!t.Dossier.IsEmpty)
        {
            var d = new SvJsonObject();
            AddText(d, "when", t.Dossier.When);
            AddText(d, "where", t.Dossier.Where);
            AddText(d, "summary", t.Dossier.Summary);
            o.Add("dossier", d);
        }

        if (t.Places.Any(p => p.Value.Length > 0))
        {
            var places = new SvJsonObject();
            foreach ((int number, string name) in t.Places.Where(p => p.Value.Length > 0))
            {
                places.Add(number.ToString(System.Globalization.CultureInfo.InvariantCulture), SvJsonNode.String(name));
            }

            o.Add("places", places);
        }

        return o;
    }

    public static IdentityText TextFromJson(SvJsonObject o)
    {
        var t = new IdentityText { Name = ModelJson.Str(o, "name") ?? "", Description = ModelJson.Str(o, "description") ?? "" };
        if (o["dossier"] is SvJsonObject d)
        {
            t.Dossier = new Dossier { When = ModelJson.Str(d, "when") ?? "", Where = ModelJson.Str(d, "where") ?? "", Summary = ModelJson.Str(d, "summary") ?? "" };
        }

        if (o["places"] is SvJsonObject places)
        {
            foreach ((string key, SvJsonNode name) in places)
            {
                if (int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number)
                    && number is >= 1 and <= 99 && name is SvJsonString { Value.Length: > 0 } s)
                {
                    t.Places[number] = s.Value;
                }
            }
        }

        return t;
    }

    public static SvJsonObject PlaceToJson(Place p)
    {
        var o = new SvJsonObject();
        o.Add("number", SvJsonNode.Integer(p.Number));
        o.Add("name", SvJsonNode.String(p.Name));
        if (p.At is { } at)
        {
            o.Add("at", ModelJson.Vec(at));
        }

        if (p.Floor is { } floor)
        {
            o.Add("floor", SvJsonNode.Integer(floor));
        }

        return o;
    }

    public static Place PlaceFromJson(SvJsonObject o) => new()
    {
        Number = (int)ModelJson.Num(o["number"]),
        Name = ModelJson.Str(o, "name") ?? "",
        At = o["at"] is SvJsonArray ? ModelJson.ReadVec(o["at"]) : null,
        Floor = o["floor"] is SvJsonNumber f ? (int)ModelJson.Num(f) : null,
    };

    public static SvJsonObject RecipeToJson(PictureRecipe r)
    {
        var o = new SvJsonObject();
        o.Add("from", SvJsonNode.String(SourceName(r.From)));
        switch (r.From)
        {
            case PictureSource.File:
                o.Add("file", SvJsonNode.String(r.File ?? ""));
                o.Add("focus", new SvJsonArray([SvJsonNode.Float((float)r.FocusX), SvJsonNode.Float((float)r.FocusY)]));
                break;
            case PictureSource.Camera:
                o.Add("bookmark", SvJsonNode.String(r.Bookmark ?? ""));
                o.Add("look", SvJsonNode.String(r.Look));
                o.Add("fov", SvJsonNode.Float((float)r.Fov));
                break;
        }

        return o;
    }

    /// <summary>A recipe, or null when <paramref name="node"/> is not one.</summary>
    public static PictureRecipe? RecipeFromJson(SvJsonNode? node)
    {
        if (node is not SvJsonObject o || ModelJson.Str(o, "from") is not { } from || SourceOf(from) is not { } source)
        {
            return null;
        }

        var r = new PictureRecipe { From = source, File = ModelJson.Str(o, "file"), Bookmark = ModelJson.Str(o, "bookmark") };
        if (o["focus"] is SvJsonArray { Count: 2 } focus)
        {
            r.FocusX = Math.Clamp(ModelJson.Num(focus[0]), 0, 1);
            r.FocusY = Math.Clamp(ModelJson.Num(focus[1]), 0, 1);
        }

        if (ModelJson.Str(o, "look") is { } look && PictureRecipe.Looks.Contains(look))
        {
            r.Look = look;
        }

        if (o["fov"] is SvJsonNumber fov)
        {
            r.Fov = Math.Clamp(ModelJson.Num(fov), PictureRecipe.MinFov, PictureRecipe.MaxFov);
        }

        return r;
    }

    public static string SourceName(PictureSource s) => s switch
    {
        PictureSource.File => "file",
        PictureSource.Camera => "camera",
        _ => "plan",
    };

    public static PictureSource? SourceOf(string name) => name switch
    {
        "file" => PictureSource.File,
        "camera" => PictureSource.Camera,
        "plan" => PictureSource.Plan,
        _ => null,
    };

    private static SvJsonObject PicturesToJson(IdentityPictures p)
    {
        var o = new SvJsonObject();
        if (p.Menu is not null)
        {
            o.Add("menu", RecipeToJson(p.Menu));
        }

        if (p.LoadingSpy.Count > 0 || p.LoadingMerc.Count > 0)
        {
            var loading = new SvJsonObject();
            loading.Add("spy", new SvJsonArray(p.LoadingSpy.Select(RecipeToJson)));
            loading.Add("merc", new SvJsonArray(p.LoadingMerc.Select(RecipeToJson)));
            o.Add("loading", loading);
        }

        if (p.Briefing is not null)
        {
            o.Add("briefing", RecipeToJson(p.Briefing));
        }

        if (p.Banner is not null)
        {
            o.Add("banner", RecipeToJson(p.Banner));
        }

        return o;
    }

    private static IdentityPictures PicturesFromJson(SvJsonObject o)
    {
        var p = new IdentityPictures { Menu = RecipeFromJson(o["menu"]), Briefing = RecipeFromJson(o["briefing"]), Banner = RecipeFromJson(o["banner"]) };
        if (o["loading"] is SvJsonObject loading)
        {
            p.LoadingSpy = Recipes(loading["spy"]);
            p.LoadingMerc = Recipes(loading["merc"]);
        }

        return p;
    }

    private static List<PictureRecipe> Recipes(SvJsonNode? node) =>
        node is SvJsonArray a ? a.Select(RecipeFromJson).OfType<PictureRecipe>().ToList() : new List<PictureRecipe>();

    private static List<string> Strings(SvJsonNode? node) =>
        node is SvJsonArray a ? a.OfType<SvJsonString>().Select(s => s.Value).Where(s => s.Length > 0).ToList() : new List<string>();

    private static void AddText(SvJsonObject o, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            o.Add(key, SvJsonNode.String(value));
        }
    }
}
