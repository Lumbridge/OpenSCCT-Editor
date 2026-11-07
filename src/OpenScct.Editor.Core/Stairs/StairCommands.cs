using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Stairs;

public static class StairCommands
{
    public static string NewId(MapProject p)
    {
        for (int i = 0; ; i++)
        {
            string id = "Stair" + i;
            if (p.Find(id) is null && !p.AllRefs().Any(r => r.Id.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase))) return id;
        }
    }
    public static AddObjectsCommand Place(MapProject p, StairDesign design, Vec3 at, Rot rotation = default)
    {
        if (design.Carve && Compile.BakedContent.BakedWorldKept(p))
            throw new ArgumentException("This map retains baked geometry; disable clearance cutting before placing stairs");
        return new AddObjectsCommand("Place staircase", brushes: [StairGeometry.Create(NewId(p), design, at, rotation)]);
    }

    public static ReplaceObjectsCommand Edit(MapProject p, string id, Action<StairDesign> change, string? mergeKey = null)
    {
        Brush before = p.FindBrush(id) ?? throw new ArgumentException("Staircase no longer exists");
        Brush after = before.Clone();
        change(after.Stair ?? throw new ArgumentException("Select an editable staircase"));
        if (after.Stair!.Carve && Compile.BakedContent.BakedWorldKept(p))
            throw new ArgumentException("Disable clearance cutting for a retained baked world");
        StairGeometry.Rebuild(after);
        return new ReplaceObjectsCommand("Edit staircase", mergeKey).Brush(before.Clone(), after);
    }

    public static ReplaceObjectsCommand Material(MapProject p, string id, string role, string material) => Edit(p, id, s =>
    {
        if (role == StairGeometry.Tread) s.TreadMaterial = material;
        else if (role == StairGeometry.Riser) s.RiserMaterial = material;
        else s.SideMaterial = material;
    });

    public static IEditCommand BreakApart(MapProject p, string id)
    {
        Brush owner = p.FindBrush(id) ?? throw new ArgumentException("Select stairs");
        var g = StairGeometry.Generate(owner);
        return new StairReplacement("Break apart staircase", p.Brushes.IndexOf(owner), [owner.Clone()], g.Cuts.Concat(g.Solids).ToList(), g.Smoothing);
    }

    public static IEditCommand Convert(MapProject p, string id)
    {
        Brush hit = p.FindBrush(id) ?? throw new ArgumentException("Select generated stair brushes");
        BuilderSpec spec = hit.Shape.Builder ?? throw new ArgumentException("This brush has no stair builder metadata");
        if (spec.Kind is not ("stairs" or "linearStair" or "curvedStair" or "spiralStair")) throw new ArgumentException("Select generated stair brushes");
        var old = PiecePlacer.PieceOf(p, hit);
        if (old.Count == 1)
        {
            bool SameSpec(Brush b) => b.Shape.Builder is { } other && other.Kind == spec.Kind
                && other.Numbers.Where(k => k.Key != "part").OrderBy(k => k.Key).SequenceEqual(spec.Numbers.Where(k => k.Key != "part").OrderBy(k => k.Key))
                && other.Strings.Where(k => k.Key != "piece").OrderBy(k => k.Key).SequenceEqual(spec.Strings.Where(k => k.Key != "piece").OrderBy(k => k.Key))
                && b.Location == hit.Location && b.Rotation == hit.Rotation;
            old = p.Brushes.Where(SameSpec).OrderBy(b => b.Shape.Builder!.Get("part", 0)).ToList();
        }
        var expected = BuilderCatalog.Get(spec.Kind).Build(spec);
        if (old.Count != expected.Count) throw new ArgumentException("These stair parts are unlinked or incomplete; conversion cannot safely identify the complete staircase");
        for (int i = 0; i < old.Count; i++)
        {
            if (old[i].Location != hit.Location || old[i].Rotation != hit.Rotation || old[i].MainScale != hit.MainScale || old[i].PostScale != hit.PostScale
                || old[i].PrePivot != hit.PrePivot || old[i].PolyFlags != 0
                || old[i].Shape.Faces.Count != expected[i].Shape.Faces.Count
                || !old[i].Shape.Faces.Zip(expected[i].Shape.Faces).All(pair => pair.First.Vertices.SequenceEqual(pair.Second.Vertices) && pair.First.Flags == pair.Second.Flags && pair.First.TextureU == pair.Second.TextureU && pair.First.TextureV == pair.Second.TextureV && pair.First.PanU == pair.Second.PanU && pair.First.PanV == pair.Second.PanV))
                throw new ArgumentException("Modified stair geometry cannot be upgraded automatically; keep it as brushes");
        }
        if (spec.Get("addToFirstStep", 0) != 0 || spec.Get("slopedFloor", 0) != 0)
            throw new ArgumentException("Raised-base or sloped-floor legacy stairs cannot be converted without changing their shape");
        var s = new StairDesign { Smooth = true, Carve = spec.Kind == "stairs" && spec.Strings.GetValueOrDefault("mode", "carve") == "carve", Overhang = 0, EdgeRadius = 0 };
        int count = (int)spec.Get("numSteps", Math.Ceiling(spec.Get("rise", 272) / spec.Get("stepHeight", 16)));
        s.StepCount = count; s.TargetStepHeight = spec.Get("stepHeight", 16); s.Rise = spec.Get("rise", count * s.TargetStepHeight);
        s.Run = spec.Get("stepLength", 32) * count; s.Width = spec.Get("width", spec.Get("stepWidth", 192)); s.LandingLength = s.Width;
        s.Layout = spec.Kind == "curvedStair" ? "curved" : spec.Kind == "spiralStair" ? "spiral" : spec.Strings.GetValueOrDefault("layout", "straight");
        s.InnerRadius = spec.Get("innerRadius", spec.Get("wellRadius", 96));
        s.TurnDegrees = s.Layout == "spiral" ? 360 * count / spec.Get("numStepsPer360", spec.Get("stepsPerTurn", 16)) : spec.Get("angleOfCurve", 90);
        s.TreadThickness = spec.Kind == "spiralStair" || s.Layout == "spiral" ? spec.Get("stepThickness", 32) : Math.Min(8, s.Rise / count);
        s.ClampUndersideToFloor = spec.Kind == "stairs";
        s.RoundStairwell = spec.Kind == "stairs" && s.Layout == "spiral";
        s.CentralColumnRadius = s.RoundStairwell ? spec.Get("wellRadius", 48) : 0;
        s.SlopedUnderside = spec.Kind == "spiralStair" && spec.Get("slopedCeiling", 1) != 0;
        s.Headroom = spec.Get("headroom", 224);
        s.Left = spec.Strings.GetValueOrDefault("turn") == "left" || spec.Get("counterClockwise", 0) != 0;
        var topMaterials = old.Where(b => b.Oper == CsgOper.Add).SelectMany(b => b.Shape.Faces).Where(f => f.Normal().Z > .99).Select(f => f.Material ?? "Editor.Grid").Distinct().ToList();
        var otherMaterials = old.Where(b => b.Oper == CsgOper.Add).SelectMany(b => b.Shape.Faces).Where(f => f.Normal().Z <= .99).Select(f => f.Material ?? "Editor.Grid").Distinct().ToList();
        if (topMaterials.Count > 1 || otherMaterials.Count > 1) throw new ArgumentException("This staircase has individual face materials; conversion would lose those overrides, so it has been left unchanged");
        s.TreadMaterial = topMaterials.FirstOrDefault() ?? "Editor.Grid";
        s.SideMaterial = s.RiserMaterial = otherMaterials.FirstOrDefault() ?? s.TreadMaterial;
        Vec3 origin;
        Rot rotation = hit.Rotation;
        Vec3 scale = hit.MainScale;
        if (s.Layout is "curved" or "spiral")
        {
            origin = new Vec3(s.InnerRadius + s.Width / 2, 0, 0);
            if (rotation.Pitch != 0 || rotation.Roll != 0) throw new ArgumentException("Level the tilted legacy staircase before conversion");
            rotation = rotation with { Yaw = rotation.Yaw + (s.Left ? -1 : 1) * Rot.UnitsPerTurn / 4 };
            scale = new Vec3(hit.MainScale.Y, hit.MainScale.X, hit.MainScale.Z);
        }
        else
        {
            var vertices = old.Where(b => b.Oper == CsgOper.Add).OrderBy(b => b.Shape.Faces.SelectMany(f => f.Vertices).Max(v => v.Z)).First().Shape.Faces.SelectMany(f => f.Vertices).ToList();
            origin = new Vec3(vertices.Min(v => v.X), (vertices.Min(v => v.Y) + vertices.Max(v => v.Y)) / 2, 0);
        }
        Brush owner = StairGeometry.Create(NewId(p), s, hit.LocalToWorld(origin), rotation);
        owner.MainScale = scale; owner.PostScale = hit.PostScale; owner.Editor = hit.Editor.Clone();
        return new StairReplacement("Convert to editable stairs", old.Min(p.Brushes.IndexOf), old.Select(b => b.Clone()).ToList(), [owner], []);
    }

    public sealed class StairReplacement(string name, int index, List<Brush> before, List<Brush> after, List<Actor> actors) : IEditCommand
    {
        private readonly List<int> _positions = [];
        public string Name => name;
        public IReadOnlyList<Brush> Removed => before;
        public IEnumerable<ObjectRef> Added => after.Select(ObjectRef.ForBrush).Concat(actors.Select(a => ObjectRef.Actor(a.Id)));
        public void Do(MapProject p)
        {
            _positions.Clear(); _positions.AddRange(before.Select(b => p.IndexOfBrush(b.Id)));
            p.Brushes.RemoveAll(b => before.Any(x => x.Id == b.Id));
            p.Brushes.InsertRange(Math.Min(index, p.Brushes.Count), after.Select(b => b.Clone()));
            p.Actors.AddRange(actors.Select(a => a.Clone()));
        }
        public void Undo(MapProject p)
        {
            p.Brushes.RemoveAll(b => after.Any(x => x.Id == b.Id)); p.Actors.RemoveAll(a => actors.Any(x => x.Id == a.Id));
            foreach (var (b, at) in before.Zip(_positions)) p.Brushes.Insert(Math.Clamp(at, 0, p.Brushes.Count), b.Clone());
        }
    }
}
