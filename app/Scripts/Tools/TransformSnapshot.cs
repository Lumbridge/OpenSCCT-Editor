using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Viewports.Render;

namespace OpenScctEditor.Tools;

public enum TransformMode
{
    Move,
    Rotate,
    Scale,
}

/// <summary>
/// The selection as it was when a drag (or a typed edit) began. Every step of the drag is computed from this start
/// state, not from the last step, so nothing drifts, and every step is a <see cref="ReplaceObjectsCommand"/> with the
/// drag's merge key, so the whole drag is one undo step.
/// </summary>
public sealed class TransformSnapshot
{
    private readonly Dictionary<string, Brush> _brushes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Actor> _actors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Ge> _ges = new(StringComparer.OrdinalIgnoreCase);

    public TransformSnapshot(MapProject p, IEnumerable<ObjectRef> refs)
    {
        Refs = refs.Where(r => p.Exists(r)).Distinct().ToList();
        foreach (ObjectRef r in Refs)
        {
            switch (r.Kind)
            {
                case ObjectKind.Brush:
                case ObjectKind.Stair:
                    _brushes[r.Id] = p.FindBrush(r.Id)!.Clone();
                    break;
                case ObjectKind.Actor:
                    _actors[r.Id] = p.FindActor(r.Id)!.Clone();
                    break;
                case ObjectKind.Ge:
                    _ges[r.Id] = p.FindGe(r.Id)!.Clone();
                    break;
            }
        }
    }

    public IReadOnlyList<ObjectRef> Refs { get; }

    public bool IsEmpty => Refs.Count == 0;

    /// <summary>A brush as it was when the snapshot was taken (do not change it), or null.</summary>
    public Brush? StartBrush(string id) => _brushes.GetValueOrDefault(id);

    /// <summary>An actor as it was when the snapshot was taken (do not change it), or null.</summary>
    public Actor? StartActor(string id) => _actors.GetValueOrDefault(id);

    /// <summary>A GE as it was when the snapshot was taken (do not change it), or null.</summary>
    public Ge? StartGe(string id) => _ges.GetValueOrDefault(id);

    /// <summary>A fresh move baseline rotated about the selection pivot, without touching the map.</summary>
    public TransformSnapshot Turned(MapProject p, Vec3 pivot, int units)
    {
        var copy = new TransformSnapshot(p, Refs);
        Rot turn = RotationMath.About(2, units);
        Vec3 Orbit(Vec3 v) => pivot + turn.Rotate(v - pivot);
        foreach ((string id, Brush original) in _brushes)
        {
            Brush b = original.Clone();
            b.Location = Orbit(original.Location);
            b.Rotation = RotationMath.TurnAbout(original.Rotation, 2, units);
            copy._brushes[id] = b;
        }
        foreach ((string id, Actor original) in _actors)
        {
            Actor a = original.Clone();
            a.Location = Orbit(original.Location);
            a.Rotation = RotationMath.TurnAbout(original.Rotation, 2, units);
            copy._actors[id] = a;
        }
        foreach ((string id, Ge original) in _ges)
        {
            Ge g = original.Clone();
            g.Points = original.Points.Select(Orbit).ToList();
            copy._ges[id] = g;
        }
        return copy;
    }

    public ReplaceObjectsCommand Translate(MapProject p, Vec3 offset, string? mergeKey) =>
        ReplaceObjectsCommand.Edit(p, Refs, Name("Move"),
            brush: b => { b.Location = _brushes[b.Id].Location + offset; b.Rotation = _brushes[b.Id].Rotation; },
            actor: a => { a.Location = _actors[a.Id].Location + offset; a.Rotation = _actors[a.Id].Rotation; },
            ge: g => g.Points = _ges[g.Id].Points.Select(x => x + offset).ToList(),
            mergeKey: mergeKey);

    /// <summary>Turns everything by <paramref name="units"/> about a world axis (0 X, 1 Y, 2 Z) through
    /// <paramref name="pivot"/>: rotations compose with the turn, locations and GE points orbit the pivot.</summary>
    public ReplaceObjectsCommand Rotate(MapProject p, Vec3 pivot, int axis, int units, string? mergeKey)
    {
        Rot turn = RotationMath.About(axis, units);
        Vec3 Orbit(Vec3 v) => pivot + turn.Rotate(v - pivot);
        return ReplaceObjectsCommand.Edit(p, Refs, Name("Rotate"),
            brush: b =>
            {
                Brush s = _brushes[b.Id];
                b.Rotation = RotationMath.TurnAbout(s.Rotation, axis, units);
                b.Location = Orbit(s.Location);
            },
            actor: a =>
            {
                Actor s = _actors[a.Id];
                a.Rotation = RotationMath.TurnAbout(s.Rotation, axis, units);
                a.Location = Orbit(s.Location);
            },
            ge: g => g.Points = _ges[g.Id].Points.Select(Orbit).ToList(),
            mergeKey: mergeKey);
    }

