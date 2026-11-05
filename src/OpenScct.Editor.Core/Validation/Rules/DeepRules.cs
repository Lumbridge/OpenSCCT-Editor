using System.Buffers.Binary;
using OpenScct.Content.Collision;
using OpenScct.Content.Json;
using OpenScct.Core;
using OpenScct.Editor.Core.Analysis;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Physics.Collision;

namespace OpenScct.Editor.Core.Validation.Rules;

/// <summary>
/// The deep rules: checks of the map as OpenSCCT loads it (<see cref="CheckOptions.Compiled"/>), so they see baked
/// worlds, static mesh collision and the compiled zones as the game does. Zones that leak into each other (with the
/// smallest opening that would seal them), zone portals that seal nothing, actors in the void, static meshes floating just above a floor, mesh collision past
/// the original engine's caps, and what neither side can reach or use.
/// </summary>
internal sealed class DeepRules : MapRule
{
    public const string InVoid = "place.in-void";
    public const string Floating = "place.floating";
    public const string MeshCollision = "limit.mesh-collision";

    /// <summary>A mesh whose bottom is this close above a floor (and not touching anything) reads as floating.</summary>
    public const float FloatGap = 48f;

    /// <summary>Smaller gaps are left alone: stock maps are full of meshes a unit or two up, and they do not show.</summary>
    public const float MinGap = 4f;

    /// <summary>An actor this far or less inside solid is mounted on a wall, not lost in the void.</summary>
    private const float WallDepth = 16f;

