using System.Security.Cryptography;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Geometry;

namespace OpenScct.Editor.Core.Model;

/// <summary>Editor-only learning state. Content-dependent steps are derived afresh, never latched.</summary>
public sealed class FirstMapGuide
{
    public bool Hidden { get; set; } = true;
    public bool Explored { get; set; }
    public string? CheckedRevision { get; set; }
    public string? PlayedRevision { get; set; }
    public string[]? ConstructionBaseline { get; set; }
    public FirstMapGuide Clone()
    {
        var copy = (FirstMapGuide)MemberwiseClone();
        copy.ConstructionBaseline = ConstructionBaseline?.ToArray();
        return copy;
    }

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
        HasConnectedRooms(p) && HasLessonPiece(p, "doorway") && HasLessonPiece(p, "corridor") && HasLessonPiece(p, "room"),
        p.Actors.Any(a => IsStart(a) && Team(a) == 0) && p.Actors.Any(a => IsStart(a) && Team(a) == 1),
        HasPlayableComputer(p),
        p.Workspace.FirstMap.CheckedRevision == revision,
        p.Workspace.FirstMap.PlayedRevision == revision,
    ];

    public static bool IsStart(Actor a) => Short(a.Class) == "PlayerStart";
    public static bool HasPiece(MapProject p, string kind) => p.Brushes.Any(b => b.Shape.Builder?.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) == true);
    public static bool HasLessonPiece(MapProject p, string kind) => p.Brushes.Any(b =>
        b.Shape.Builder?.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) == true &&
        !(p.Workspace.FirstMap.ConstructionBaseline?.Contains(b.Id, StringComparer.OrdinalIgnoreCase) ?? false));
    public static bool HasPlayableComputer(MapProject p) => p.Actors.Any(a => Short(a.Class) == "SObjective" &&
        a.Props["Triggers"] is SvJsonArray triggers && triggers.Any(t => p.Actors.Any(c => c.Id == PropValues.ActorId(t) && Short(c.Class) == "SComputerObjectiveTrigger")) &&
        p.Actors.Any(m => Short(m.Class) == "SMission" && m.Props.GetInt("GameMode") == 5 &&
            m.Props["Objectives"] is SvJsonArray objectives && objectives.Any(o => PropValues.ActorId(o) == a.Id)));
    private static int Team(Actor a) => (int)ModelJson.Num(a.Props["TeamNumber"]);
    private static string Short(string cls) => cls[(cls.LastIndexOf('.') + 1)..];

    // Two room-sized carved spaces joined by an overlapping, player-sized carved opening.
    // Map Check remains the authority for obstructed or otherwise unplayable geometry.
    public static bool HasConnectedRooms(MapProject p)
    {
        var boxes = p.Brushes.Where(b => b.Oper == CsgOper.Subtract)
            .Select(b => ShapeCsg.World(b).Bounds()).ToList();
        var rooms = Enumerable.Range(0, boxes.Count).Where(i => boxes[i].Max.X - boxes[i].Min.X >= 512 && boxes[i].Max.Y - boxes[i].Min.Y >= 512).ToHashSet();
        var visited = new HashSet<int>();
        foreach (int start in rooms)
        {
            if (!visited.Add(start)) continue;
            var pending = new Queue<int>();
            pending.Enqueue(start);
            int connectedRooms = 0;
            while (pending.TryDequeue(out int current))
            {
                if (rooms.Contains(current) && ++connectedRooms >= 2) return true;
                for (int next = 0; next < boxes.Count; next++)
                {
                    if (!visited.Contains(next) && Joins(boxes[current], boxes[next], p.Workspace.Movement.StandHeight, Math.Max(72, p.Workspace.Movement.Radius * 2)))
                    { visited.Add(next); pending.Enqueue(next); }
                }
            }
        }
        return false;
    }

    private static bool Joins((Vec3 Min, Vec3 Max) a, (Vec3 Min, Vec3 Max) b, double height, double width) =>
        ((Math.Min(a.Max.X, b.Max.X) - Math.Max(a.Min.X, b.Min.X) >= -.01 &&
          Math.Min(a.Max.Y, b.Max.Y) - Math.Max(a.Min.Y, b.Min.Y) >= width) ||
         (Math.Min(a.Max.Y, b.Max.Y) - Math.Max(a.Min.Y, b.Min.Y) >= -.01 &&
          Math.Min(a.Max.X, b.Max.X) - Math.Max(a.Min.X, b.Min.X) >= width)) &&
        Math.Min(a.Max.Z, b.Max.Z) - Math.Max(a.Min.Z, b.Min.Z) >= height;
}
