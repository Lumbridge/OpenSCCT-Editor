using System.Numerics;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Game.Lighting;

int checks = 0;
var near = Plane(40, -200, 200);
var far = Plane(80, -200, 200);
Vector3 start = new(-20, 0, 0);
foreach (var triangles in new[] { near.Concat(far).ToArray(), far.Concat(near).ToArray() })
{
    TriangleBvh bvh = TriangleBvh.Build(triangles);
    Require(bvh.FarthestBlocker(start, Vector3.UnitZ, 100, false) == 80, "farthest blocker ignores triangle order");
    Require(bvh.FarthestBlocker(start, Vector3.UnitZ, 60, false) == 40, "segment excludes the farther plane");
    Require(bvh.FarthestBlocker(start, Vector3.UnitZ, 30, false) < 0, "short segment misses both planes");
    Require(bvh.Occluded(start, Vector3.UnitZ, 100, false), "any-hit visibility still sees blockers");
}

var sky = far.Select(t => t with { Flags = OccluderFlags.Sky });
TriangleBvh skyScene = TriangleBvh.Build(near.Concat(sky).ToArray());
Require(skyScene.FarthestBlocker(start, Vector3.UnitZ, 100, true) == 40, "sun passes sky triangles");
Require(skyScene.FarthestBlocker(start, Vector3.UnitZ, 100, false) == 80, "point lights treat sky as a blocker");
AlphaMask transparent = AlphaMask.FromRgba(1, 1, new byte[4])!;
TriangleBvh masked = TriangleBvh.Build(near.Concat(far.Select(t => t with { Mask = transparent })).ToArray());
Require(masked.FarthestBlocker(start, Vector3.UnitZ, 100, false) == 40, "transparent mask does not widen a shadow");
Require(TriangleBvh.Build([]).FarthestBlocker(start, Vector3.UnitZ, 100, false) < 0, "empty tree misses");

var front = new OccluderTriangle(new(-100, -100, 20), new(0, 100, 20), new(100, -100, 20), OccluderFlags.None, 7);
var back = new OccluderTriangle(front.A, front.C, front.B, OccluderFlags.None, 8);
foreach (var triangles in new[] { new[] { back, front }, new[] { front, back } })
{
    var bvh = TriangleBvh.Build(triangles);
    BvhHit? hit = bvh.Closest(Vector3.Zero, Vector3.UnitZ, 100);
    Require(hit is { Front: true, Distance: 20 } && bvh.SourceOf(hit.Value.Triangle) == 7,
        "coincident visible and back faces do not depend on visitation order");
}
var closerBack = back with { A = back.A - Vec3.UnitZ, B = back.B - Vec3.UnitZ, C = back.C - Vec3.UnitZ };
Require(TriangleBvh.Build([front, closerBack]).Closest(Vector3.Zero, Vector3.UnitZ, 100) is { Front: false, Distance: 19 },
    "a genuinely nearer back face remains the closest hit");
var sameGeometry = front with { Source = 3 };
foreach (var triangles in new[] { new[] { front, sameGeometry }, new[] { sameGeometry, front } })
{
    var bvh = TriangleBvh.Build(triangles);
    Require(bvh.SourceOf(bvh.Closest(Vector3.Zero, Vector3.UnitZ, 100)!.Value.Triangle) == 3,
        "coincident same-facing bounce sources are stable");
}

