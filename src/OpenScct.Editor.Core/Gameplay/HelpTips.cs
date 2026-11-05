using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>A zone tip (SZoneTips, or SEndZoneTip which also ends the game): a cylinder that shows a hint on the HUD
/// while the player stands in it, in offline play.</summary>
public sealed record ZoneTip(string Id, Vec3 Center, double Radius, double Height, string Spy, string Merc,
    bool OnlyWithHelp, bool StayPermanently, bool EndsGame, int EndGameDelay);

/// <summary>A help path marker (SHelpPath): a static mesh shown only with the profile's in-game help on.</summary>
public sealed record HelpMarker(string Id, Vec3 Location, string Mesh);

/// <summary>
/// Help paths and zone tips (spec/devices/help-path.md, zone-tips.md). The stock maps place them in pairs: an "info"
/// marker on the floor and a tip cylinder about 100 units above it, both shown only with in-game help (Visit a Map).
/// </summary>
public static class HelpTips
{
    public const string TipClass = "SBase.SZoneTips";
    public const string EndTipClass = "SBase.SEndZoneTip";
    public const string MarkerClass = "SBase.SHelpPath";

    /// <summary>How far above its marker the stock maps put a tip (SteeE: marker at -167, tip at -67).</summary>
    public const double TipAboveMarker = 100;

    public static bool IsTip(Actor a) => ClassDefaults.IsA(a, TipClass);

    public static bool IsMarker(Actor a) => ClassDefaults.IsA(a, MarkerClass);

    public static ZoneTip Tip(Actor a)
    {
        bool ends = ClassDefaults.IsA(a, EndTipClass);
        return new ZoneTip(a.Id, a.Location, ClassDefaults.Number(a, "CollisionRadius", 300), ClassDefaults.Number(a, "CollisionHeight", 150),
            Text(a, "sMessage_ATT"), Text(a, "sMessage_DEF"), a.Props.GetBool("bShowOnlyWithIngameHelp"),
            !a.Props.TryGet("bStayPermanently", out _) || a.Props.GetBool("bStayPermanently"),
            ends, ends ? (int)ClassDefaults.Number(a, "EndGameDelay", 5) : 0);
    }

    public static List<ZoneTip> Tips(MapProject p) => p.Actors.Where(IsTip).Select(Tip).ToList();

    public static List<HelpMarker> Markers(MapProject p) =>
        p.Actors.Where(IsMarker).Select(a => new HelpMarker(a.Id, a.Location, MeshOf(a) ?? "")).ToList();

