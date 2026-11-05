using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Geometry.Edit;

/// <summary>
/// Edits of a selection of brushes, actors and GEs: snap to grid, mirror, copies along a vector and replace class.
/// Each returns one undoable command.
/// </summary>
public static class SelectionEdit
{
    /// <summary>Snaps pivots (brush and actor locations, GE points) to the grid on the given axes (all when null).</summary>
    public static ReplaceObjectsCommand SnapToGrid(MapProject p, IEnumerable<ObjectRef> refs, double grid, IReadOnlyList<int>? axes = null)
    {
        Vec3 Snap(Vec3 v)
        {
            foreach (int axis in axes ?? [0, 1, 2])
            {
                v = v.With(axis, Math.Round(v[axis] / grid) * grid);
            }

            return v;
        }

        return ReplaceObjectsCommand.Edit(p, refs, "Snap to grid",
            brush: b => b.Location = Snap(b.Location),
            actor: a => a.Location = Snap(a.Location),
            ge: g => g.Points = g.Points.Select(Snap).ToList());
    }

    /// <summary>
    /// Mirrors the objects across the plane through <paramref name="pivot"/> normal to <paramref name="axis"/>. Brushes
    /// get their transform baked and their faces mirrored (wound outwards); actors mirror their location and facing,
    /// and movers their base (BasePos, BaseRot) and key offsets (KeyPos, KeyRot), so every key mirrors; GE points
    /// mirror.
    /// </summary>
    public static ReplaceObjectsCommand Mirror(MapProject p, IEnumerable<ObjectRef> refs, int axis, Vec3 pivot)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)axis, 2u);
        Vec3 Flip(Vec3 v) => v.With(axis, 2 * pivot[axis] - v[axis]);
        Vec3 FlipDir(Vec3 v) => v.With(axis, 0 - v[axis]); // 0 - v, not -v: no -0 in saved files
        string name = "Mirror " + "XYZ"[axis];
        return ReplaceObjectsCommand.Edit(p, refs, name,
            brush: b =>
            {
                Brush baked = BrushOps.Bake(b);
                b.Rotation = Rot.Zero;
                b.MainScale = Vec3.One;
                b.PostScale = Vec3.One;
                b.PrePivot = Vec3.Zero;
                b.Shape = BrushOps.Mirror(baked.Shape, axis);
                b.Location = Flip(b.Location);
            },
            actor: a =>
            {
                (Vec3 f, Vec3 _, Vec3 u) = a.Rotation.Axes();
                bool shaped = a.Shape is not null;
                a.Location = Flip(a.Location);
                if (PropValues.TryVector(a.Props["BasePos"], out Vec3 basePos))
                {
                    a.Props["BasePos"] = PropValues.Vector(Flip(basePos));
                }

                if (PropValues.TryRotator(a.Props["BaseRot"], out Rot baseRot))
                {
                    a.Props["BaseRot"] = PropValues.Rotator(MirrorRotation(baseRot, axis, shaped));
                }

                MoverBase.MapKeyPos(a, FlipDir);
                MoverBase.MapKeyRot(a, k => MirrorOffset(k, axis, shaped));
                if (a.Shape is { } shape)
                {
                    // A shaped actor (volume) mirrors its shape in local space too: the rotation becomes M R Mk,
                    // which is a proper rotation again.
                    a.Shape = BrushOps.Mirror(shape, axis);
                    a.PrePivot = FlipDir(a.PrePivot);
                    a.Rotation = Orientation.FromAxes(FlipDir(axis == 0 ? -f : f), FlipDir(axis == 2 ? -u : u));
                }
                else
                {
                    // Others keep facing the mirrored way.
                    a.Rotation = Orientation.FromAxes(FlipDir(f), FlipDir(u));
                }
            },
            ge: g => g.Points = g.Points.Select(Flip).ToList(),
            followBase: false);
    }

    /// <summary>
    /// A rotation mirrored the way <see cref="Mirror"/> turns an actor: the fixed angle (a half turn of yaw for X, of
    /// roll for Z, none for a shaped actor) plus <see cref="MirrorOffset"/>. A mover's base is set by this rather than
    /// by following the actor's new rotation, which is written another way when its pitch is past 90 degrees; base
    /// plus key then mirrors for every key.
    /// </summary>
    public static Rot MirrorRotation(Rot r, int axis, bool shaped)
    {
        Rot k = MirrorOffset(r, axis, shaped);
        int half = Rot.UnitsPerTurn / 2;
        return (shaped ? k : axis switch
        {
            0 => k with { Yaw = k.Yaw + half },
            2 => k with { Roll = k.Roll + half },
            _ => k,
        }).Tidy();
    }

    /// <summary>
    /// A mover key rotation mirrored. The rotation mirror in <see cref="Mirror"/> works out to a fixed angle plus or
    /// minus each component (X: pitch, -yaw, -roll, or -pitch, -yaw, roll for a shaped actor; Y: pitch, -yaw, -roll;
    /// Z: -pitch, yaw, -roll), and KeyRot adds to BaseRot component by component, so each offset takes the same sign.
    /// A full turn stays a full turn.
    /// </summary>
    public static Rot MirrorOffset(Rot k, int axis, bool shaped) => axis switch
    {
        0 when shaped => new Rot(-k.Pitch, -k.Yaw, k.Roll),
        2 => new Rot(-k.Pitch, k.Yaw, -k.Roll),
        _ => new Rot(k.Pitch, -k.Yaw, -k.Roll),
    };

    /// <summary>
    /// <paramref name="count"/> copies of the objects, copy <c>i</c> offset by <c>step * i</c>, with fresh ids. New
    /// brushes go at the end of the CSG order, as Duplicate does. Actors with their own event Tag get a fresh Tag per
    /// copy (RE+ copies), and a copied actor whose Event names a Tag copied in the same round follows it, so a copied
    /// trigger fires its own copied target. As Duplicate, copies are plain objects (not prefab parts), a copied mover
    /// takes its base along, and copied piece parts form a piece of their own. Copies of brushes imported with the baked
    /// world are new brushes (<see cref="BakedContent.Unmark"/>); <paramref name="note"/> says so when there were any.
    /// </summary>
    public static AddObjectsCommand Array(MapProject p, IReadOnlyCollection<ObjectRef> refs, int count, Vec3 step, out Finding? note)
    {
        if (count < 1 || count > 1000)
        {
            throw new ArgumentException("array: the count must be 1 to 1000");
        }

        var ids = new FreshIds(p);
        var tags = new FreshTags(p);
        var brushes = new List<Brush>();
        var actors = new List<Actor>();
        var ges = new List<Ge>();
        for (int i = 1; i <= count; i++)
        {
            Vec3 d = step * i;
            var round = new List<Actor>();
            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var copyOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int firstBrush = brushes.Count;
            foreach (ObjectRef r in refs)
            {
                switch (r.Kind)
                {
                    case ObjectKind.Brush when p.FindBrush(r.Id) is { } b:
                        Brush nb = b.Clone(ids.Next(b.Id));
                        nb.Location += d;
                        nb.Editor.UnlinkPrefab();
                        copyOf[b.Id] = nb.Id;
                        brushes.Add(nb);
                        break;
                    case ObjectKind.Actor when p.FindActor(r.Id) is { } a:
                        Actor na = a.Clone(ids.Next(a.Id));
                        na.Location += d;
                        MoverBase.Shift(na, d);
                        na.Editor.UnlinkPrefab();
                        if (a.Props.GetString("Tag") is { Length: > 0 } tag && !string.Equals(tag, a.ShortClass, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!renamed.TryGetValue(tag, out string? fresh))
                            {
                                renamed[tag] = fresh = tags.Next(tag);
                            }

                            na.Props.Set("Tag", OpenScct.Content.Json.SvJsonNode.String(fresh));
                        }

                        round.Add(na);
                        break;
                    case ObjectKind.Ge when p.FindGe(r.Id) is { } g:
                        Ge ng = g.Clone(ids.Next(g.Id));
                        ng.Points = g.Points.Select(x => x + d).ToList();
                        ng.Editor.UnlinkPrefab();
                        ges.Add(ng);
                        break;
                }
            }

            foreach (Actor na in round)
            {
                if (na.Props.GetString("Event") is { Length: > 0 } ev && renamed.TryGetValue(ev, out string? target))
                {
                    na.Props.Set("Event", OpenScct.Content.Json.SvJsonNode.String(target));
                }
            }

            actors.AddRange(round);
            PiecePlacer.RepointPieces(brushes.Skip(firstBrush), copyOf);
        }

        (int baked, int carving) = BakedContent.Unmark(brushes);
        note = BakedContent.CopyNote(p, baked, carving);
        int total = brushes.Count + actors.Count + ges.Count;
        return new AddObjectsCommand($"Array {count} x {refs.Count} objects ({total} new)", brushes, actors, ges);
    }

    /// <summary>Changes the class of the selected actors (stock <c>ACTOR REPLACE</c>), keeping their transform, shape,
    /// editor data and the stored properties the new class also has; the others are dropped, since the new class would
    /// not read them. With no class catalogue loaded every property is kept.</summary>
    public static ReplaceObjectsCommand ReplaceClass(MapProject p, IEnumerable<ObjectRef> refs, string newClass)
    {
        if (string.IsNullOrWhiteSpace(newClass))
        {
            throw new ArgumentException("replace class: give a class");
        }

        string cls = newClass.Trim();
        HashSet<string>? shared = null;
        if (EditorServices.Classes is { } classes)
        {
            ClassInfo info = classes.Find(cls) ?? throw new ArgumentException($"replace class: there is no class {cls}");
            cls = info.Name;
            shared = new HashSet<string>(classes.AllProperties(info.Name).Select(prop => prop.Name), StringComparer.OrdinalIgnoreCase);
        }

        return ReplaceObjectsCommand.Edit(p, refs.Where(r => r.Kind == ObjectKind.Actor), $"Replace class with {cls}", actor: a =>
        {
            a.Class = cls;
            if (shared is not null)
            {
                foreach (string key in a.Props.Keys.Where(k => !shared.Contains(k)).ToList())
                {
                    a.Props.Remove(key);
                }
            }
        });
    }

    /// <summary>The centre of the objects' pivots (brush and actor locations, GE points); zero when empty.</summary>
    public static Vec3 PivotCentre(MapProject p, IEnumerable<ObjectRef> refs)
    {
        List<Vec3> pts = refs.SelectMany(r => r.Kind switch
        {
            ObjectKind.Brush when p.FindBrush(r.Id) is { } b => [b.Location],
            ObjectKind.Actor when p.FindActor(r.Id) is { } a => [a.Location],
            ObjectKind.Ge when p.FindGe(r.Id) is { } g => g.Points,
            _ => Enumerable.Empty<Vec3>(),
        }).ToList();
        if (pts.Count == 0)
        {
            return Vec3.Zero;
        }

        (Vec3 min, Vec3 max) = BrushOps.BoundsOf(pts);
        return (min + max) / 2;
    }
}
