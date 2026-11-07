using System.Globalization;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Edit;

/// <summary>Where a piece goes on a wall: the point on the wall at floor height, the direction into the wall (the
/// carved brush's outward face normal, level), and the face's material.</summary>
public readonly record struct WallAnchor(FaceRef Face, Vec3 Point, Vec3 Into, string? Material)
{
    public bool Vertical => Math.Abs(Into.Z) > .95;
    public Vec3 PlaneNormal { get; init; }
    public string Surface => Vertical ? Into.Z > 0 ? "ceiling" : "floor" : "wall";
    /// <summary>The yaw that turns a blockout piece's +X (its length) into the wall.</summary>
    public Rot Facing => new(0, (int)Math.Round(Math.Atan2(Into.Y, Into.X) * Rot.UnitsPerTurn / (2 * Math.PI)), 0);
}

/// <summary>
/// The RE+ hover "+" on a wall of a carved room: a doorway or window through the wall (as deep as the wall is thick),
/// a doorway with a zone portal sheet in it, a corridor, vent or crawlway leading away from it, or a new room behind it
/// joined by a doorway. Pieces are the parametric blockout builders, stand on the room's floor, face away from the room
/// and take the wall's material.
/// </summary>
public static class WallPieces
{
    /// <summary>A doorway with a zone portal sheet across it.</summary>
    public const string PortalDoorway = "portal-doorway";

    public static readonly IReadOnlyList<string> Kinds = ["doorway", PortalDoorway, "window", "corridor", "vent", "crawlway", "room"];
    public static readonly IReadOnlyList<string> VerticalKinds = ["hatch", "shaft", "room", "stairs"];
    public static IReadOnlyList<string> KindsAt(WallAnchor anchor) => anchor.Vertical ? VerticalKinds : Kinds;
    public static string Title(string kind, WallAnchor anchor) => anchor.Vertical ? kind switch
    {
        "hatch" => "Hatch Opening", "shaft" => anchor.Into.Z > 0 ? "Shaft Up" : "Shaft Down",
        "room" => anchor.Into.Z > 0 ? "Room Above" : "Room Below", "stairs" => "Connect Stairs…", _ => Title(kind)
    } : Title(kind);

    /// <summary>The menu title of a kind ("Doorway with Zone Portal").</summary>
    public static string Title(string kind) =>
        kind == PortalDoorway ? "Doorway with Zone Portal" : char.ToUpperInvariant(kind[0]) + kind[1..];

    /// <summary>The wall thickness a doorway cuts through when nothing open lies behind the wall within reach.</summary>
    public const double DefaultDepth = 64;

    /// <summary>The wall left between a room and a new room behind it.</summary>
    public const double RoomGap = 32;

    /// <summary>How far behind a wall the open space search looks.</summary>
    public const double MaxDepth = 1024;

    /// <summary>
    /// The anchor for a wall face of a subtractive brush, under <paramref name="at"/> (a point on or near the face;
    /// the face's centre when null). Null when the face is not a wall (it slopes more than about 17 degrees) or the
    /// brush adds solid (only carved rooms have walls to build through).
    /// </summary>
    public static WallAnchor? Anchor(MapProject p, FaceRef face, Vec3? at = null)
    {
        if (p.FindBrush(face.Brush) is not { } b || b.Editor.Hidden || b.Editor.Locked
            || Compile.BakedContent.SurfacesLocked(p, b) || face.Face < 0 || face.Face >= b.Shape.Faces.Count)
        {
            return null;
        }

        BrushFace f = b.Shape.Faces[face.Face];
        List<Vec3> w = ShapeTransform.For(b).FaceToWorld(f);
        Vec3 n = Poly.Normal(w);
        if (w.Count < 3) return null;
        if (Math.Abs(n.Z) > .95)
        {
            Vec3 on = Plane3.FromPointNormal(w[0], n).Project(at ?? Poly.Average(w));
            return new WallAnchor(face, Poly.Tidy(on), Vec3.UnitZ * Math.Sign(n.Z) * (b.Oper == CsgOper.Subtract ? 1 : -1), f.Material) { PlaneNormal = n };
        }
        if (b.Oper != CsgOper.Subtract || Math.Abs(n.Z) >= 0.3)
        {
            return null;
        }

        Vec3 into = new Vec3(n.X, n.Y, 0).Normalized();
        Plane3 plane = Plane3.FromPointNormal(w[0], n);
        Vec3 onWall = plane.Project(at ?? Poly.Average(w));
        double floor = w.Min(v => v.Z);
        return new WallAnchor(face, Poly.Tidy(onWall with { Z = floor }), into, f.Material);
    }

