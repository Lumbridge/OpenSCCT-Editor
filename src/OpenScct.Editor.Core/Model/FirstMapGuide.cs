using System.Security.Cryptography;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Geometry;

namespace OpenScct.Editor.Core.Model;

/// <summary>Editor-only learning state. Content-dependent steps are derived afresh, never latched.</summary>
public sealed class FirstMapGuide
{
    public bool Hidden { get; set; }
    public bool Explored { get; set; }
    public string? CheckedRevision { get; set; }
    public string? PlayedRevision { get; set; }
    public FirstMapGuide Clone() => (FirstMapGuide)MemberwiseClone();

    /// <summary>Changes to geometry, gameplay or build settings invalidate check/play; camera and guide changes do not.</summary>
    public static string Revision(MapProject p)
    {
        var doc = new SvJsonObject();
        doc.Add("map", ProjectStore.MapToJson(p));
        doc.Add("brushes", new SvJsonArray(p.Brushes.Select(b => ModelJson.BrushToJson(b, includeEditor: false))));
        doc.Add("actors", new SvJsonArray(p.Actors.Select(a => ModelJson.ActorToJson(a, includeEditor: false))));
        doc.Add("ges", new SvJsonArray(p.Ges.Select(g => ModelJson.GeToJson(g, includeEditor: false))));
        SvJsonObject workspace = ProjectStore.WorkspaceToJson(p.Workspace);
        doc.Add("movement", workspace["movement"]!);
        doc.Add("play", workspace["play"]!);
        doc.Add("outfits", OutfitsJson.ToJson(p.Outfits));
        return Convert.ToHexString(SHA256.HashData(CanonicalJson.ToFileBytes(doc)));
    }

    public static bool[] Steps(MapProject p, bool saved, string revision) =>
    [
        saved && !string.IsNullOrWhiteSpace(p.Title) && p.Title != "New map",
        p.Workspace.FirstMap.Explored && p.Brushes.Any(b => b.Oper == CsgOper.Subtract),
        HasConnectedRooms(p),
        p.Actors.Any(a => IsStart(a) && Team(a) == 0) && p.Actors.Any(a => IsStart(a) && Team(a) == 1),
        p.Actors.Any(a => Short(a.Class) == "SObjective" && a.Props["Triggers"] is SvJsonArray triggers
            && triggers.Any(t => p.Actors.Any(c => c.Id == PropValues.ActorId(t) && Short(c.Class) == "SComputerObjectiveTrigger"))),
        p.Workspace.FirstMap.CheckedRevision == revision,
        p.Workspace.FirstMap.PlayedRevision == revision,
    ];

    public static bool IsStart(Actor a) => Short(a.Class) == "PlayerStart";
    private static int Team(Actor a) => (int)ModelJson.Num(a.Props["TeamNumber"]);
    private static string Short(string cls) => cls[(cls.LastIndexOf('.') + 1)..];

    // Two room-sized carved spaces joined by an overlapping, player-sized carved opening.
    // Map Check remains the authority for obstructed or otherwise unplayable geometry.
    public static bool HasConnectedRooms(MapProject p)
    {
        var boxes = p.Brushes.Where(b => b.Oper == CsgOper.Subtract)
            .Select(b => ShapeCsg.World(b).Bounds()).ToList();
        var rooms = boxes.Where(b => b.Max.X - b.Min.X >= 512 && b.Max.Y - b.Min.Y >= 512).ToList();
        foreach (var c in boxes)
        {
            if (rooms.Where(a => Joins(a, c, p.Workspace.Movement.StandHeight, Math.Max(72, p.Workspace.Movement.Radius * 2))).Distinct().Take(2).Count() == 2) return true;
        }
        return false;
    }

    private static bool Joins((Vec3 Min, Vec3 Max) a, (Vec3 Min, Vec3 Max) b, double height, double width) =>
        Math.Min(a.Max.X, b.Max.X) - Math.Max(a.Min.X, b.Min.X) >= 32 &&
        Math.Min(a.Max.Y, b.Max.Y) - Math.Max(a.Min.Y, b.Min.Y) >= width &&
        Math.Min(a.Max.Z, b.Max.Z) - Math.Max(a.Min.Z, b.Min.Z) >= height;
}
