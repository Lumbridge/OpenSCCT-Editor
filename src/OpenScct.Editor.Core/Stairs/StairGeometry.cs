using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Stairs;

/// <summary>One generator for the editable preview, compiled world and collision envelopes.</summary>
public static class StairGeometry
{
    // Coplanar subtraction caps inherit the cutter's material/UVs. Keep clearance above the
    // support plane so placing stairs on an existing floor cannot repaint the approach.
    private const double ClearanceFloorGap = .05;
    public const string Tread = "tread", Riser = "riser", Side = "side";
    public sealed record Step(Vec3 A, Vec3 B, Vec3 C, Vec3 D, double Bottom, double Top, bool Landing = false);
    public sealed record Generated(List<Brush> Solids, List<Brush> Cuts, List<Actor> Smoothing, Vec3 Top);

    public static IReadOnlyList<Step> Steps(StairDesign s)
    {
        s.Validate();
        var result = new List<Step>();
        int n = s.Count, n1 = n / 2; double h = s.Rise / n, d = s.Run / n, w = s.Width;
        double sign = s.Left ? -1 : 1;
        Vec3 P(double x, double y) => new(x, y * sign, 0);
        void Rect(double x, double y, double length, double width, int i, int direction, bool landing = false)
        {
            Vec3 origin = P(x, y), along = P(direction == 0 ? 1 : direction == 2 ? -1 : 0, direction == 1 ? 1 : 0);
            Vec3 across = new(-along.Y, along.X, 0); if (s.Left) across = -across;
            result.Add(new Step(origin - across * width / 2, origin + across * width / 2,
                origin + along * length + across * width / 2, origin + along * length - across * width / 2,
                i * h, landing ? i * h : (i + 1) * h, landing));
        }
        for (int i = 0; i < n; i++)
        {
            if (s.Layout is "curved" or "spiral")
            {
                double a = i * s.TurnDegrees / n * Math.PI / 180;
                double b = (i + 1) * s.TurnDegrees / n * Math.PI / 180;
                Vec3 Ring(double radius, double angle) => new(radius * Math.Sin(angle), sign * (s.InnerRadius + w / 2 - radius * Math.Cos(angle)), 0);
                result.Add(new Step(Ring(s.InnerRadius, a), Ring(s.InnerRadius + w, a), Ring(s.InnerRadius + w, b), Ring(s.InnerRadius, b), i * h, (i + 1) * h));
            }
            else if (s.Layout == "straight" || i < n1) Rect(i * d, 0, d, w, i, 0);
            else
            {
                if (i == n1) Rect(n1 * d, s.Layout == "u" ? w / 2 : 0, s.LandingLength, s.Layout == "u" ? 2 * w : w, i, 0, true);
                int j = i - n1;
                if (s.Layout == "l") Rect(n1 * d + s.LandingLength - w / 2, w / 2 + j * d, d, w, i, 1);
                else Rect(n1 * d - j * d, w, d, w, i, 2);
            }
        }
        return result;
    }

