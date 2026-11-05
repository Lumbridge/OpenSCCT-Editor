using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;

namespace OpenScct.Editor.Core.Scripting;

public static partial class ChangeApplier
{
    private static string[] IdentityKeys =>
        ["name", "family", "variant", "description", "dossier", "authors", "version", "language", "texts", "players", "tags", "floors", "places", "place",
            "pictures"];

    /// <summary>
    /// identity: the map's name (the title) and identity (docs/FORMAT.md "Map identity"). Only the fields given change;
    /// null clears one. <c>dossier {when, where, summary}</c>; <c>authors</c>, <c>tags</c> lists; <c>players</c> the
    /// recommended lobby size; <c>floors</c> "auto" or a list of heights; <c>places</c> replaces every place,
    /// <c>place {number, name, at, floor, delete}</c> adds or edits one; <c>pictures {menu, loading {spy, merc}, briefing,
    /// banner}</c> sets the given slots to recipes (<c>{"from":"file","file","focus"}</c>, <c>{"from":"camera",
    /// "bookmark","look","fov"}</c>, <c>{"from":"plan"}</c>) or null; <c>texts {lang: {name, description, dossier,
    /// places {number: name}} | null}</c> sets each given translation whole (null removes it; <c>texts: null</c> removes
    /// them all).
    /// </summary>
    private static void IdentityOp(ChangeContext ctx, SvJsonObject op)
    {
        ctx.SnapshotProject();
        ProjectIdentity id = ctx.Project.Identity.Clone();
        string title = ctx.Project.Title;
        if (op["name"] is { } nameNode)
        {
            title = Text(nameNode, "name", ProjectIdentity.MaxName) ?? throw new ApiException("the map needs a name", "name");
        }

        id.Family = op["family"] is { } family ? Text(family, "family", ProjectIdentity.MaxName) : id.Family;
        id.Variant = op["variant"] is { } variant ? Text(variant, "variant", ProjectIdentity.MaxName) : id.Variant;
        id.Description = op["description"] is { } description ? Text(description, "description", ProjectIdentity.MaxDescription) : id.Description;
        id.Version = op["version"] is { } version ? Text(version, "version", 32) : id.Version;
        if (op["language"] is { } language)
        {
            string lang = Text(language, "language", 3) ?? "en";
            id.Language = ProjectIdentity.IsLanguage(lang) ? lang : throw new ApiException("language is a 2 or 3 letter code (en, fr, de)", "language");
        }

        if (op["texts"] is { } texts)
        {
            SetTexts(id, texts);
        }

        // Checked when either changes, so a clash made elsewhere (map.json by hand) does not block unrelated edits.
        if ((op["language"] is not null || op["texts"] is not null) && id.Texts.ContainsKey(id.Language))
        {
            throw new ApiException($"the map's own language is '{id.Language}': its texts are the map's, not a translation", "texts");
        }

        if (op["dossier"] is { } dossier)
        {
            id.Dossier = ApiJson.IsNull(dossier) ? new Dossier() : DossierOf(dossier);
        }

        if (op["authors"] is { } authors)
        {
            id.Authors = ApiJson.IsNull(authors) ? [] : (ApiJson.StringList(authors, "authors") ?? []).Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        }

        if (op["tags"] is { } tags)
        {
            id.Tags = ApiJson.IsNull(tags) ? [] : (ApiJson.StringList(tags, "tags") ?? []).Select(t => t.Trim().ToLowerInvariant()).Distinct().ToList();
            if (id.Tags.FirstOrDefault(t => !ProjectIdentity.IsTag(t)) is { } bad)
            {
                throw new ApiException($"tag '{bad}' is not 1 to 24 lowercase letters, digits or -", "tags");
            }
        }

        if (op["players"] is { } players)
        {
            id.RecommendedPlayers = ApiJson.IsNull(players) ? null : (int)ApiJson.RequireNumber(players, "players") is var n and >= 1 and <= ProjectIdentity.MaxPlayers
                ? n
                : throw new ApiException($"players is the recommended lobby size, 1 to {ProjectIdentity.MaxPlayers}", "players");
        }

        if (op["floors"] is { } floors)
        {
            id.Floors = floors is SvJsonString { Value: "auto" } || ApiJson.IsNull(floors) ? null
                : floors is SvJsonArray list ? list.Select((z, i) => ApiJson.RequireNumber(z, $"floors[{i}]")).Order().ToList()
                : throw new ApiException("floors is \"auto\" or a list of floor heights ([] for no strategic map)", "floors");
        }

        if (op["places"] is { } places)
        {
            id.Places = ApiJson.IsNull(places) ? [] : places is SvJsonArray all
                ? all.Select((p, i) => PlaceOf(ctx, p, $"places[{i}]")).ToList()
                : throw new ApiException("places is a list of {number, name, at, floor}", "places");
        }

        if (op["place"] is SvJsonObject one)
        {
            int number = (int)ApiJson.RequireNumber(one["number"], "place.number");
            int at = id.Places.FindIndex(p => p.Number == number);
            if (ApiJson.GetBool(one, "delete", false))
            {
                id.Places.RemoveAll(p => p.Number == number);
            }
            else
            {
                Place place = PlaceOf(ctx, one, "place", at >= 0 ? id.Places[at] : null);
                if (at >= 0)
                {
                    id.Places[at] = place;
                }
                else
                {
                    id.Places.Add(place);
                }
            }
        }

        if (id.Places.GroupBy(p => p.Number).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new ApiException($"place number {twice.Key} is used twice", "places");
        }

        if (op["pictures"] is { } pictures)
        {
            SetPictures(ctx, id.Pictures, pictures);
        }

        id.Places.Sort((a, b) => a.Number.CompareTo(b.Number));
        ctx.Run(new ProjectSettingsCommand("Map identity", p =>
        {
            p.Title = title;
            p.Identity = id.Clone();
        }), touchesBrushes: false);
    }