    /// <summary>The help marker meshes the map already uses, most used first (every map has its own copies).</summary>
    public static List<string> MarkerMeshes(MapProject p) =>
        p.Actors.Where(IsMarker).Select(MeshOf).OfType<string>()
            .GroupBy(m => m, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();

    /// <summary>The stock info marker mesh the stock maps share by name (each map ships its own copy).</summary>
    public const string StockMarkerName = "help_path_INFO";

    /// <summary>The mesh a new info marker uses: the map's most used marker mesh, else a stock <c>help_path_INFO</c>
    /// from the content (a new map has no markers of its own). Null when neither is there.</summary>
    public static string? DefaultMarkerMesh(MapProject p, Func<AssetCatalog> catalog) =>
        MarkerMeshes(p).FirstOrDefault()
        ?? catalog().Search(StockMarkerName, AssetKind.Mesh).FirstOrDefault(e => e.Name.Equals(StockMarkerName, StringComparison.OrdinalIgnoreCase))?.Path;

    /// <summary>
    /// Places a tip, one undo step: the cylinder at <paramref name="at"/> and, with <paramref name="marker"/>, an info
    /// marker <see cref="TipAboveMarker"/> below it using <paramref name="mesh"/> (default: the map's most used marker
    /// mesh; see <see cref="DefaultMarkerMesh"/> for maps without one). Tips are set like the stock ones: only with
    /// in-game help, cleared on leaving.
    /// </summary>
    public static AddObjectsCommand Place(MapProject p, Vec3 at, string spy, string? merc = null, double radius = 150, double height = 100,
        bool marker = true, string? mesh = null, bool endsGame = false)
    {
        if (radius <= 0 || height <= 0)
        {
            throw new ArgumentException("radius and height must be more than 0");
        }

        Func<string, string> ids = GeShapes.IdSource(p);
        string cls = endsGame ? EndTipClass : TipClass;
        var tip = new Actor(ids(cls[(cls.IndexOf('.') + 1)..]), cls) { Location = at };
        tip.Props.Set("CollisionRadius", SvJsonNode.Float((float)radius));
        tip.Props.Set("CollisionHeight", SvJsonNode.Float((float)height));
        tip.Props.Set("bShowOnlyWithIngameHelp", SvJsonNode.Bool(true));
        tip.Props.Set("bStayPermanently", SvJsonNode.Bool(false));
        SetText(tip, "sMessage_ATT", spy);
        SetText(tip, "sMessage_DEF", merc ?? spy);
        var actors = new List<Actor> { tip };
        if (marker)
        {
            string meshPath = mesh ?? MarkerMeshes(p).FirstOrDefault()
                ?? throw new ArgumentException($"this map has no help marker to take the mesh from and no stock {StockMarkerName} was found: give the marker's static mesh");
            var m = new Actor(ids("SHelpPath"), MarkerClass) { Location = at - new Vec3(0, 0, TipAboveMarker) };
            m.Props.Set("DrawType", SvJsonNode.Integer(8));
            m.Props.Set("StaticMesh", Asset(meshPath));
            m.Props.Set("bUnlit", SvJsonNode.Bool(true));
            m.Props.Set("bUseDynamicLights", SvJsonNode.Bool(false));
            actors.Add(m);
        }

        return new AddObjectsCommand(marker ? $"Add {tip.Id} with help marker" : $"Add {tip.Id}", actors: actors);
    }

    /// <summary>Changes a tip, one undo step; null leaves a value as it is.</summary>
    public static ReplaceObjectsCommand Set(MapProject p, string id, string? spy = null, string? merc = null, double? radius = null, double? height = null,
        bool? onlyWithHelp = null, bool? stayPermanently = null, int? endGameDelay = null)
    {
        Actor tip = p.FindActor(id) is { } a && IsTip(a) ? a : throw new ArgumentException($"'{id}' is not a zone tip");
        if (radius <= 0 || height <= 0)
        {
            throw new ArgumentException("radius and height must be more than 0");
        }

        if (endGameDelay is not null && !ClassDefaults.IsA(tip, EndTipClass))
        {
            throw new ArgumentException($"{id} does not end the game (only SEndZoneTip has a delay)");
        }

        return ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(id)], $"Edit tip {id}", actor: t =>
        {
            if (spy is not null)
            {
                SetText(t, "sMessage_ATT", spy);
            }

            if (merc is not null)
            {
                SetText(t, "sMessage_DEF", merc);
            }

            if (radius is { } r)
            {
                t.Props.Set("CollisionRadius", SvJsonNode.Float((float)r));
            }

            if (height is { } h)
            {
                t.Props.Set("CollisionHeight", SvJsonNode.Float((float)h));
            }

            if (onlyWithHelp is { } help)
            {
                t.Props.Set("bShowOnlyWithIngameHelp", SvJsonNode.Bool(help));
            }

            if (stayPermanently is { } stay)
            {
                t.Props.Set("bStayPermanently", SvJsonNode.Bool(stay));
            }

            if (endGameDelay is { } delay)
            {
                t.Props.Set("EndGameDelay", SvJsonNode.Integer(Math.Max(0, delay)));
            }
        });
    }

    /// <summary>The text the HUD shows: the two parts joined by a space (rule 2).</summary>
    public static string Text(Actor a, string prop)
    {
        string[] parts = [Part(a.Props[prop], 0), Part(a.Props[prop], 1)];
        return string.Join(" ", parts.Where(s => s.Length > 0));
    }

    private static string Part(SvJsonNode? node, int i) =>
        node is SvJsonObject o && o["$elements"] is SvJsonObject e && e[i.ToString(CultureInfo.InvariantCulture)] is SvJsonString s ? s.Value : "";

    /// <summary>Stores the whole text in the first part: the game joins both with a space, so one part shows the same.</summary>
    private static void SetText(Actor a, string prop, string text)
    {
        if (text.Length == 0)
        {
            a.Props.Remove(prop);
            return;
        }

        var elements = new SvJsonObject();
        elements.Add("0", SvJsonNode.String(text));
        var o = new SvJsonObject();
        o.Add("$elements", elements);
        a.Props.Set(prop, o);
    }

    private static string? MeshOf(Actor a) => a.Props["StaticMesh"] is SvJsonObject o && o["$asset"] is SvJsonString s ? s.Value : null;

    private static SvJsonObject Asset(string path)
    {
        var o = new SvJsonObject();
        o.Add("$asset", SvJsonNode.String(path));
        return o;
    }
}