    public static Generated Generate(Brush owner)
    {
        StairDesign s = owner.Stair ?? throw new ArgumentException("Not an editable staircase");
        var solids = new List<Brush>(); var cuts = new List<Brush>(); var smooth = new List<Actor>();
        var steps = Steps(s); int id = 0; Step? previous = null;
        Brush Make(List<List<Vec3>> polygons, string suffix, CsgOper op)
        {
            var b = new Brush(owner.Id + suffix) { Oper = op, Location = owner.Location, Rotation = owner.Rotation,
                MainScale = owner.MainScale, PostScale = owner.PostScale, PrePivot = owner.PrePivot, Editor = owner.Editor.Clone() };
            b.Shape.Faces = polygons.Select(p => FaceKit.Make(s.SideMaterial, p)).ToList(); return b;
        }
        foreach (Step t in steps)
        {
            string suffix = "_Step" + id++;
            Vec3 forward = ((t.D + t.C - t.A - t.B) / 2).Normalized();
            double bottom = s.Layout == "spiral" ? t.Bottom - s.TreadThickness : 0;
            if (s.Layout == "spiral" && s.LegacyOpenRisers) bottom = Math.Max(0, t.Top - s.TreadThickness);
            if (s.ClampUndersideToFloor) bottom = Math.Max(0, bottom);
            var basePoints = new[] { t.A, t.B, t.C, t.D };
            double bodyTop = Math.Max(bottom, t.Top - s.TreadThickness);
            var bodyPoints = basePoints.SelectMany((p, i) => new[] { p with { Z = s.Layout == "spiral" && s.SlopedUnderside && i >= 2 ? bodyTop : bottom }, p with { Z = bodyTop } });
            Brush body = Make(ConvexSolid.Hull(bodyPoints), suffix + "Body", CsgOper.Add);
            foreach (BrushFace f in body.Shape.Faces) SetRole(f, Vec3.Dot(f.Normal(), -forward) > .5 ? Riser : Side, s);
            if (body.Shape.Faces.Count > 0) solids.Add(body);
            // A convex extrusion of a rounded front profile. The profile is identical at the two sides,
            // including radial stair treads, and contains no smoothing-volume geometry.
            var capPoints = new List<Vec3>();
            double low = s.Layout == "spiral" && !s.ClampUndersideToFloor ? t.Top - s.TreadThickness : Math.Max(0, t.Top - s.TreadThickness), r = s.EdgeRadius;
            double frontLow = s.Layout == "spiral" && s.LegacyOpenRisers && s.SlopedUnderside ? low - s.Rise / s.Count : low;
            foreach (Vec3 front in new[] { t.A, t.B })
            {
                if (r == 0)
                { capPoints.Add(front - forward * s.Overhang + Vec3.UnitZ * frontLow); capPoints.Add(front - forward * s.Overhang + Vec3.UnitZ * t.Top); }
                else for (int k = 0; k <= 4; k++)
                {
                    double a = k * Math.PI / 8;
                    Vec3 p = front + forward * (-s.Overhang + r - r * Math.Cos(a));
                    capPoints.Add(p + Vec3.UnitZ * (t.Top - r + r * Math.Sin(a)));
                    capPoints.Add(p + Vec3.UnitZ * (frontLow + r - r * Math.Sin(a)));
                }
            }
            capPoints.AddRange(new[] { t.C with { Z = low }, t.C with { Z = t.Top }, t.D with { Z = low }, t.D with { Z = t.Top } });
            Brush cap = Make(ConvexSolid.Hull(capPoints), suffix + "Tread", CsgOper.Add);
            foreach (BrushFace f in cap.Shape.Faces) SetRole(f, f.Normal().Z > .5 || Vec3.Dot(f.Normal(), -forward) > .1 ? Tread : Side, s);
            solids.Add(cap);
            if (s.Carve && s.Layout is "curved" or "spiral")
            {
                double stepRise = s.Rise / s.Count;
                double Ceiling(Step step) => step.Top + s.Headroom + (step.Landing ? stepRise : 0);
                double frontCeiling = Ceiling(t), backCeiling = id < steps.Count ? Ceiling(steps[id]) : frontCeiling;
                Vec3 frontAcross = (t.B - t.A).Normalized() * s.ClearanceMargin;
                Vec3 backAcross = (t.C - t.D).Normalized() * s.ClearanceMargin;
                Vec3 lead = -forward * (id == 1 ? s.ClearanceStart : .05);
                Vec3 tail = forward * (id == steps.Count ? s.ClearanceEnd : .05);
                Vec3[] outline = [t.A - frontAcross + lead, t.B + frontAcross + lead, t.C + backAcross + tail, t.D - backAcross + tail];
                cuts.Add(Make(ConvexSolid.Hull(outline.SelectMany((p, i) => new[] { p with { Z = t.Bottom + ClearanceFloorGap }, p with { Z = i < 2 ? frontCeiling : backCeiling } })), suffix + "Clearance", CsgOper.Subtract));
            }
            if (s.Smooth && !t.Landing)
            {
                // The walking envelope passes through tread nosings; its final horizontal section reaches
                // the landing exactly, and the first lead-in is included in the clearance preview.
                (Vec3 A, Vec3 B) Nose(Step step)
                {
                    Vec3 f = ((step.C + step.D - step.A - step.B) / 2).Normalized();
                    double run = Math.Max(1, Vec3.Dot((step.C + step.D - step.A - step.B) / 2, f));
                    double slope = (step.Top - step.Bottom) / run;
                    double angle = Math.Atan2(1, slope);
                    double dx = -s.Overhang + s.EdgeRadius * (1 - Math.Cos(angle));
                    double dz = -s.EdgeRadius + s.EdgeRadius * Math.Sin(angle);
                    return (step.A + f * dx + Vec3.UnitZ * (step.Top + dz), step.B + f * dx + Vec3.UnitZ * (step.Top + dz));
                }
                var nose = Nose(t);
                Vec3 a, b;
                if (previous is { Landing: false } prev) (a, b) = Nose(prev);
                else
                {
                    a = nose.A - (t.D - t.A) - Vec3.UnitZ * (t.Top - t.Bottom);
                    b = nose.B - (t.C - t.B) - Vec3.UnitZ * (t.Top - t.Bottom);
                }
                double floor = Math.Min(Math.Min(a.Z, b.Z), Math.Min(nose.A.Z, nose.B.Z)) - 2;
                var pts = new[] { a, b, nose.A, nose.B,
                    a with { Z = floor }, b with { Z = floor }, nose.A with { Z = floor }, nose.B with { Z = floor } };
                Brush ramp = Make(ConvexSolid.Hull(pts), suffix + "Smooth", CsgOper.Add);
                var volume = new Actor(ramp.Id, "Engine.BlockingVolume") { Shape = new BrushShape { Faces = ramp.WorldFaces().ToList() } };
                foreach (string key in new[] { "bCollideActors", "bBlockPlayers", "bBlockNonZeroExtentTraces", "bOnlyAffectPawns", "bHidden", "bWorldGeometry" }) volume.Props.Set(key, SvJsonNode.Bool(true));
                foreach (string key in new[] { "bBlockActors", "bBlockZeroExtentTraces", "bBlockCamera", "bShadowCast", "bProjTarget" }) volume.Props.Set(key, SvJsonNode.Bool(false));
                smooth.Add(volume);
                if (s.Carve && previous is null)
                    cuts.Add(Make(ConvexSolid.Hull(new[] { a, b, nose.A, nose.B }.SelectMany(p => new[] { p with { Z = ClearanceFloorGap }, p with { Z = t.Top + s.Headroom } })), suffix + "EntranceClearance", CsgOper.Subtract));
            }
            previous = t;
        }
        if (s.Carve && s.Layout is not ("curved" or "spiral"))
        {
            void Cut(Vec3 a, Vec3 b, Vec3 c, Vec3 d, double front, double back, double bottom, string name, bool first, bool last)
            {
                Vec3 forward = (c + d - a - b).Normalized(), across = (b - a).Normalized() * s.ClearanceMargin;
                Vec3 lead = -forward * (first ? s.ClearanceStart : .05), tail = forward * (last ? s.ClearanceEnd : .05);
                // Extend along the same roof plane so adjacent cuts overlap without a ceiling seam.
                double length = ((c + d - a - b) / 2).Length;
                double slope = length > 1e-6 ? (back - front) / length : 0;
                Vec3[] outline = [a-across+lead, b+across+lead, c+across+tail, d-across+tail];
                double f = front - slope * lead.Length, r = back + slope * tail.Length;
                cuts.Add(Make(ConvexSolid.Hull(outline.SelectMany((p, i) => new[] { p with { Z = bottom + ClearanceFloorGap }, p with { Z = i < 2 ? f : r } })), name, CsgOper.Subtract));
            }
            int index = 0, flight = 0;
            while (index < steps.Count)
            {
                Step first = steps[index];
                if (first.Landing)
                {
                    double roof = first.Top + s.Headroom + s.Rise / s.Count;
                    Cut(first.A, first.B, first.C, first.D, roof, roof, first.Bottom, "_LandingClearance", false, false);
                    index++; continue;
                }
                int start = index;
                while (index + 1 < steps.Count && !steps[index + 1].Landing) index++;
                Step last = steps[index]; bool ending = index == steps.Count - 1;
                if (ending && index > start)
                {
                    Cut(first.A, first.B, last.B, last.A, first.Top+s.Headroom, last.Top+s.Headroom, first.Bottom, "_FlightClearance"+flight, start == 0, false);
                    Cut(last.A, last.B, last.C, last.D, last.Top+s.Headroom, last.Top+s.Headroom, last.Bottom, "_ExitClearance", false, true);
                }
                else
                {
                    double back = last.Top + s.Headroom + (ending ? 0 : s.Rise / s.Count);
                    Cut(first.A, first.B, last.C, last.D, first.Top+s.Headroom, back, first.Bottom, "_FlightClearance"+flight, start == 0, ending);
                }
                flight++; index++;
            }
        }
        if (s.Layout == "spiral")
        {
            Vec3 centre = new(0, (s.Left ? -1 : 1) * (s.InnerRadius + s.Width / 2), 0);
            List<List<Vec3>> Cylinder(int sides, double radius, double height, double bottom = 0) => ConvexSolid.Prism(Geometry.Builders.Ring.Points(sides, radius, true), bottom, height)
                .Select(face => face.Select(v => v + centre).ToList()).ToList();
            if (s.CentralColumnRadius > 0)
            {
                Brush column = Make(Cylinder(12, s.CentralColumnRadius, s.Rise), "_Column", CsgOper.Add);
                foreach (BrushFace face in column.Shape.Faces) SetRole(face, Side, s);
                solids.Add(column);
            }
            if (s.Carve && s.RoundStairwell) cuts.Insert(0, Make(Cylinder(16, s.InnerRadius + s.Width + 16, s.Rise + s.Headroom, ClearanceFloorGap), "_Stairwell", CsgOper.Subtract));
        }
        Step finalStep = steps[^1];
        return new Generated(solids, cuts, smooth, owner.LocalToWorld((finalStep.C + finalStep.D) / 2 + Vec3.UnitZ * s.Rise));
    }