    private static Dossier DossierOf(SvJsonNode node)
    {
        if (node is not SvJsonObject o)
        {
            throw new ApiException("dossier is {when, where, summary}", "dossier");
        }

        return new Dossier
        {
            When = Text(o["when"] ?? SvJsonNode.Null, "dossier.when", ProjectIdentity.MaxDossierLine) ?? "",
            Where = Text(o["where"] ?? SvJsonNode.Null, "dossier.where", ProjectIdentity.MaxDossierLine) ?? "",
            Summary = Text(o["summary"] ?? SvJsonNode.Null, "dossier.summary", ProjectIdentity.MaxDescription) ?? "",
        };
    }

    private static void SetTexts(ProjectIdentity id, SvJsonNode node)
    {
        if (ApiJson.IsNull(node))
        {
            id.Texts.Clear();
            return;
        }

        if (node is not SvJsonObject all)
        {
            throw new ApiException("texts is {lang: {name, description, dossier, places} | null}", "texts");
        }

        foreach ((string lang, SvJsonNode value) in all)
        {
            string field = $"texts.{lang}";
            if (!ProjectIdentity.IsLanguage(lang))
            {
                throw new ApiException($"'{lang}' is not a 2 or 3 letter language code (fr, de, es)", field);
            }

            if (ApiJson.IsNull(value))
            {
                id.Texts.Remove(lang);
                continue;
            }

            if (value is not SvJsonObject o)
            {
                throw new ApiException("a translation is {name, description, dossier, places}", field);
            }

            var text = new IdentityText
            {
                Name = Text(o["name"] ?? SvJsonNode.Null, $"{field}.name", ProjectIdentity.MaxName) ?? "",
                Description = Text(o["description"] ?? SvJsonNode.Null, $"{field}.description", ProjectIdentity.MaxDescription) ?? "",
                Dossier = o["dossier"] is { } d && !ApiJson.IsNull(d) ? DossierOf(d) : new Dossier(),
            };
            if (o["places"] is SvJsonObject places)
            {
                foreach ((string key, SvJsonNode name) in places)
                {
                    if (!int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number)
                        || number is < 1 or > 99)
                    {
                        throw new ApiException("places are keyed by place number, 1 to 99", $"{field}.places");
                    }

                    if (Text(name, $"{field}.places.{key}", ProjectIdentity.MaxName) is { } placeName)
                    {
                        text.Places[number] = placeName;
                    }
                }
            }
            else if (o["places"] is { } other && !ApiJson.IsNull(other))
            {
                throw new ApiException("places is {number: name}", $"{field}.places");
            }

            if (text.IsEmpty)
            {
                id.Texts.Remove(lang);
            }
            else
            {
                id.Texts[lang] = text;
            }
        }
    }

    private static Place PlaceOf(ChangeContext ctx, SvJsonNode node, string field, Place? existing = null)
    {
        if (node is not SvJsonObject o)
        {
            throw new ApiException("a place is {number, name, at, floor}", field);
        }

        int number = existing is not null && o["number"] is null ? existing.Number : (int)ApiJson.RequireNumber(o["number"], $"{field}.number");
        if (number is < 1 or > 99)
        {
            throw new ApiException("place numbers are 1 to 99", $"{field}.number");
        }

        return new Place
        {
            Number = number,
            Name = o["name"] is { } name ? Text(name, $"{field}.name", ProjectIdentity.MaxName) ?? "" : existing?.Name ?? "",
            At = o["at"] is { } at ? (ApiJson.IsNull(at) ? null : ctx.Point(at, $"{field}.at")) : existing?.At,
            Floor = o["floor"] is { } floor ? (ApiJson.IsNull(floor) ? null : (int)ApiJson.RequireNumber(floor, $"{field}.floor")) : existing?.Floor,
        };
    }

    private static void SetPictures(ChangeContext ctx, IdentityPictures slots, SvJsonNode node)
    {
        if (node is not SvJsonObject o)
        {
            throw new ApiException("pictures is {menu, loading: {spy, merc}, briefing, banner}", "pictures");
        }

        PictureRecipe? One(string key) => o[key] is { } r && !ApiJson.IsNull(r) ? Recipe(ctx, r, $"pictures.{key}") : null;
        slots.Menu = o.ContainsKey("menu") ? One("menu") : slots.Menu;
        slots.Briefing = o.ContainsKey("briefing") ? One("briefing") : slots.Briefing;
        slots.Banner = o.ContainsKey("banner") ? One("banner") : slots.Banner;
        if (slots.Banner is { From: PictureSource.Plan })
        {
            throw new ApiException("the banner is 16:9 and the plan is square: give the banner a file or a camera", "pictures.banner");
        }

        if (o["loading"] is SvJsonObject loading)
        {
            List<PictureRecipe> Many(string side) => loading[side] is SvJsonArray list
                ? list.Select((r, i) => Recipe(ctx, r, $"pictures.loading.{side}[{i}]")).Take(ProjectIdentity.MaxPictures).ToList()
                : [];
            slots.LoadingSpy = loading.ContainsKey("spy") ? Many("spy") : slots.LoadingSpy;
            slots.LoadingMerc = loading.ContainsKey("merc") ? Many("merc") : slots.LoadingMerc;
        }
    }

    private static PictureRecipe Recipe(ChangeContext ctx, SvJsonNode node, string field)
    {
        PictureRecipe r = IdentityJson.RecipeFromJson(node)
            ?? throw new ApiException("a picture is {from: file|camera|plan, ...}", field);
        if (r.From == PictureSource.File && string.IsNullOrEmpty(r.File))
        {
            throw new ApiException("a file picture needs file (pictures/x.png in the project, or a full path)", field);
        }

        if (r.From == PictureSource.Camera && !ctx.Project.Workspace.Bookmarks.Any(b => b.Name == r.Bookmark))
        {
            throw new ApiException($"there is no bookmark named '{r.Bookmark}' (add one with the bookmark op)", field);
        }

        if (node is SvJsonObject o && ApiJson.GetString(o, "look") is { } look && !PictureRecipe.Looks.Contains(look))
        {
            throw new ApiException($"look is one of {string.Join(", ", PictureRecipe.Looks)}", field);
        }

        return r;
    }

    /// <summary>A trimmed string up to <paramref name="max"/> characters, or null for null or empty.</summary>
    private static string? Text(SvJsonNode node, string field, int max)
    {
        if (ApiJson.IsNull(node))
        {
            return null;
        }

        string text = node is SvJsonString s ? s.Value.Trim() : throw new ApiException($"{field} is text", field);
        return text.Length <= max ? (text.Length == 0 ? null : text) : throw new ApiException($"{field} is at most {max} characters", field);
    }
}
