using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Validation.Rules;

/// <summary>The map's identity: its name, pictures, recommended players, places and floors, and, for the original
/// game, text its files can hold.</summary>
internal sealed class IdentityRules : MapRule
{
    public const string Name = "identity.name";
    public const string NameLong = "identity.name-long";
    public const string PicturesMissing = "identity.pictures-missing";
    public const string Picture = "identity.picture";
    public const string PictureRender = "identity.picture-render";
    public const string Players = "identity.players";
    public const string Places = "identity.places";
    public const string Floors = "identity.floors";
    public const string Text = "identity.text";
    public const string Translation = "identity.translation";
    public const string TranslationLanguage = "identity.translation-language";

    /// <summary>The original's lists clip longer names (OpenSCCT wraps them).</summary>
    public const int ShortName = 32;

    /// <summary>The characters of Windows-1252 between 0x80 and 0x9F (the rest of it is Latin-1).</summary>
    private const string Cp1252Extras = "€‚ƒ„…†‡ˆ‰Š‹ŒŽ‘’“”•–—˜™š›œžŸ";

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(Name, Severity.Warning, $"The map has no name (menus show its map code), or one longer than {ProjectIdentity.MaxName} characters (menus cut it)"),
        new(NameLong, Severity.Warning, $"The name is longer than {ShortName} characters (the original's lists clip it)"),
        new(PicturesMissing, Severity.Warning, "No menu picture, or no loading picture for a side"),
        new(Picture, Severity.Warning, "A picture file is missing, not the slot's shape (square, 16:9 for the banner), smaller than 512 or larger "
            + "than 2048 pixels, or names a missing bookmark; the banner uses the square plan"),
        new(PictureRender, Severity.Warning, "A camera picture has never been rendered (ose mapshot, or Render now in the Identity panel)"),
        new(Players, Severity.Info, "More players recommended than the map has team starts (players share starts, so spawns stack)"),
        new(Places, Severity.Warning, "A place number used twice or outside 1..99, or on a floor the floor list does not have; a place with no "
            + "position is noted"),
        new(Floors, Severity.Warning, "The strategic map floor heights do not rise"),
        new(Translation, Severity.Warning, "A translation in the map's own language, one that names a place the map does not have (both "
            + "left out of the build), or a translated text longer than the same text may be (the build cuts it)"),
        new(TranslationLanguage, Severity.Warning, "A translation into a language OpenSCCT does not show: it reads "
            + string.Join(", ", ProjectIdentity.GameLanguages) + " (two letter codes)"),
        new(Text, Severity.Error, "A name, dossier or place text the original game's files cannot hold (outside Windows-1252, or a double quote)")
        {
            OriginalOnly = true,
        },
    ];

    public override void Run(CheckContext c)
    {
        MapProject p = c.Project;
        ProjectIdentity id = p.Identity;
        string name = p.Title.Trim();
        if (name.Length is 0 or > ProjectIdentity.MaxName)
        {
            c.Report(Severity.Warning, Name, name.Length == 0 ? $"The map has no name: menus show its map code, {p.Id}"
                : $"The name has {name.Length} characters: menus show the first {ProjectIdentity.MaxName}", null);
        }
        else if (name.Length > ShortName)
        {
            c.Report(Severity.Warning, NameLong, $"The name has {name.Length} characters; the original's map lists show about {ShortName}", null);
        }

        CheckPictures(c, id.Pictures);
        int starts = StartRules.TeamStarts(c, 0).Count() + StartRules.TeamStarts(c, 1).Count();
        if (id.RecommendedPlayers is { } players && starts > 0 && players > starts)
        {
            c.Report(Severity.Info, Players, $"{players} players recommended and {starts} team starts: players will share starts", null);
        }

        foreach (IGrouping<int, Place> same in id.Places.GroupBy(pl => pl.Number).Where(g => g.Count() > 1))
        {
            c.Report(Severity.Warning, Places, $"Place number {same.Key} is used {same.Count()} times", null);
        }

        foreach (Place place in id.Places)
        {
            if (place.Number is < 1 or > 99)
            {
                c.Report(Severity.Warning, Places, $"Place {place.Name} has number {place.Number}; numbers are 1 to 99", place.At);
            }
            else if (place.At is null)
            {
                c.Report(Severity.Info, Places, $"Place {place.Number} ({place.Name}) has no position: it is listed in the legend but not drawn", null);
            }
            else if (id.Floors is { } floors && place.Floor is { } floor && floor >= floors.Count)
            {
                c.Report(Severity.Warning, Places, $"Place {place.Number} is on floor {floor}; the map has {floors.Count}", place.At);
            }
        }

        if (id.Floors is { Count: > 1 } list && list.Zip(list.Skip(1)).Any(z => z.Second <= z.First))
        {
            c.Report(Severity.Warning, Floors, "The strategic map floor heights must rise", null);
        }

        foreach ((string lang, IdentityText text) in id.Texts)
        {
            if (lang == id.Language)
            {
                c.Report(Severity.Warning, Translation, $"The '{lang}' translation is in the map's own language: the build leaves it out", null);
            }

            else if (!ProjectIdentity.GameLanguages.Contains(lang))
            {
                c.Report(Severity.Warning, TranslationLanguage, $"OpenSCCT does not show '{lang}' translations: it reads "
                    + string.Join(", ", ProjectIdentity.GameLanguages) + (lang.Length == 3 ? " (two letter codes, fr rather than fra)" : ""), null);
            }

            foreach (int number in text.Places.Keys.Where(n => id.Places.All(pl => pl.Number != n)))
            {
                c.Report(Severity.Warning, Translation, $"The '{lang}' translation names place {number}, which the map does not have", null);
            }

            foreach ((string what, string value, int max) in text.Fields().Where(t => t.Value.Trim().Length > t.Max))
            {
                c.Report(Severity.Warning, Translation, $"The '{lang}' {what} has {value.Trim().Length} characters: the build keeps the first {max}", null);
            }
        }

        if (c.Original)
        {
            IEnumerable<(string What, string Value)> texts = new[] { ("name", name), ("dossier", Compile.SideFiles.LegendOf(id.Dossier)) }
                .Concat(id.Places.Select(pl => ($"place {pl.Number}", pl.Name)));
            foreach ((string what, string value) in texts)
            {
                if (value.FirstOrDefault(ch => ch == '"' || !IsCp1252(ch)) is var bad and not '\0')
                {
                    c.Report(Severity.Error, Text, $"The {what} has '{bad}', which the original game's files cannot hold", null);
                }
            }
        }
    }

    private static void CheckPictures(CheckContext c, IdentityPictures pictures)
    {
        if (pictures.Menu is null)
        {
            c.Report(Severity.Warning, PicturesMissing, "The map has no menu picture (map lists and the lobby briefing show it)", null);
        }

        foreach ((string side, List<PictureRecipe> list) in new[] { ("spies", pictures.LoadingSpy), ("mercs", pictures.LoadingMerc) })
        {
            if (list.Count == 0)
            {
                c.Report(Severity.Warning, PicturesMissing, $"The map has no loading picture for the {side}", null);
            }
        }

        foreach ((string slot, PictureRecipe recipe) in pictures.Slots())
        {
            if (recipe.From == PictureSource.Camera)
            {
                CheckCamera(c, slot, recipe);
            }
            else if (recipe.From == PictureSource.Plan && Compile.IdentityBuild.AspectOf(slot) != 1)
            {
                c.Report(Severity.Warning, Picture, $"Picture {slot} is 16:9 and the plan is square: the map goes without it", null);
            }
            else if (recipe.From == PictureSource.File && recipe.File is { } file && ProjectPictures.Resolve(c.Options.ProjectFolder, file) is { } path)
            {
                if (!File.Exists(path))
                {
                    c.Report(Severity.Warning, Picture, $"Picture {slot}: {file} is missing", null);
                }
                else if (FileProblem(path, slot, Interop.Svmap.IdentityImport.CameWithMap(c.Project, path)) is { } problem)
                {
                    c.Report(Severity.Warning, Picture, $"Picture {slot}: {problem}", null);
                }
            }
        }
    }

    private static void CheckCamera(CheckContext c, string slot, PictureRecipe recipe)
    {
        if (c.Project.Workspace.Bookmarks.FirstOrDefault(b => b.Name == recipe.Bookmark) is not { } bookmark)
        {
            c.Report(Severity.Warning, Picture, $"Picture {slot} is taken from bookmark '{recipe.Bookmark}', which does not exist", null);
            return;
        }

        (int width, int height) = MapShots.SizeOf(slot);
        if (c.Options.ProjectFolder is { } folder && MapShots.StateOf(folder, MapShots.Key(recipe, bookmark, width, height), mapKey: null) == ShotState.Missing)
        {
            c.Report(Severity.Warning, PictureRender, $"Picture {slot} from bookmark '{recipe.Bookmark}' has never been rendered: the map goes "
                + "without it until ose mapshot (or Render now in the Identity panel) renders it", null);
        }
    }

    /// <summary>What the compile will do about a picture file, when it changes it or cannot use it. A picture the map came
    /// with (<paramref name="original"/>) is not asked to be bigger or another shape: the original game shows it so.</summary>
    private static string? FileProblem(string path, string slot, bool original)
    {
        var info = new FileInfo(path);
        double aspect = Compile.IdentityBuild.AspectOf(slot);
        string shape = aspect == 1 ? "square" : "16:9";
        bool Fits(int w, int h) => Math.Abs(w - h * aspect) <= 1;
        string? Small(int w, int h) => slot != "banner" && Math.Min(w, h) < Compile.IdentityBuild.MinPictureSide
            ? $"{info.Name} is {w}x{h}; menus ask for at least {Compile.IdentityBuild.MinPictureSide} pixels (1024 looks best)"
            : null;
        byte[] head = new byte[Math.Min(256 * 1024, info.Length)];
        using (FileStream f = info.OpenRead())
        {
            f.ReadExactly(head);
        }

        // A JPEG's size can sit past its first segments (a large EXIF thumbnail): read the whole file then.
        (int Width, int Height)? size = RgbaImage.SizeOf(head) ?? (RgbaImage.FormatOf(head) is not null ? RgbaImage.SizeOf(File.ReadAllBytes(path)) : null);
        if (size is not { } s)
        {
            return $"{info.Name} is not a {RgbaImage.Formats} file";
        }

        if (original)
        {
            return null;
        }

        return !Fits(s.Width, s.Height) ? $"{info.Name} is {s.Width}x{s.Height}; it is cropped to {shape} around its focal point"
            : Math.Max(s.Width, s.Height) > Compile.IdentityBuild.MaxPictureSide
                ? $"{info.Name} is {s.Width}x{s.Height}; it is scaled to fit {Compile.IdentityBuild.MaxPictureSide} pixels"
            : Small(s.Width, s.Height);
    }

    private static bool IsCp1252(char ch) => ch is (>= ' ' and <= '~') or (>= '\u00A0' and <= '\u00FF') || Cp1252Extras.Contains(ch);
}