    public static void SetRole(BrushFace f, string role, StairDesign s)
    {
        f.StairRole = role;
        f.Material = role == Tread ? s.TreadMaterial : role == Riser ? s.RiserMaterial : s.SideMaterial;
        double scale = role == Tread ? s.TreadTextureScale : role == Riser ? s.RiserTextureScale : s.SideTextureScale;
        double rotation = (role == Tread ? s.TreadTextureRotation : role == Riser ? s.RiserTextureRotation : s.SideTextureRotation) * Math.PI / 180;
        Vec3 u = f.TextureU, v = f.TextureV;
        f.TextureU = (u * Math.Cos(rotation) - v * Math.Sin(rotation)) / scale;
        f.TextureV = (u * Math.Sin(rotation) + v * Math.Cos(rotation)) / scale;
        f.PanU = (int)Math.Round(role == Tread ? s.TreadPanU : role == Riser ? s.RiserPanU : s.SidePanU);
        f.PanV = (int)Math.Round(role == Tread ? s.TreadPanV : role == Riser ? s.RiserPanV : s.SidePanV);
    }

    public static Brush Create(string id, StairDesign design, Vec3 at, Rot rotation = default)
    {
        var owner = new Brush(id) { Stair = design.Clone(), Oper = CsgOper.Add, Location = at, Rotation = rotation };
        Rebuild(owner); return owner;
    }

