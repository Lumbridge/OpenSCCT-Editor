using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Game.Svm;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>
/// "Drive selected meshes": a driver conveyor fitted to the top of placed mesh
/// actors, with their textures scrolling at its speed. This is AeroD's escalators and travelators (hidden RE+ belts
/// under the map's own meshes plus a scroll script with hand-worked rates) as one conveyor whose scroll rate follows
/// from its speed.
/// </summary>
public static class ConveyorDriver
{
    /// <summary>How far apart the meshes' top is sampled along the path.</summary>
    private const double SampleStep = 16;

    /// <summary>How far in from each end the top is sampled, so the sample lands on the meshes, not past their edge.</summary>
    private const double EndInset = 4;

    /// <summary>
    /// A sample this close (uu) to the line through its neighbours adds no point to the path. Near a pawn's step height
    /// (34), so a stepped mesh's treads give its overall slope, not a staircase.
    /// </summary>
    private const double Tolerance = 32;

    /// <summary>
    /// A rise or drop of more than this within <see cref="StepWindow"/> along the line is an edge of the top (an end
    /// lip, a wall, a plate at another height), not a tread or a slope: escalator steps rise up to
    /// <see cref="ConveyorLayout.StepRise"/> per 40 uu tread, and a 30 degree slope 18.5 in the window.
    /// </summary>
    private const double MaxStep = 24;

    /// <summary>The distance along the line over which a rise or drop is measured against <see cref="MaxStep"/>.</summary>
    private const double StepWindow = 32;

    /// <summary>
    /// Stretches of top at the same height this far apart or less (along the line) are one surface: the dip or bump
    /// between them (the plate between two belt meshes, a seam) is bridged.
    /// </summary>
    private const double BridgeGap = 64;

    /// <summary>How finely the top's width is measured across the line.</summary>
    private const double WidthStep = 8;

    /// <summary>Triangles steeper than this (the up part of their normal) are sides and ends, not the top.</summary>
    private const double MinUp = 0.5;

    /// <summary>
    /// A new driver conveyor (not yet added; its id is empty) over <paramref name="meshes"/>. Each mesh gives a run along
    /// its own longer side, down its middle (so the meshes may face different ways). The runs are chained end to nearest
    /// end; where two runs meet at an angle the path turns where their middles cross, so an L of travelator meshes gives
    /// an L. The path follows the meshes' top (their triangles) along that line, with a point wherever the slope
    /// changes, so an escalator mesh gives a landing, a slope and a landing, and it runs from the lower end to the
    /// higher one. It covers the longest stretch of top with no sudden rise or drop (<see cref="MaxStep"/>), joined
    /// across short dips and bumps to stretches at the same height (<see cref="BridgeGap"/>), so it ends where the
    /// meshes' belt does, not on an end lip or a lower plate past it. The width is the top's own width across the line
    /// (the middle value along it), at most the narrowest mesh's shorter side. Each mesh scrolls one texture repeat per
    /// <paramref name="unitsPerTile"/> uu of travel along texture axis <paramref name="axis"/> (0 U, 1 V).
    /// </summary>
    /// <param name="folder">The map's folder, for its own meshes.</param>
    /// <exception cref="ApiException">There are no meshes, or one has no mesh the content knows.</exception>
    public static Actor Fit(string? folder, IReadOnlyList<Actor> meshes, double unitsPerTile, byte axis)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        if (meshes.Count == 0)
        {
            throw new ApiException("give one or more placed mesh actors to drive", "drive");
        }

        if (unitsPerTile <= 0)
        {
            throw new ApiException("tile is the uu of travel one texture repeat covers, above 0", "tile");
        }

        var rules = new ActorRules(Compile.ClassDefaults.Create(EditorServices.ContentRoot, EditorServices.Classes));
        AssetResolver library = AssetResolver.For(EditorServices.ContentRoot, folder);
        var triangles = new List<(Vec3 A, Vec3 B, Vec3 C)>();
        var runs = new List<Run>();
        foreach (Actor a in meshes)
        {
            string? path = PropValues.AssetPath(rules.Value(a, "StaticMesh"));
            LibraryMesh mesh = (path is null ? null : library.FindMesh(path))
                               ?? throw new ApiException($"{a.Id} has no static mesh the content knows", "drive");
            (List<Vec3> positions, List<int> indices, _) = library.Glb(mesh.GlbPath).ReadUeTriangles(mesh.Mesh);
            List<Vec3> world = positions.Select(p => rules.LocalToWorld(a, p)).ToList();
            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                triangles.Add((world[indices[i]], world[indices[i + 1]], world[indices[i + 2]]));
            }

