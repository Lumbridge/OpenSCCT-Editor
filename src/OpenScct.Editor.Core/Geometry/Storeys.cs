using OpenScct.Content.Collision;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Geometry;

/// <summary>One storey: its name, the height range the storey filter shows, and the floor it was found from (null for
/// a storey the map maker typed). <see cref="Top"/> is infinite for the highest detected storey.</summary>
public sealed record Storey(string Name, double Bottom, double Top, double? Floor)
{
    /// <summary>True when a vertical extent overlaps the storey.</summary>
    public bool Overlaps(double minZ, double maxZ) => maxZ >= Bottom && minZ < Top;

    public bool Contains(double z) => z >= Bottom && z < Top;

    /// <summary>The height new things go at on this storey: its floor, or the bottom of a typed range.</summary>
    public double Level => Floor ?? Bottom;
}

/// <summary>
/// The storeys of a map, for the storey filter (RE+ Map Design's storey slider). A map maker can name their own height
/// ranges (<see cref="Workspace.Storeys"/>); otherwise they are found from the floors: every walkable surface (the
/// floor of a carved brush, the top of an added one, and the floors of an imported map's baked collision) is binned by
/// height with its area. RE+'s rules then apply: floors within 64 units are one, floors with under 6% of the largest
/// floor's area are dropped, and at most 24 are kept. Each storey runs from just under its floor to just under the next.
/// </summary>
public static class Storeys
{
    /// <summary>Floors closer than this are one floor.</summary>
    public const double MergeDistance = 64;

    /// <summary>A floor with less than this share of the largest floor's area is not a storey.</summary>
    public const double MinShare = 0.06;

    public const int MaxStoreys = 24;

    /// <summary>A storey starts this far under its floor, so the floor itself (and things standing in it) is inside.</summary>
    public const double Below = 16;

    /// <summary>Surfaces this close to level count as floors (the game walks floors with a normal Z of 0.7 or more).</summary>
    private const double FloorNormalZ = 0.7;

    /// <summary>The floors of the last baked collision read. Detection runs off the main thread; the cache is swapped
    /// whole, so a reader always sees one file's floors.</summary>
    private static volatile BakedFloorCache? s_bakedFloors;

    /// <summary>The map's storeys, lowest first: its own ranges when it has any, else the detected ones.</summary>
    public static IReadOnlyList<Storey> Of(MapProject p, string? projectFolder) =>
        p.Workspace.Storeys.Count > 0
            ? p.Workspace.Storeys.OrderBy(s => s.Bottom).Select(s => new Storey(s.Name, s.Bottom, s.Top, null)).ToList()
            : Detect(p, projectFolder);