    public static void Rebuild(Brush owner)
    {
        Generated g = Generate(owner);
        // Union locally for an exact, single selectable preview without internal step faces.
        foreach (Brush b in g.Solids) { b.Location = Vec3.Zero; b.Rotation = Rot.Zero; b.MainScale = b.PostScale = Vec3.One; b.PrePivot = Vec3.Zero; }
        var world = WorldModel.Build(g.Solids, [], false, null);
        var source = g.Solids.ToDictionary(b => b.Id);
        owner.Shape = new BrushShape { Faces = world.Visible.Select(p => {
            BrushFace f = source[p.Surface.BrushId].Shape.Faces[p.Surface.FaceIndex].Clone();
            f.Vertices = p.Verts.ToList(); return f;
        }).ToList() };
    }

    public static IEnumerable<Brush> Expand(IEnumerable<Brush> brushes)
    {
        foreach (Brush b in brushes)
            if (b.Stair is null) yield return b;
            else { var g = Generate(b); foreach (Brush c in g.Cuts.Concat(g.Solids)) yield return c; }
    }

    public static MapProject Lower(MapProject p)
    {
        if (!p.Brushes.Any(b => b.Stair is not null)) return p;
        var copy = Session.ProjectSnapshot.Take(p);
        copy.Brushes = Expand(p.Brushes).Select(b => b.Clone()).ToList();
        foreach (Brush b in p.Brushes.Where(b => b.Stair is not null)) copy.Actors.AddRange(Generate(b).Smoothing);
        return copy;
    }
}