    /// <summary>How far the in-void fix looks for open space.</summary>
    private const float FixReach = 64f;

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(MapCompiler.ZoneLeakCode, Severity.Warning, "Two zone actors in one connected space: no zone portal seals them apart (at: the smallest opening that would; the views draw the way through and that opening)") { Deep = true },
        new(MapCompiler.ZonePortalCode, Severity.Warning, "A zone portal that seals nothing: no open space on either side, the space flows round it so both sides are one zone (the views draw the way round), or neither side has a zone actor") { Deep = true },
        new(InVoid, Severity.Warning, $"An actor more than {WallDepth} units inside the solid world (lights, sounds, emitters, zone actors...); fix, when open space lies within {FixReach} on one side only: move it there") { Deep = true },
        new(Floating, Severity.Warning, $"A colliding static mesh whose bottom hangs {MinGap} to {FloatGap} units above the floor under it, touching nothing at its sides or top; fix: lower it onto the floor") { Deep = true },
        new(MeshCollision, Severity.Warning, $"A static mesh with more than {Limits.CollisionTrianglesPerMesh} collision triangles, {Limits.CollisionBspNodes} collision BSP nodes, collision hulls with more faces than their corners allow (the original game's caps; error for the original game), or hulls of more than {Limits.MaxHullCorners} corners (more than the original tools build; the game's own limit is not known)") { Deep = true },
        new(Reachability.ObjectiveCode, Severity.Warning, "An objective spies cannot use from anywhere they get from their starts: the game's own use test (reach cylinder, line to it, front side, who may use it), with doors opened only by what spies reach and lifts ridden once they work; a note when walls break on events or GE triggers change the map") { Deep = true },
        new(Reachability.UseSpotCode, Severity.Warning, "A switch, ammo stock or infirmary neither side can use from anywhere they get from their starts (the game's use test; doors and lifts as each side can work them; a note when the map changes as it plays)") { Deep = true },
        new(Reachability.StartOffFloorCode, Severity.Warning, "A player start with no floor of its side's navigation graph within 200 under it") { Deep = true },
    ];

    public override void Run(CheckContext c)
    {
        if (c.Options.Compiled is not { } map)
        {
            return;
        }

        foreach (Finding zone in map.CompileFindings.Where(f => f.Code is MapCompiler.ZoneLeakCode or MapCompiler.ZonePortalCode))
        {
            c.Report(zone);
        }

        lock (map.Gate)
        {
            if (c.Wants(InVoid))
            {
                Void(c, map);
            }

            if (c.Wants(Floating) || c.Wants(MeshCollision))
            {
                Meshes(c, map);
            }

            if (Reachability.Codes.Any(c.Wants))
            {
                ReachMap spy = Reachability.FromStarts(map, new ReachProfile(TeamSide.Spy), map.ActivationMask);
                ReachMap merc = Reachability.FromStarts(map, new ReachProfile(TeamSide.Merc), map.ActivationMask);
                foreach (Finding f in Reachability.Findings(map, spy, merc))
                {
                    c.Report(f);
                }
            }
        }
    }

    // ---- void ----------------------------------------------------------------------------------------------------

    private static void Void(CheckContext c, AnalysisMap map)
    {
        CollisionWorld world = map.Collision;
        foreach (Actor a in c.Project.Actors.Where(a => CanBeInVoid(c, a)))
        {
            FVector p = AnalysisMap.F(a.Location);
            if (!Solid(world, p) || Directions.Any(d => !Solid(world, p + d * WallDepth)))
            {
                continue;
            }

            var finding = new Finding(Severity.Warning, InVoid, $"{a.Id} ({Short(a.Class)}) is inside the solid world, more than {WallDepth} units from open space",
                [ObjectRef.Actor(a.Id)], a.Location);
            if (OpenNear(world, p) is { } by)
            {
                finding = finding.WithFix(MoveTo(a, a.Location + by, $"Move {a.Id} {Math.Round(by.Length)} units into the open"));
            }

            c.Report(finding);
        }
    }

    /// <summary>Actors whose place is open space: not meshes, movers, brushes or infos that may sit anywhere, and not the
    /// actors an .sdc import brings in that the cook dropped (they are not in the game).</summary>
    private static bool CanBeInVoid(CheckContext c, Actor a) =>
        a.Shape is null && !BakedContent.IsCookedOut(a)
        && !c.IsA(a, "Engine.StaticMeshActor") && !c.IsA(a, "Engine.Mover") && !c.IsA(a, "Engine.Brush")
        && (!c.IsA(a, "Engine.Info") || c.IsA(a, "Engine.ZoneInfo"));

    private static readonly FVector[] Directions =
    [
        new(1f, 0f, 0f), new(-1f, 0f, 0f), new(0f, 1f, 0f), new(0f, -1f, 0f), new(0f, 0f, 1f), new(0f, 0f, -1f),
    ];

    /// <summary>
    /// True when <paramref name="p"/> is inside the world's solid: a short line from it starts in solid (the level's
    /// point check only answers for boxes that cross a surface).
    /// </summary>
    private static bool Solid(CollisionWorld world, FVector p) =>
        world.LineCheck(out FCheckResult hit, null, p - new FVector(0f, 0f, 1f), p, default, ETraceFlags.TRACE_Level) && hit.Time <= 0f;

    /// <summary>
    /// The move along an axis (8-unit steps, up to <see cref="FixReach"/>) into open space, 8 units clear, when only
    /// one direction has open space that near. An actor in a wall with open space on both sides (a light between two
    /// rooms, a zone actor in a partition) gets no fix: which side it belongs to is the author's call.
    /// </summary>
    private static Vec3? OpenNear(CollisionWorld world, FVector p)
    {
        Vec3? found = null;
        foreach (FVector dir in Directions)
        {
            for (float d = WallDepth + 8f; d <= FixReach; d += 8f)
            {
                if (!Solid(world, p + dir * d) && !Solid(world, p + dir * (d + 8f)))
                {
                    if (found is not null)
                    {
                        return null;
                    }

                    found = AnalysisMap.V(dir * (d + 8f));
                    break;
                }
            }
        }

        return found;
    }

    // ---- meshes ----------------------------------------------------------------------------------------------

    private static void Meshes(CheckContext c, AnalysisMap map)
    {
        CollisionData data = map.SvMap.Collision;
        Dictionary<string, List<Collider>> byActor = MeshColliders(c, data);

        var overCap = new Dictionary<string, (long Tris, long Bsp, Hulls Bad, List<string> Actors)>(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, List<Collider> colliders) in byActor.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (c.Project.FindActor(id) is not { } actor)
            {
                continue;
            }

            // The per-poly set is what the trace table holds; the simple hulls are counted on their own.
            long tris = colliders.Where(col => col.Kind == ColliderKind.MeshPoly).Sum(col => (long)col.TriCount);
            long bsp = colliders.Sum(col => (long)col.BspCount);
            Hulls bad = colliders.Aggregate(default(Hulls), (sum, col) => sum + BadHulls(data, col));
            if (tris > Limits.CollisionTrianglesPerMesh || bsp > Limits.CollisionBspNodes || bad.Any)
            {
                string mesh = PropValues.AssetPath(actor.Props["StaticMesh"]) ?? id;
                (long Tris, long Bsp, Hulls Bad, List<string> Actors) entry = overCap.GetValueOrDefault(mesh, (tris, bsp, bad, []));
                entry.Actors.Add(id);
                overCap[mesh] = (Math.Max(entry.Tris, tris), Math.Max(entry.Bsp, bsp), Hulls.Max(entry.Bad, bad), entry.Actors);
            }

            if (colliders.Any(col => (col.Flags & ColliderFlags.BlockPlayers) != 0) && c.Wants(Floating) && Gap(map.Collision, colliders) is { } gap)
            {
                c.Report(new Finding(Severity.Warning, Floating, $"{id} ({Short(actor.Class)}) hangs {gap:0.#} units above the floor under it, touching nothing",
                    [ObjectRef.Actor(id)], actor.Location).WithFix(MoveTo(actor, actor.Location - new Vec3(0, 0, gap), $"Lower {id} {gap:0.#} units onto the floor")));
            }
        }

        foreach ((string mesh, (long tris, long bsp, Hulls bad, List<string> actors)) in overCap)
        {
            var what = new List<string>();
            if (tris > Limits.CollisionTrianglesPerMesh)
            {
                what.Add($"{tris} collision triangles (cap {Limits.CollisionTrianglesPerMesh}: past it the original game's trace table overflows" +
                    (tris > Limits.PerPolyCollisionTriangles ? $"; its importer keeps only {Limits.PerPolyCollisionTriangles})" : ")"));
            }

            if (bsp > Limits.CollisionBspNodes)
            {
                what.Add($"{bsp} collision BSP nodes (cap {Limits.CollisionBspNodes})");
            }

            if (bad.Faces > 0)
            {
                what.Add($"{bad.Faces} collision hulls with more faces than their corners allow (2V - 4), which the original game ignores");
            }

            if (bad.Corners > 0)
            {
                what.Add($"{bad.Corners} collision hulls of more than {Limits.MaxHullCorners} corners (up to {bad.MostCorners}), more than the original " +
                    "tools build: split them");
            }

            // The corner count is only the tools' cap, so on its own it stays a warning for the original game too.
            bool overGameCap = tris > Limits.CollisionTrianglesPerMesh || bsp > Limits.CollisionBspNodes || bad.Faces > 0;
            c.Report(overGameCap ? c.ForTarget(Severity.Warning) : Severity.Warning, MeshCollision, $"{mesh}: {string.Join(", ", what)}; used by {string.Join(", ", actors.Take(5))}" +
                (actors.Count > 5 ? $" and {actors.Count - 5} more" : ""), null, [.. actors.Select(ObjectRef.Actor)]);
        }
    }

    /// <summary>Hulls over the caps: how many have more planes than a convex hull of their corners can have faces
    /// (2V - 4, which the original game ignores), how many have more than <see cref="Limits.MaxHullCorners"/> corners (the
    /// tools' cap), and the most corners.</summary>
    private readonly record struct Hulls(int Faces, int Corners, int MostCorners)
    {
        public bool Any => Faces > 0 || Corners > 0;

        public static Hulls operator +(Hulls a, Hulls b) => new(a.Faces + b.Faces, a.Corners + b.Corners, Math.Max(a.MostCorners, b.MostCorners));

        public static Hulls Max(Hulls a, Hulls b) => new(Math.Max(a.Faces, b.Faces), Math.Max(a.Corners, b.Corners), Math.Max(a.MostCorners, b.MostCorners));
    }

    private static Hulls BadHulls(CollisionData data, in Collider col)
    {
        var bad = default(Hulls);
        foreach (CollisionHull hull in data.HullsOf(col))
        {
            int corners = (int)hull.VertCount;
            bool faces = corners >= 4 && hull.PlaneCount > Limits.MaxHullTriangles(corners);
            bool many = corners > Limits.MaxHullCorners;
            bad += new Hulls(faces ? 1 : 0, many ? 1 : 0, many ? corners : 0);
        }

        return bad;
    }

    /// <summary>
    /// The mesh colliders of the project's static mesh actors, by the actor that placed each one: its CSRC source (the
    /// actor id the compiler writes for every collider), else the actor named on it.
    /// </summary>
    private static Dictionary<string, List<Collider>> MeshColliders(CheckContext c, CollisionData data)
    {
        CollisionUnknownChunk? csrc = data.UnknownChunks.FirstOrDefault(k => k.FourCC == "CSRC" && k.Count == data.Colliders.Length);
        int stride = csrc is null || csrc.Count == 0 ? 0 : csrc.Payload.Length / (int)csrc.Count;
        var byActor = new Dictionary<string, List<Collider>>(StringComparer.Ordinal);
        for (int i = 0; i < data.Colliders.Length; i++)
        {
            Collider col = data.Colliders[i];
            if (col.Kind is not (ColliderKind.MeshPoly or ColliderKind.MeshHull))
            {
                continue;
            }

            string? source = stride >= 4 ? data.GetString(BinaryPrimitives.ReadUInt32LittleEndian(csrc!.Payload.AsSpan(i * stride))) : null;
            string? id = new[] { source, data.ActorOf(col) }.FirstOrDefault(s => s is { Length: > 0 } && c.Project.FindActor(s) is not null);
            if (id is null)
            {
                continue;
            }

            if (!byActor.TryGetValue(id, out List<Collider>? list))
            {
                byActor[id] = list = [];
            }

            list.Add(col);
        }

        return byActor;
    }

    private const ETraceFlags FloorFlags = ETraceFlags.TRACE_Level | ETraceFlags.TRACE_LevelGeometry | ETraceFlags.TRACE_Others | ETraceFlags.TRACE_Movers;

    /// <summary>
    /// How far a mesh's bottom hangs above the floor under it (<see cref="MinGap"/> to <see cref="FloatGap"/>), or null
    /// when it rests on something, is mounted (touches a wall, the ceiling or another mesh at its sides or top: a
    /// radiator, a sign, a window), hangs higher (a lamp) or has nothing under it. The floor is looked for under the
    /// middle and four inset corners of its bounds; the gap is the smallest found, measured to a hundredth, so lowering
    /// by it lands the mesh on the first thing it would touch there.
    /// </summary>
    private static float? Gap(CollisionWorld world, List<Collider> colliders)
    {
        FVector min = colliders[0].AabbMin, max = colliders[0].AabbMax;
        foreach (Collider col in colliders.Skip(1))
        {
            min = new FVector(MathF.Min(min.X, col.AabbMin.X), MathF.Min(min.Y, col.AabbMin.Y), MathF.Min(min.Z, col.AabbMin.Z));
            max = new FVector(MathF.Max(max.X, col.AabbMax.X), MathF.Max(max.Y, col.AabbMax.Y), MathF.Max(max.Z, col.AabbMax.Z));
        }

        var half = new FVector((max.X - min.X) * 0.5f, (max.Y - min.Y) * 0.5f, 0f);
        var bottom = new FVector((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, min.Z);

        // Anything just under the bottom (a ledge, a crate, another mesh) means it rests there.
        if (world.PointCheck(out _, null, bottom - new FVector(0f, 0f, 0.75f), new FVector(half.X * 0.95f, half.Y * 0.95f, 0.5f), FloorFlags) ||
            Mounted(world, min, max))
        {
            return null;
        }

        (float Gap, FVector Column)? best = null;
        foreach ((float fx, float fy) in new[] { (0f, 0f), (-0.5f, -0.5f), (0.5f, -0.5f), (-0.5f, 0.5f), (0.5f, 0.5f) })
        {
            FVector start = bottom + new FVector(half.X * fx, half.Y * fy, -0.25f);
            FVector end = start - new FVector(0f, 0f, FloatGap + 2f);
            if (world.LineCheck(out FCheckResult hit, null, end, start, default, FloorFlags))
            {
                // A line stops short of what it hits (half a unit on BSP, up to one on meshes): exact below.
                float d = start.Z - hit.Location.Z + 0.25f;
                if (best is not { } b || d < b.Gap)
                {
                    best = (d, start + new FVector(0f, 0f, 0.25f));
                }
            }
        }

        if (best is not { } found || found.Gap > FloatGap)
        {
            return null;
        }

        float gap = ExactGap(world, found.Column, found.Gap);
        return gap is >= MinGap and <= FloatGap ? gap : null;
    }

    /// <summary>
    /// True when the mesh touches what is round it: its bounds, grown 3 units out at the sides and top, cross a level
    /// surface (a wall or ceiling it is fixed to or set into), or slabs just outside its sides and top overlap another
    /// mesh. The bottom is left out, being where the gap is.
    /// </summary>
    private static bool Mounted(CollisionWorld world, FVector min, FVector max)
    {
        const float Reach = 3f;
        FVector centre = (min + max) * 0.5f;
        FVector half = (max - min) * 0.5f;
        var grown = new FVector(half.X + Reach, half.Y + Reach, half.Z + Reach * 0.5f);
        if (world.PointCheck(out _, null, centre + new FVector(0f, 0f, 1f + Reach * 0.5f), grown - new FVector(0f, 0f, 1f), ETraceFlags.TRACE_Level))
        {
            return true;
        }

        const ETraceFlags meshes = ETraceFlags.TRACE_LevelGeometry | ETraceFlags.TRACE_Others | ETraceFlags.TRACE_Movers;
        float slab = Reach * 0.5f;
        float sideZ = centre.Z + 0.5f, sideHalfZ = MathF.Max(half.Z - 0.5f, 0.5f);
        (FVector At, FVector Extent)[] slabs =
        [
            (new FVector(max.X + 0.25f + slab, centre.Y, sideZ), new FVector(slab, half.Y, sideHalfZ)),
            (new FVector(min.X - 0.25f - slab, centre.Y, sideZ), new FVector(slab, half.Y, sideHalfZ)),
            (new FVector(centre.X, max.Y + 0.25f + slab, sideZ), new FVector(half.X, slab, sideHalfZ)),
            (new FVector(centre.X, min.Y - 0.25f - slab, sideZ), new FVector(half.X, slab, sideHalfZ)),
            (new FVector(centre.X, centre.Y, max.Z + 0.25f + slab), new FVector(half.X, half.Y, slab)),
        ];
        return slabs.Any(s => world.PointCheck(out _, null, s.At, s.Extent, meshes));
    }

    /// <summary>
    /// The gap under <paramref name="bottom"/> to a hundredth: the drop at which a 2-unit box under it first crosses
    /// the floor, between the line's <paramref name="measured"/> gap (short by the line's pull-back of half a unit to
    /// one) and 1.25 more.
    /// </summary>
    private static float ExactGap(CollisionWorld world, FVector bottom, float measured)
    {
        bool Crosses(float drop) => world.PointCheck(out _, null, bottom - new FVector(0f, 0f, drop - 1f), new FVector(1f, 1f, 1f), FloorFlags);

        float clear = measured - 0.05f, touching = measured + 1.25f;
        if (Crosses(clear) || !Crosses(touching))
        {
            return measured;
        }

        while (touching - clear > 0.01f)
        {
            float mid = (clear + touching) * 0.5f;
            if (Crosses(mid))
            {
                touching = mid;
            }
            else
            {
                clear = mid;
            }
        }

        return MathF.Round(clear, 2);
    }

    /// <summary>A fix that moves an actor to a point (absolute, so applying it twice does no harm), current while the
    /// actor stands where the check saw it.</summary>
    private static FindingFix MoveTo(Actor a, Vec3 to, string label)
    {
        var doc = new SvJsonObject();
        doc.Add("format", SvJsonNode.String("osmap.changes/1"));
        var op = new SvJsonObject();
        op.Add("op", SvJsonNode.String("move"));
        op.Add("id", SvJsonNode.String(a.Id));
        op.Add("to", new SvJsonArray([Round(to.X), Round(to.Y), Round(to.Z)]));
        doc.Add("ops", new SvJsonArray([op]));
        return new FindingFix(label, doc) { Was = [(a.Id, a.Location)] };
    }

    private static SvJsonNode Round(double v) => SvJsonNode.Float((float)Math.Round(v, 2));

    private static string Short(string cls) => cls[(cls.LastIndexOf('.') + 1)..];
}