    /// <summary>The brushes of <paramref name="kind"/> placed at the anchor (not yet added to the map).</summary>
    public static List<Brush> Build(MapProject p, WallAnchor a, string kind, WorldGeometry? world = null)
    {
        string k = kind.ToLowerInvariant();
        if (a.Vertical) return BuildVertical(p, a, k, world ?? new WorldGeometry(p, null));
        switch (k)
        {
            case "doorway" or "window":
            {
                double depth = WallDepth(world ?? new WorldGeometry(p, null), a, k == "window" ? 104 + 64 : 112);
                return Piece(p, a, k, depth / 2, ("depth", depth));
            }

            case PortalDoorway:
            {
                double depth = WallDepth(world ?? new WorldGeometry(p, null), a, 112);
                return Piece(p, a, "doorway", depth / 2, ("depth", depth), ("portal", 1));
            }

            case "corridor" or "vent" or "crawlway":
            {
                double length = BuilderCatalog.Get(k).Defaults().Get("length", 512);
                return Piece(p, a, k, length / 2);
            }

            case "room":
            {
                double length = BuilderCatalog.Get("room").Defaults().Get("length", 1024);
                List<Brush> door = Piece(p, a, "doorway", RoomGap / 2, ("depth", RoomGap));
                // The room's ids must not clash with the doorway's, which are not in the map yet.
                var scratch = new MapProject { Brushes = [.. p.Brushes, .. door], Actors = p.Actors, Ges = p.Ges };
                return [.. door, .. Piece(scratch, a, "room", RoomGap + length / 2)];
            }

            default:
                throw new ArgumentException($"wall piece must be {string.Join(", ", Kinds)}, not '{kind}'");
        }
    }

    private static List<Brush> BuildVertical(MapProject p, WallAnchor a, string kind, WorldGeometry world)
    {
        if (kind is not ("hatch" or "shaft" or "room")) throw new ArgumentException("Choose a hatch opening, shaft or connected room on this floor/ceiling");
        var ids = new FreshIds(p);
        Brush Cut(string stem, Vec3 size, Vec3 center)
        {
            var shape = OpenScct.Editor.Core.Geometry.Box.Shape(size);
            foreach (BrushFace face in shape.Faces) face.Material = a.Material ?? Starter.DefaultMaterial;
            return new Brush(ids.Next(stem)) { Shape = shape, Oper = CsgOper.Subtract, Location = Poly.Tidy(center) };
        }
        double depth = WallDepth(world, a, 0);
        double overlap = 2 + (Math.Abs(a.PlaneNormal.Z) > .01 ? 128 * (Math.Abs(a.PlaneNormal.X) + Math.Abs(a.PlaneNormal.Y)) / Math.Abs(a.PlaneNormal.Z) : 0);
        if (kind == "shaft") depth = Math.Max(512, depth);
        if (kind == "room")
        {
            const double height = 384;
            return [Cut("Hatch", new(256, 256, RoomGap + 2 * overlap), a.Point + a.Into * RoomGap / 2),
                Cut("Room", new(1024, 1024, height), a.Point + a.Into * (RoomGap + height / 2))];
        }
        return [Cut(kind == "hatch" ? "Hatch" : "Shaft", new(256, 256, depth + 2 * overlap), a.Point + a.Into * depth / 2)];
    }

    /// <summary>How thick the solid behind the wall is at <paramref name="height"/> over the floor: the distance to the
    /// next open space, or <see cref="DefaultDepth"/> when there is none within <see cref="MaxDepth"/>.</summary>
    public static double WallDepth(WorldGeometry world, WallAnchor a, double height)
    {
        Vec3 start = a.Point + new Vec3(0, 0, height);
        const double Step = 4;
        bool seenSolid = false;
        for (double d = Step / 2; d <= MaxDepth; d += Step)
        {
            bool solid = world.IsSolid(start + a.Into * d);
            if (solid)
            {
                seenSolid = true;
            }
            else if (seenSolid)
            {
                return Math.Round((d - Step / 2) / Step) * Step;
            }
        }

        return DefaultDepth;
    }

    private static List<Brush> Piece(MapProject p, WallAnchor a, string kind, double along, params (string Key, double Value)[] numbers)
    {
        BuilderSpec spec = BuilderCatalog.Get(kind).Defaults();
        foreach ((string key, double value) in numbers)
        {
            spec.Numbers[key] = value;
        }

        if (a.Material is { Length: > 0 } m)
        {
            spec.Strings["material"] = m;
        }

        Vec3 at = Poly.Tidy(a.Point + a.Into * along);
        return [.. PiecePlacer.Create(p, spec, at, a.Facing)];
    }

    /// <summary>A short description of where a piece went, for status lines and CLI reports.</summary>
    public static string Describe(WallAnchor a, string kind) => string.Create(CultureInfo.InvariantCulture,
        $"{kind} on {a.Surface} {a.Face} at {a.Point.X:0.#}, {a.Point.Y:0.#}, {a.Point.Z:0.#}");
}