            if (world.Count > 0)
            {
                runs.Add(RunOf(world, a.Rotation.Yaw));
            }
        }

        if (triangles.Count == 0)
        {
            throw new ApiException("the meshes have no triangles to fit to", "drive");
        }

        double width = runs.Min(r => r.Width);
        List<(double S, Vec3 At)> samples = Samples(triangles, Corners(Chain(runs), width), out List<int> corners);
        if (samples.Count > 0)
        {
            List<int> kept = Surface(samples);
            // A corner whose sample is left out moves to the last kept one before it; a leg with no top of its own
            // repeats the corner before it.
            corners = corners.Select(k => kept.FindLastIndex(i => i <= k)).Where(k => k > 0 && k < kept.Count - 1).Distinct().ToList();
            samples = kept.Select(i => samples[i]).ToList();
        }

        if (samples.Count < 2)
        {
            throw new ApiException("the meshes have no top along their middle to fit a conveyor to", "drive");
        }

        if (samples[^1].At.Z < samples[0].At.Z)
        {
            double total = samples[^1].S;
            samples = samples.Select(p => (total - p.S, p.At)).Reverse().ToList();
            corners = corners.Select(k => samples.Count - 1 - k).Reverse().ToList();
        }

        // Each leg is simplified on its own, so the corners stay.
        var keep = new List<int> { 0 };
        int from = 0;
        foreach (int k in corners.Where(k => k > 0 && k < samples.Count - 1).Append(samples.Count - 1))
        {
            keep.AddRange(Simplify(samples, from, k).Skip(1));
            from = k;
        }

        List<ConveyorPoint> points = keep.Select(k => new ConveyorPoint(samples[k].At, 0)).ToList();
        var actor = new Actor("", ConveyorDesign.ClassName) { Location = points[0].At };
        actor.Props.Set("Path", ConveyorDesign.PathValue(actor, points));
        actor.Props.Set("Look", SvJsonNode.Integer((int)EConveyorLook.CL_Driver));
        actor.Props.Set("Width", SvJsonNode.Float((float)Math.Round(TopWidth(triangles, samples, width))));
        actor.Props.Set("ScrollTargets", ConveyorDesign.ScrollValue(meshes.Select(m => new FConveyorScroll
        {
            Target = m.Id, UnitsPerTile = (float)unitsPerTile, Axis = axis,
        })));
        return actor;
    }

    /// <summary>A mesh's run: the flat middle line along its longer side (in its own frame) and its shorter side.</summary>
    private readonly record struct Run(Vec3 Start, Vec3 End, double Width)
    {
        public Run Reversed => new(End, Start, Width);
    }

    /// <summary>The run of a mesh's world points, in the frame of its <paramref name="yaw"/>.</summary>
    private static Run RunOf(List<Vec3> points, int yaw)
    {
        (Vec3 forward, Vec3 right, _) = new Rot(0, yaw, 0).Axes();
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (Vec3 p in points)
        {
            double x = Vec3.Dot(p, forward), y = Vec3.Dot(p, right);
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        Vec3 At(double x, double y) => (forward * x + right * y) with { Z = 0 };
        return maxY - minY > maxX - minX
            ? new Run(At((minX + maxX) / 2, minY), At((minX + maxX) / 2, maxY), maxX - minX)
            : new Run(At(minX, (minY + maxY) / 2), At(maxX, (minY + maxY) / 2), maxY - minY);
    }

    /// <summary>
    /// The runs in order, each turned to start at its end nearest the previous run's end. The chain starts at an open
    /// end: the run end farthest from every other run's ends.
    /// </summary>
    private static List<Run> Chain(List<Run> runs)
    {
        var left = new List<Run>(runs);
        double Gap(Vec3 p, Run self) => left.Where(r => r != self).Select(r => Math.Min(Flat(r.Start - p), Flat(r.End - p)))
            .DefaultIfEmpty(double.MaxValue).Min();
        Run first = left.OrderByDescending(r => Math.Max(Gap(r.Start, r), Gap(r.End, r))).First();
        var chain = new List<Run> { Gap(first.Start, first) >= Gap(first.End, first) ? first : first.Reversed };
        left.Remove(first);
        while (left.Count > 0)
        {
            Vec3 end = chain[^1].End;
            Run next = left.OrderBy(r => Math.Min(Flat(r.Start - end), Flat(r.End - end))).First();
            left.Remove(next);
            chain.Add(Flat(next.Start - end) <= Flat(next.End - end) ? next : next.Reversed);
        }

        return chain;
    }

    /// <summary>
    /// The flat line through the chained runs. Where two runs at an angle meet, one corner where their middle lines
    /// cross (when that is within <paramref name="reach"/> of both their ends); else straight on from one run's end to
    /// the next run's start.
    /// </summary>
    private static List<Vec3> Corners(List<Run> chain, double reach)
    {
        var line = new List<Vec3> { chain[0].Start };
        for (int i = 0; i + 1 < chain.Count; i++)
        {
            Run a = chain[i], b = chain[i + 1];
            if (Crossing(a, b) is { } corner && Flat(corner - a.End) <= reach && Flat(corner - b.Start) <= reach)
            {
                line.Add(corner);
            }
            else
            {
                line.Add(a.End);
                line.Add(b.Start);
            }
        }

        line.Add(chain[^1].End);
        return line.Where((p, i) => i == 0 || Flat(p - line[i - 1]) > 1).ToList();
    }

    /// <summary>Where the middle lines of two runs cross (flat), or null when they are close to parallel.</summary>
    private static Vec3? Crossing(Run a, Run b)
    {
        Vec3 da = a.End - a.Start, db = b.End - b.Start;
        double denominator = da.X * db.Y - da.Y * db.X;
        if (Math.Abs(denominator) < 0.1 * Flat(da) * Flat(db))
        {
            return null;
        }

        Vec3 w = b.Start - a.Start;
        return a.Start + da * ((w.X * db.Y - w.Y * db.X) / denominator);
    }

    /// <summary>
    /// The meshes' top along the flat line, every <see cref="SampleStep"/> or less on each leg: the distance along the
    /// line and the point on the top. <paramref name="corners"/> gets the index of the last sample of each leg but the
    /// last (the line's corners).
    /// </summary>
    private static List<(double S, Vec3 At)> Samples(List<(Vec3 A, Vec3 B, Vec3 C)> triangles, List<Vec3> line, out List<int> corners)
    {
        var samples = new List<(double S, Vec3 At)>();
        corners = [];
        double along = 0;
        for (int leg = 0; leg + 1 < line.Count; leg++)
        {
            Vec3 a = line[leg], b = line[leg + 1];
            double length = Flat(b - a);
            Vec3 dir = (b - a) * (1 / length);
            int n = Math.Max(1, (int)Math.Ceiling(length / SampleStep));
            for (int i = leg == 0 ? 0 : 1; i <= n; i++)
            {
                double s = length * i / n;
                // Inset at the line's two ends, so the probe lands on the meshes, not past their edge.
                double probe = Math.Clamp(s, leg == 0 ? EndInset : 0, leg == line.Count - 2 ? length - EndInset : length);
                Vec3 at = a + dir * probe;
                if (Top(triangles, at.X, at.Y) is { } z)
                {
                    samples.Add((along + s, (a + dir * s) with { Z = z }));
                }
            }

            along += length;
            if (leg + 2 < line.Count && samples.Count > 0)
            {
                corners.Add(samples.Count - 1);
            }
        }

        return samples;
    }

    /// <summary>
    /// The samples on the riding surface, in order. The line is cut wherever the top rises or drops more than
    /// <see cref="MaxStep"/> within <see cref="StepWindow"/>; the longest stretch is the surface, and it takes in each
    /// stretch beyond either end that starts within <see cref="BridgeGap"/> of it at about its height, leaving out what
    /// lies between.
    /// </summary>
    private static List<int> Surface(List<(double S, Vec3 At)> samples)
    {
        var stretches = new List<(int First, int Last)>();
        int from = 0;
        for (int i = 1; i <= samples.Count; i++)
        {
            if (i < samples.Count && !Steps(samples, i))
            {
                continue;
            }

            stretches.Add((from, i - 1));
            from = i;
        }

        double Length((int First, int Last) r) => samples[r.Last].S - samples[r.First].S;
        int main = stretches.IndexOf(stretches.MaxBy(Length));
        var chosen = new List<int> { main };
        for (int k = main, next; (next = Bridged(samples, stretches, k, +1)) >= 0; k = next)
        {
            chosen.Add(next);
        }

        for (int k = main, next; (next = Bridged(samples, stretches, k, -1)) >= 0; k = next)
        {
            chosen.Insert(0, next);
        }

        return chosen.SelectMany(k => Enumerable.Range(stretches[k].First, stretches[k].Last - stretches[k].First + 1)).ToList();
    }

    /// <summary>Whether the top rises or drops more than <see cref="MaxStep"/> from the samples within <see cref="StepWindow"/> before sample <paramref name="i"/>.</summary>
    private static bool Steps(List<(double S, Vec3 At)> samples, int i)
    {
        for (int j = i - 1; j >= 0 && samples[i].S - samples[j].S <= StepWindow; j--)
        {
            if (Math.Abs(samples[i].At.Z - samples[j].At.Z) > MaxStep)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The first stretch past stretch <paramref name="k"/> the way <paramref name="direction"/> goes (+1 on along the
    /// line, -1 back) whose near end is within <see cref="BridgeGap"/> of <paramref name="k"/>'s and at about its
    /// height, or -1. The next stretch is never it: a rise or drop is what ended <paramref name="k"/> there.
    /// </summary>
    private static int Bridged(List<(double S, Vec3 At)> samples, List<(int First, int Last)> stretches, int k, int direction)
    {
        (double S, Vec3 At) end = direction > 0 ? samples[stretches[k].Last] : samples[stretches[k].First];
        for (int n = k + 2 * direction; n >= 0 && n < stretches.Count; n += direction)
        {
            (double S, Vec3 At) near = direction > 0 ? samples[stretches[n].First] : samples[stretches[n].Last];
            if (Math.Abs(near.S - end.S) > BridgeGap)
            {
                return -1;
            }

            if (Math.Abs(near.At.Z - end.At.Z) <= MaxStep)
            {
                return n;
            }
        }

        return -1;
    }

    /// <summary>
    /// How wide the top is across the line: at each sample, the stretch of top within <see cref="MaxStep"/> of its
    /// height either side of it, unbroken, at most <paramref name="widest"/>; the middle value along the line.
    /// </summary>
    private static double TopWidth(List<(Vec3 A, Vec3 B, Vec3 C)> triangles, List<(double S, Vec3 At)> samples, double widest)
    {
        var widths = new List<double>();
        for (int i = 0; i < samples.Count; i++)
        {
            Vec3 along = samples[Math.Min(i + 1, samples.Count - 1)].At - samples[Math.Max(i - 1, 0)].At;
            if (Flat(along) < 1e-6)
            {
                continue;
            }

            Vec3 at = samples[i].At, across = new Vec3(-along.Y, along.X, 0) * (1 / Flat(along));
            double Reach(double sign)
            {
                double reach = 0;
                for (double d = WidthStep; d <= widest / 2; d += WidthStep)
                {
                    Vec3 p = at + across * (sign * d);
                    if (Top(triangles, p.X, p.Y) is not { } z || Math.Abs(z - at.Z) > MaxStep)
                    {
                        break;
                    }

                    reach = d;
                }

                return reach;
            }

            widths.Add(Reach(-1) + Reach(1));
        }

        if (widths.Count == 0)
        {
            return widest;
        }

        widths.Sort();
        double middle = widths[widths.Count / 2];
        return middle >= WidthStep ? Math.Min(middle, widest) : widest;
    }

    /// <summary>The height of the highest top-facing triangle over (<paramref name="x"/>, <paramref name="y"/>), or null.</summary>
    private static double? Top(List<(Vec3 A, Vec3 B, Vec3 C)> triangles, double x, double y)
    {
        double? top = null;
        foreach ((Vec3 a, Vec3 b, Vec3 c) in triangles)
        {
            double d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            Vec3 normal = Vec3.Cross(b - a, c - a);
            if (Math.Abs(d) < 1e-6 || Math.Abs(normal.Z) < MinUp * normal.Length)
            {
                continue;
            }

            double u = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
            double v = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
            if (u < 0 || v < 0 || u + v > 1)
            {
                continue;
            }

            double z = u * a.Z + v * b.Z + (1 - u - v) * c.Z;
            top = top is { } t ? Math.Max(t, z) : z;
        }

        return top;
    }

    /// <summary>
    /// The samples to keep from <paramref name="first"/> to <paramref name="last"/>: Douglas-Peucker on along and height,
    /// so a point stays where the slope changes.
    /// </summary>
    private static List<int> Simplify(List<(double S, Vec3 At)> samples, int first, int last)
    {
        (double s0, Vec3 p0) = samples[first];
        (double s1, Vec3 p1) = samples[last];
        int worst = -1;
        double worstOff = Tolerance;
        for (int i = first + 1; i < last; i++)
        {
            double t = s1 > s0 ? (samples[i].S - s0) / (s1 - s0) : 0;
            double off = Math.Abs(samples[i].At.Z - (p0.Z + (p1.Z - p0.Z) * t));
            if (off > worstOff)
            {
                worst = i;
                worstOff = off;
            }
        }

        if (worst < 0)
        {
            return [first, last];
        }

        return [.. Simplify(samples, first, worst)[..^1], .. Simplify(samples, worst, last)];
    }

    private static double Flat(Vec3 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);
}