    /// <summary>Scales along a world axis (or uniformly when <paramref name="axis"/> is -1): brushes' MainScale, actors'
    /// DrawScale3D (DrawScale when uniform), GE points; locations spread from <paramref name="pivot"/>. A rotated
    /// object scales along its own axis nearest the world axis, so it never shears. Brushes keep their texture size
    /// (<see cref="TextureLock"/>) unless Surface > Scale Keeps Texture Size is off.</summary>
    public ReplaceObjectsCommand Scale(MapProject p, Vec3 pivot, int axis, double factor, string? mergeKey, bool preview = false)
    {
        Vec3 world = axis < 0 ? Vec3.One * factor : Vec3.One.With(axis, factor);
        Vec3 Local(Rot r) => axis < 0 ? world : Vec3.One.With(RotationMath.LocalAxisNearest(r, axis), factor);
        Vec3 Spread(Vec3 v) => pivot + (v - pivot).Scale(world);
        return ReplaceObjectsCommand.Edit(p, Refs, Name("Scale"),
            brush: b =>
            {
                Brush s = _brushes[b.Id];
                b.MainScale = s.MainScale.Scale(Local(s.Rotation));
                b.Location = Spread(s.Location);
                if (s.Stair is not null)
                {
                    b.Stair = s.Stair.Clone();
                    OpenScct.Editor.Core.Stairs.StairTransforms.Resize(s, b, rebuild: !preview);
                }
                else if (!preview && SurfaceActions.ScaleKeepsTextures)
                {
                    TextureLock.Keep(s, b);
                }
            },
            actor: a =>
            {
                // From the scale it is drawn at (the class default when it has none of its own: a sticky cam's
                // DrawScale 2 goes to 2 × factor, not to the factor).
                Actor s = _actors[a.Id];
                a.Window = s.Window?.Clone();
                a.Door = s.Door?.Clone();
                if (axis < 0)
                {
                    a.DrawScale = StaticMeshDrawing.DrawScale(s) * factor;
                }
                else
                {
                    a.DrawScale3D = StaticMeshDrawing.DrawScale3D(s).Scale(Local(s.Rotation));
                }

                a.Location = Spread(s.Location);
                OpenScct.Editor.Core.Windows.WindowTransforms.Follow(s, a, prepare: !preview);
                OpenScct.Editor.Core.Doors.DoorCommands.FollowScale(s, a, prepare: !preview);
            },
            ge: g => g.Points = _ges[g.Id].Points.Select(Spread).ToList(),
            mergeKey: mergeKey, followScale: false);
    }

    /// <summary>The start location, rotation and scale of an object (for typed values).</summary>
    public (Vec3 Location, Rot Rotation, Vec3 Scale)? Start(ObjectRef r) => r.Kind switch
    {
        ObjectKind.Brush or ObjectKind.Stair when _brushes.TryGetValue(r.Id, out Brush? b) => (b.Location, b.Rotation, b.MainScale),
        ObjectKind.Actor when _actors.TryGetValue(r.Id, out Actor? a) => (a.Location, a.Rotation, a.DrawScale3D * a.DrawScale),
        ObjectKind.Ge when _ges.TryGetValue(r.Id, out Ge? g) && g.Points.Count > 0 => (g.Points[0], Rot.Zero, Vec3.One),
        _ => null,
    };

    public bool HasParametric => _brushes.Values.Any(b => b.Stair is not null) || _actors.Values.Any(a => a.Window is not null || a.Door is not null);

    /// <summary>Actual bounds after a semantic scale, without a BSP rebuild or writing preview assets.</summary>
    public OpenScct.Editor.Core.Geometry.Snap.MovingShape ScaledShape(Vec3 pivot, int axis, double factor)
    {
        var p = new MapProject { Brushes = _brushes.Values.Select(b => b.Clone()).ToList(), Actors = _actors.Values.Select(a => a.Clone()).ToList(), Ges = _ges.Values.Select(g => g.Clone()).ToList() };
        Scale(p, pivot, axis, factor, null, preview: true).Do(p);
        var points = new List<Vec3>();
        foreach (Brush b in p.Brushes)
            points.AddRange(b.Stair is null ? b.WorldFaces().SelectMany(f => f.Vertices)
                : OpenScct.Editor.Core.Stairs.StairGeometry.Generate(b).Solids.SelectMany(part => part.WorldFaces()).SelectMany(f => f.Vertices));
        foreach (Actor a in p.Actors)
        {
            var bounds = a.Window is not null || a.Door is not null || a.Shape is not null ? OpenScct.Editor.Core.Scripting.MapObjects.ActorBounds(a)
                : ActorSnapBox.Of(a)?.Box.Bounds() ?? (a.Location, a.Location);
            points.Add(bounds.Item1); points.Add(bounds.Item2);
        }
        points.AddRange(p.Ges.SelectMany(g => g.Points));
        return OpenScct.Editor.Core.Geometry.Snap.MovingShape.Box(points.Aggregate(Vec3.Min), points.Aggregate(Vec3.Max), Refs.Count == 1 ? Refs[0] : null);
    }

    private string Name(string verb) => Refs.Count == 1 ? $"{verb} {Refs[0].Id}" : $"{verb} {Refs.Count} objects";
}
