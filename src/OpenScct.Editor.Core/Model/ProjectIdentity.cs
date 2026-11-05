namespace OpenScct.Editor.Core.Model;

/// <summary>When and where the map is set and one line about it: the map's dossier, which the original shows as
/// <c>SMapSettings.LegendMap</c> ("when|where||summary").</summary>
public sealed class Dossier
{
    public string When { get; set; } = "";

    public string Where { get; set; } = "";

    public string Summary { get; set; } = "";

    public bool IsEmpty => When.Length == 0 && Where.Length == 0 && Summary.Length == 0;

    public Dossier Clone() => (Dossier)MemberwiseClone();
}

/// <summary>A numbered area of the briefing plan and the strategic map ("3. Ghost Train").</summary>
public sealed class Place
{
    public int Number { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Where the number is drawn, or null for a place known only by name (imported from a legend).</summary>
    public Vec3? At { get; set; }

    /// <summary>The strategic map floor the number is drawn on, or null for the floor of <see cref="At"/>'s height.</summary>
    public int? Floor { get; set; }

    public Place Clone() => (Place)MemberwiseClone();
}

/// <summary>
/// The map's texts in another language (SVMap 1.2 <c>ui/text/&lt;lang&gt;.json</c>): its name, description, dossier and
/// place names. An empty text falls back to the map's own, so a translation may cover only part of the map.
/// </summary>
public sealed class IdentityText
{
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public Dossier Dossier { get; set; } = new();

    /// <summary>Place names by place number.</summary>
    public SortedDictionary<int, string> Places { get; set; } = new();

    public bool IsEmpty => Name.Length == 0 && Description.Length == 0 && Dossier.IsEmpty && Places.Values.All(n => n.Length == 0);

    /// <summary>Each text with what it is and its limit, the same as the map's own text has.</summary>
    public IEnumerable<(string What, string Value, int Max)> Fields() =>
        new[]
        {
            ("name", Name, ProjectIdentity.MaxName), ("description", Description, ProjectIdentity.MaxDescription),
            ("dossier when", Dossier.When, ProjectIdentity.MaxDossierLine), ("dossier where", Dossier.Where, ProjectIdentity.MaxDossierLine),
            ("dossier summary", Dossier.Summary, ProjectIdentity.MaxDescription),
        }.Concat(Places.Select(p => ($"place {p.Key}", p.Value, ProjectIdentity.MaxName)));

    public IdentityText Clone() => new() { Name = Name, Description = Description, Dossier = Dossier.Clone(), Places = new(Places) };
}

/// <summary>How a map picture is made.</summary>
public enum PictureSource
{
    /// <summary>An image file, cropped at its focal point to the slot's shape (square, 16:9 for the banner).</summary>
    File,

    /// <summary>A workspace bookmark rendered by OpenSCCT's renderer (<c>ose mapshot</c>), with a look.</summary>
    Camera,

    /// <summary>The generated plan of the map, drawn through the strategic map camera.</summary>
    Plan,
}

/// <summary>A picture recipe: what the compile turns into one file of <c>ui/pictures</c>.</summary>
public sealed class PictureRecipe
{
    public const string PlainLook = "plain", NightVisionLook = "nightvision", ThermalLook = "thermal";

    public static readonly IReadOnlyList<string> Looks = [PlainLook, NightVisionLook, ThermalLook];

    /// <summary>The camera field of view a picture may use, in degrees.</summary>
    public const double MinFov = 10, MaxFov = 120;

    public PictureSource From { get; set; }

    /// <summary>File: the image, relative to the project folder (<c>pictures/menu.png</c>) or absolute.</summary>
    public string? File { get; set; }

    /// <summary>File: the point kept in the middle of the crop, 0..1 across.</summary>
    public double FocusX { get; set; } = 0.5;

    /// <summary>File: the point kept in the middle of the crop, 0..1 down.</summary>
    public double FocusY { get; set; } = 0.5;

    /// <summary>Camera: the workspace bookmark's name.</summary>
    public string? Bookmark { get; set; }

    /// <summary>Camera: <c>plain</c>, <c>nightvision</c> or <c>thermal</c>.</summary>
    public string Look { get; set; } = PlainLook;

    /// <summary>Camera: the horizontal field of view in degrees.</summary>
    public double Fov { get; set; } = 80;

    public static PictureRecipe OfFile(string file, double focusX = 0.5, double focusY = 0.5) =>
        new() { From = PictureSource.File, File = file, FocusX = focusX, FocusY = focusY };

    public static PictureRecipe OfCamera(string bookmark, string look = PlainLook, double fov = 80) =>
        new() { From = PictureSource.Camera, Bookmark = bookmark, Look = look, Fov = fov };

    public static PictureRecipe OfPlan() => new() { From = PictureSource.Plan };