    /// <summary>
    /// The storey <paramref name="spec"/> names: its name, its number (1 for the lowest), or <c>all</c> for none (-1).
    /// Throws <see cref="ArgumentException"/> listing the storeys when it names none of them.
    /// </summary>
    public static int Find(IReadOnlyList<Storey> storeys, string spec)
    {
        string s = spec.Trim();
        if (s.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        int byName = storeys.ToList().FindIndex(st => st.Name.Equals(s, StringComparison.OrdinalIgnoreCase));
        if (byName >= 0)
        {
            return byName;
        }

        if (int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= storeys.Count)
        {
            return n - 1;
        }

        throw new ArgumentException(storeys.Count == 0
            ? "the map has no storeys (none of its own, and no floors were found)"
            : $"no storey '{spec}' (storeys: {string.Join(", ", storeys.Select(st => st.Name))}, or all)");
    }

    /// <summary>The storey a workspace shows (<see cref="Workspace.ShownStorey"/>), or -1 for all.</summary>
    public static int Shown(IReadOnlyList<Storey> storeys, double? height) =>
        height is double z ? storeys.ToList().FindIndex(s => s.Contains(z)) : -1;

    /// <summary>
    /// How tall storey <paramref name="index"/> is when copies stack it: its own range; for the open top of the
    /// highest detected storey, enough to hold what stands on it (<paramref name="contentTop"/>, rounded up to 16
    /// units) and at least as tall as the storey below.
    /// </summary>
    public static double StackHeight(IReadOnlyList<Storey> storeys, int index, double contentTop)
    {
        Storey s = storeys[index];
        if (double.IsFinite(s.Top))
        {
            return s.Top - s.Bottom;
        }

        double below = index > 0 && double.IsFinite(storeys[index - 1].Top) ? storeys[index - 1].Top - storeys[index - 1].Bottom : MergeDistance;
        return Math.Max(below, Math.Ceiling((contentTop - s.Bottom) / 16) * 16);
    }

    /// <summary>The top of what stands on a storey: the highest point of the objects whose middle is on it, or its
    /// bottom when it is empty. An imported map's baked world is not counted.</summary>
    public static double ContentTop(MapProject p, Storey s) =>
        p.AllRefs()
            .Select(r => Scripting.MapObjects.Bounds(p, r))
            .Where(b => s.Contains((b.Min.Z + b.Max.Z) / 2))
            .Select(b => b.Max.Z)
            .DefaultIfEmpty(s.Bottom)
            .Max();

    /// <summary>A storey name none of <paramref name="taken"/> uses: "Floor n", n from one past their count.</summary>
    public static string NewName(IReadOnlyCollection<string> taken)
    {
        int n = taken.Count + 1;
        while (taken.Any(t => string.Equals(t, $"Floor {n}", StringComparison.OrdinalIgnoreCase)))
        {
            n++;
        }

        return $"Floor {n}";
    }

    /// <summary>The storeys found from the map's floors, lowest first (none for a map with no floors).</summary>
    public static IReadOnlyList<Storey> Detect(MapProject p, string? projectFolder) => FromFloors(Floors(p, projectFolder));

    /// <summary>Storeys from floor surfaces (height, area), by RE+'s merge, share and count rules.</summary>
    public static IReadOnlyList<Storey> FromFloors(IEnumerable<(double Z, double Area)> surfaces)
    {
        var clusters = new List<(double Weighted, double Area, double First)>();
        foreach ((double z, double area) in surfaces.Where(s => s.Area > 0 && double.IsFinite(s.Z)).OrderBy(s => s.Z))
        {
            if (clusters.Count > 0 && z - clusters[^1].First <= MergeDistance)
            {
                (double w, double a, double first) = clusters[^1];
                clusters[^1] = (w + z * area, a + area, first);
            }
            else
            {
                clusters.Add((z * area, area, z));
            }
        }

        if (clusters.Count == 0)
        {
            return [];
        }

        double largest = clusters.Max(c => c.Area);
        List<double> floors = clusters
            .Where(c => c.Area >= largest * MinShare)
            .OrderByDescending(c => c.Area)
            .Take(MaxStoreys)
            .Select(c => Math.Round(c.Weighted / c.Area))
            .Order()
            .ToList();
        var storeys = new List<Storey>(floors.Count);
        for (int i = 0; i < floors.Count; i++)
        {
            double top = i + 1 < floors.Count ? floors[i + 1] - Below : double.PositiveInfinity;
            storeys.Add(new Storey($"Floor {i + 1}", floors[i] - Below, top, floors[i]));
        }

        return storeys;
    }

    /// <summary>The walkable surfaces of the map's brushes and of its baked collision, as (height, area).</summary>
    public static IEnumerable<(double Z, double Area)> Floors(MapProject p, string? projectFolder)
    {
        // An .sdc import's baked brushes are in the baked collision already; count their floors once.
        IReadOnlyList<(double Z, double Area)> baked = BakedFloors(p, projectFolder);
        foreach (Brush b in baked.Count > 0 ? p.Brushes.Where(b => !BakedContent.IsBaked(b)) : p.Brushes)
        {
            if (b.IsPortal || b.IsNotSolid || b.Shape.Faces.Count < 4)
            {
                continue;
            }

            // A carved brush's floor faces down (out of the space); an added brush's top faces up.
            double want = b.Oper == CsgOper.Subtract ? -1 : 1;
            foreach (BrushFace f in b.WorldFaces())
            {
                if (f.Vertices.Count < 3)
                {
                    continue;
                }

                Vec3 n = Poly.Newell(f.Vertices);
                double length = n.Length;
                if (length > 0 && n.Z * want / length >= FloorNormalZ)
                {
                    yield return (Poly.Average(f.Vertices).Z, length / 2);
                }
            }
        }

        foreach ((double Z, double Area) floor in baked)
        {
            yield return floor;
        }
    }

    /// <summary>The floors of the baked world's collision (a converted map imported for editing). The last file read
    /// is remembered, so finding the storeys again after an edit does not read and scan it again.</summary>
    private static IReadOnlyList<(double Z, double Area)> BakedFloors(MapProject p, string? projectFolder)
    {
        if (CollisionFile(p, projectFolder) is not { } file)
        {
            return [];
        }

        DateTime stamp = File.GetLastWriteTimeUtc(file);
        if (s_bakedFloors is { } cached && cached.File == file && cached.Stamp == stamp)
        {
            return cached.Floors;
        }

        CollisionData data;
        try
        {
            data = CollisionReader.Load(file, strict: false);
        }
        catch (Exception ex) when (ex is IOException or OpenScct.Content.SvMapFormatException or InvalidDataException)
        {
            return [];
        }

        var floors = new List<(double Z, double Area)>();
        foreach (Collider c in data.Colliders.Where(c => c.Kind == ColliderKind.WorldBsp))
        {
            for (int i = (int)c.TriStart; i < c.TriStart + c.TriCount; i++)
            {
                var n = data.TriangleNormal(i);
                double length = Math.Sqrt((double)n.X * n.X + (double)n.Y * n.Y + (double)n.Z * n.Z);
                if (length <= 0 || n.Z / length < FloorNormalZ)
                {
                    continue;
                }

                CollisionTriangle t = data.Triangles[i];
                double z = (data.Vertices[(int)t.I0].Z + data.Vertices[(int)t.I1].Z + data.Vertices[(int)t.I2].Z) / 3.0;
                floors.Add((z, length / 2));
            }
        }

        s_bakedFloors = new BakedFloorCache(file, stamp, floors);
        return floors;
    }

    private sealed record BakedFloorCache(string File, DateTime Stamp, IReadOnlyList<(double Z, double Area)> Floors);

    /// <summary>The baked world's collision file, or null.</summary>
    private static string? CollisionFile(MapProject p, string? projectFolder)
    {
        if (p.Baked is not { SourceMap.Length: > 0 } baked)
        {
            return null;
        }

        string source = baked.SourceMap;
        string folder = Path.IsPathRooted(source) || projectFolder is null ? source : Path.Combine(projectFolder, source);
        string file = Path.Combine(folder, "sim", "collision.bin");
        if (!File.Exists(file) && EditorServices.ContentRoot is { } content)
        {
            file = Path.Combine(content, "svmap", source, "sim", "collision.bin");
        }

        return File.Exists(file) ? Path.GetFullPath(file) : null;
    }
}
