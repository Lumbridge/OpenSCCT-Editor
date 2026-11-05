using OpenScct.Core;
using OpenScct.Editor.Core.Model;
using OpenScct.Game.Svm;
using OpenScct.Physics.Conveyors;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>
/// Another conveyor's open end meeting one conveyor's side: the side (-1 left, +1 right, facing the direction of
/// travel), the stretch along the path a wall there opens over, and the joining conveyor's id.
/// </summary>
public readonly record struct ConveyorJoin(int Side, double From, double To, string Other);

/// <summary>
/// Finds where conveyors join or branch off each other's sides (spec/additions/conveyors.md#look): an <c>CW_Auto</c>
/// conveyor opens its wall there, Map Check warns when a fixed wall is in the way, and the RE+ export leaves that
/// belt's wall out.
/// </summary>
public static class ConveyorJoins
{
    /// <summary>An end this far outside the wall's outer face still joins (it stops short of the frame).</summary>
    private const double Reach = 16;

    /// <summary>An end this far above or below the side's surface is on another level, not a join.</summary>
    private const double JoinHeight = 48;

    /// <summary>
    /// The sine of the smallest angle a join makes with this conveyor (about 12 degrees): a conveyor ending alongside
    /// runs beside the wall rather than through it.
    /// </summary>
    private const double MinSine = 0.2;

    /// <summary>The joins onto <paramref name="a"/>'s sides from the other conveyors of <paramref name="conveyors"/>.</summary>
    public static List<ConveyorJoin> Find(Actor a, IEnumerable<Actor> conveyors)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(conveyors);
        Conveyor c = ConveyorDesign.Read(a);
        ConveyorPath path = c.BuildWorldPath();
        var joins = new List<ConveyorJoin>();
        if (path.Length <= 0f)
        {
            return joins;
        }

        foreach (Actor other in conveyors.Where(o => !ReferenceEquals(o, a) && ConveyorDesign.Is(o)))
        {
            Conveyor oc = ConveyorDesign.Read(other);
            ConveyorPath otherPath = oc.BuildWorldPath();
            if (otherPath.Length <= 0f || otherPath.IsLoop)
            {
                continue;
            }

            foreach (bool last in (bool[])[false, true])
            {
                if (Join(c, path, oc, otherPath, last, other.Id) is { } join)
                {
                    joins.Add(join);
                }
            }
        }

        return joins;
    }

    /// <summary>The joins onto every conveyor of the project, by id.</summary>
    public static Dictionary<string, List<ConveyorJoin>> All(IReadOnlyList<Actor> actors)
    {
        ArgumentNullException.ThrowIfNull(actors);
        List<Actor> conveyors = actors.Where(ConveyorDesign.Is).ToList();
        return conveyors.ToDictionary(a => a.Id, a => Find(a, conveyors), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The join one end of the other conveyor makes with this one's side, or null.</summary>
    private static ConveyorJoin? Join(Conveyor c, ConveyorPath path, Conveyor other, ConveyorPath otherPath, bool last, string otherId)
    {
        otherPath.Evaluate(last ? otherPath.Length : 0f, out FVector end, out FVector tangent);
        // Out of the other conveyor at this end: its body lies the other way.
        Vec3 outward = Flat(ConveyorDesign.V(tangent)) * (last ? 1 : -1);
        double half = c.HalfWidth;
        if (!path.Project(end, (float)(half + ConveyorLayout.WallThickness + Reach), (float)JoinHeight, (float)JoinHeight, 0f,
                out ConveyorProjection at))
        {
            return null;
        }

        Vec3 right = Right(ConveyorDesign.V(at.Tangent));
        Vec3 behind = ConveyorDesign.V(end) - outward * (2 * (half + ConveyorLayout.WallThickness + Reach));
        double side = Vec3.Dot(behind - ConveyorDesign.V(at.Surface), right);
        if (Math.Abs(side) <= half)
        {
            // It comes in along this conveyor (end to end), not through a side.
            return null;
        }

        double sine = Math.Abs(Vec3.Cross(outward, Flat(ConveyorDesign.V(at.Tangent))).Z);
        if (sine < MinSine)
        {
            return null;
        }

        // A slanted join crosses the wall over a longer stretch.
        double open = other.HalfWidth / sine + ConveyorLayout.WallThickness;
        return new ConveyorJoin(Math.Sign(side), Math.Max(0, at.Along - open), Math.Min(path.Length, at.Along + open), otherId);
    }

    /// <summary>True when a join opens side <paramref name="side"/> anywhere in [<paramref name="from"/>, <paramref name="to"/>].</summary>
    public static bool Opens(IReadOnlyList<ConveyorJoin>? joins, int side, double from, double to) =>
        joins is not null && joins.Any(j => j.Side == side && j.From < to && j.To > from);

    /// <summary>The flat right-hand side of a direction of travel (+Y of +X in UE's frame).</summary>
    private static Vec3 Right(Vec3 t) => new Vec3(-t.Y, t.X, 0) is var r && r.LengthSquared > 1e-12 ? r.Normalized() : Vec3.UnitY;

    private static Vec3 Flat(Vec3 v) => new Vec3(v.X, v.Y, 0) is var f && f.LengthSquared > 1e-12 ? f.Normalized() : Vec3.UnitX;
}