    public PictureRecipe Clone() => (PictureRecipe)MemberwiseClone();
}

/// <summary>The map's picture slots. The game shows the menu picture in map lists and on the lobby briefing's first
/// page, a loading picture of the player's side while the map loads, and the briefing plan on the briefing's second
/// page.</summary>
public sealed class IdentityPictures
{
    public PictureRecipe? Menu { get; set; }

    public List<PictureRecipe> LoadingSpy { get; set; } = new();

    public List<PictureRecipe> LoadingMerc { get; set; } = new();

    public PictureRecipe? Briefing { get; set; }

    public PictureRecipe? Banner { get; set; }

    /// <summary>Every slot with its recipe, in a fixed order: menu, loading-spy-1.., loading-merc-1.., briefing, banner.</summary>
    public IEnumerable<(string Slot, PictureRecipe Recipe)> Slots()
    {
        if (Menu is not null)
        {
            yield return ("menu", Menu);
        }

        for (int i = 0; i < LoadingSpy.Count; i++)
        {
            yield return ($"loading-spy-{i + 1}", LoadingSpy[i]);
        }

        for (int i = 0; i < LoadingMerc.Count; i++)
        {
            yield return ($"loading-merc-{i + 1}", LoadingMerc[i]);
        }

        if (Briefing is not null)
        {
            yield return ("briefing", Briefing);
        }

        if (Banner is not null)
        {
            yield return ("banner", Banner);
        }
    }

    public IdentityPictures Clone() => new()
    {
        Menu = Menu?.Clone(),
        LoadingSpy = LoadingSpy.Select(r => r.Clone()).ToList(),
        LoadingMerc = LoadingMerc.Select(r => r.Clone()).ToList(),
        Briefing = Briefing?.Clone(),
        Banner = Banner?.Clone(),
    };
}

/// <summary>
/// What the map says about itself for the menus (SVMap 1.2 <c>ui/identity.json</c>): its name is the project
/// <see cref="MapProject.Title"/>; the rest lives here. Playlists are not part of a map, and the player range a map
/// supports is gameplay (its match-size rules), so only the recommended lobby size is kept.
/// </summary>
public sealed class ProjectIdentity
{
    /// <summary>Limits of the SVMap 1.2 identity schema.</summary>
    public const int MaxName = 48, MaxDescription = 300, MaxDossierLine = 64, MaxPlayers = 24, MaxTags = 32, MaxPictures = 8;

    /// <summary>The languages OpenSCCT shows a map's translation in: those its content can be built in (English, French,
    /// German, Spanish, Italian). The format allows any language; the game reads no other yet.</summary>
    public static readonly IReadOnlyList<string> GameLanguages = ["en", "fr", "de", "es", "it"];

    /// <summary>Tags OpenSCCT and the content index know (any lowercase tag is allowed).</summary>
    public static readonly IReadOnlyList<string> KnownTags = ["3v3", "1v1", "competitive", "casual", "small", "large"];

    /// <summary>The name without its variant word ("Aquarius"); null: the whole name.</summary>
    public string? Family { get; set; }

    /// <summary>The variant word ("Enhanced"), or null.</summary>
    public string? Variant { get; set; }

    /// <summary>One or two sentences for map browsers; null: the dossier summary.</summary>
    public string? Description { get; set; }

    public Dossier Dossier { get; set; } = new();

    public List<string> Authors { get; set; } = new();

    public string? Version { get; set; }

    /// <summary>The language of the map's texts (identity, map info, actor strings).</summary>
    public string Language { get; set; } = "en";

    /// <summary>Translations of the map's texts by language (BCP 47 primary tag, never <see cref="Language"/>).</summary>
    public SortedDictionary<string, IdentityText> Texts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The lobby size the map plays best at (1..24), or null.</summary>
    public int? RecommendedPlayers { get; set; }

    public List<string> Tags { get; set; } = new();

    public List<Place> Places { get; set; } = new();

    public IdentityPictures Pictures { get; set; } = new();

    /// <summary>The strategic map floors: null finds them from the compiled collision (unless the LevelInfo already has
    /// <c>MapFloors</c>, as imported maps do); a list gives each floor's lowest height; an empty list: no strategic map.</summary>
    public List<double>? Floors { get; set; }

    public static bool IsTag(string tag) =>
        tag.Length is >= 1 and <= 24 && tag.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    public static bool IsLanguage(string lang) => lang.Length is 2 or 3 && lang.All(c => c is >= 'a' and <= 'z');

    public ProjectIdentity Clone() => new()
    {
        Family = Family,
        Variant = Variant,
        Description = Description,
        Dossier = Dossier.Clone(),
        Authors = Authors.ToList(),
        Version = Version,
        Language = Language,
        Texts = new(Texts.ToDictionary(t => t.Key, t => t.Value.Clone()), StringComparer.Ordinal),
        RecommendedPlayers = RecommendedPlayers,
        Tags = Tags.ToList(),
        Places = Places.Select(p => p.Clone()).ToList(),
        Pictures = Pictures.Clone(),
        Floors = Floors?.ToList(),
    };
}