string scratch = Path.Combine(Path.GetTempPath(), "ose-shadow-cache-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    foreach (int effect in new[] { 0, BakeLight.LE_Cylinder, ActorLightRules.LE_Sunlight })
    foreach (bool faithful in new[] { false, true })
    {
        // A narrow near strip overlaps a broad distant blocker. The distant plane casts a wide soft shadow;
        // stopping at the strip underestimates that band even though both scenes have exactly the same geometry.
        var triangles = Plane(40, -2, 0).Concat(Plane(80, -200, 0)).ToArray();
        var light = new BakeLight { Id = "Soft", Location = new(0, 0, 100), Direction = -Vec3.UnitZ,
            LightRadius = 30, Brightness = 128, Effect = effect };
        var q = new BakeQuality { Samples = 4, LightSize = 8, SunSize = 10, Faithful = faithful };
        BakeScene Scene(IEnumerable<OccluderTriangle> source) => new()
        {
            Charts = [new LightmapChart
            {
                Origin = new(-20, -5, 0), StepU = Vec3.UnitX, StepV = Vec3.UnitY, Normal = Vec3.UnitZ,
                Width = 40, Height = 10,
                Pieces = [new ChartPiece([new(0, 0), new(40, 0), new(40, 10), new(0, 10)], 0)],
            }.Seal()],
            Lights = [light], Ambient = _ => default, Occluders = TriangleBvh.Build(source.ToArray()),
        };
        var a = Scene(triangles);
        var b = Scene(triangles.Reverse());
        Require(a.Occluders.Key == b.Occluders.Key, "reordering geometry preserves the shadow cache key");
        var cached = new LightmapBaker();
        var initial = cached.Bake(a, q, 128);
        var fresh = new LightmapBaker().Bake(b, q, 128);
        Same(initial, fresh, $"cold bake ignores triangle order: effect {effect}, faithful {faithful}");
        var warm = cached.Bake(b, q, 128);
        Require(warm.Stats.CachedJobs == warm.Stats.Jobs && warm.Stats.Jobs > 0, "unchanged geometry reuses shadows");
        Same(fresh, warm, "warm cache equals cold reordered bake");
        string path = Path.Combine(scratch, $"{effect}-{faithful}.bin");
        cached.Save(path);
        var loaded = new LightmapBaker(); loaded.Load(path);
        var restored = loaded.Bake(b, q, 128);
        Require(restored.Stats.CachedJobs == restored.Stats.Jobs, "saved shadow masks are reused");
        Same(fresh, restored, "save/load preserves the cold result");

        // Additional leaves force a different BVH layout. They lie outside the point light and chart ray bounds.
        var distant = Enumerable.Range(0, 24).Select(i => new OccluderTriangle(new(2000 + i, 0, 0),
            new(2000 + i, 30, 0), new(2000 + i, 30, 60), OccluderFlags.None, 100 + i));
        var expanded = Scene(triangles.Concat(distant));
        Same(fresh, new LightmapBaker().Bake(expanded, q, 128), "unrelated geometry cannot change a soft shadow");
        Same(fresh, cached.Bake(expanded, q, 128), "cached and cold builds agree after unrelated geometry changes");

        // Old first-hit masks must not survive the changed penumbra rule.
        byte[] old = File.ReadAllBytes(path);
        BitConverter.GetBytes(6).CopyTo(old, 8);
        File.WriteAllBytes(path, old);
        var legacy = new LightmapBaker(); legacy.Load(path);
        var rebuilt = legacy.Bake(b, q, 128);
        Require(rebuilt.Stats.CachedJobs == 0, "old mask version is invalidated");
        Same(fresh, rebuilt, "invalidated masks rebuild to the cold result");

        var sharedFace = Scene(triangles.Concat([front, back]));
        var reversedFace = Scene(triangles.Concat([front, back]).Reverse());
        var faceBaker = new LightmapBaker();
        var faceFresh = faceBaker.Bake(sharedFace, q, 128);
        var reversedFresh = new LightmapBaker().Bake(reversedFace, q, 128);
        Same(faceFresh, reversedFresh, "coincident faces give the same cold burial and shadow result");
        Same(reversedFresh, faceBaker.Bake(reversedFace, q, 128), "coincident-face cache equals cold reordered bake");
    }
}
finally
{
    string target = Path.GetFullPath(scratch);
    string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
    if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("ose-shadow-cache-", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Refusing to delete outside the test's temporary cache directory");
    }

    Directory.Delete(target, recursive: true);
}
Console.WriteLine($"PASS: {checks} shadow ordering, visibility and cache checks");

OccluderTriangle[] Plane(double z, double lo, double hi) =>
[
    new(new(lo, -200, z), new(hi, -200, z), new(hi, 200, z), OccluderFlags.None, 0),
    new(new(lo, -200, z), new(hi, 200, z), new(lo, 200, z), OccluderFlags.None, 1),
];

void Same(LightmapResult a, LightmapResult b, string label) => Require(a.Texels[0].SequenceEqual(b.Texels[0]) &&
    a.Rgba(0).SequenceEqual(b.Rgba(0)), label);

void Require(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    checks++;
}
