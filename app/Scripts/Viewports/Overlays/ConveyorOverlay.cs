using Godot;
using OpenScct.Core;
using OpenScct.Core.Uc;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Game.Svm;
using OpenScct.Physics.Conveyors;
using OpenScctEditor.Viewports.Render;

namespace OpenScctEditor.Viewports.Overlays;

/// <summary>
/// Native conveyors: the path's centre line and the edges of the carried strip, arrows the way it runs, its points,
/// and where its items sit at the start of a round (the game's own item flow). The conveyor tool animates the arrows
/// and items while it is active; those conveyors show only their moving ones.
/// </summary>
public sealed class ConveyorOverlay : IViewOverlay
{
    public static readonly Color Colour = new(1f, 0.7f, 0.25f);
    public static readonly Color Stopped = new(0.6f, 0.6f, 0.62f);

    /// <summary>Distance between direction arrows along the path.</summary>
    private const float ArrowSpacing = 256;

    /// <summary>The length an item takes up along a belt before its <c>ItemScale</c> while none of its meshes has loaded (the game's fallback).</summary>
    private const float DefaultItemLength = 32;

    /// <summary>Item meshes asked for, so each is requested once.</summary>
    private static readonly HashSet<string> s_requested = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "conveyors";

    public string Title => "Conveyors";

    public bool OnByDefault => true;

    /// <summary>Set while a tool draws a conveyor's arrows and items itself (moving), so they are not drawn twice.</summary>
    public static Func<Actor, bool>? Animated { get; set; }

    /// <summary>Raised when an item mesh has loaded, so item sizes measured before it (the fallback) are out of date.</summary>
    public static event Action? ItemSizesChanged;

    public void Collect(OverlayContext c, OverlayBatch batch)
    {
        List<(Actor Actor, Conveyor Conveyor)> all = ConveyorDesign.ReadAll(c.Project.Actors);
        var still = new Dictionary<Conveyor, Color>(ReferenceEqualityComparer.Instance);
        foreach ((Actor a, Conveyor conveyor) in all.Where(p => c.IsVisible(p.Actor.Editor)))
        {
            ConveyorPath path = conveyor.WorldPath;
            bool selected = c.IsSelected(ObjectRef.Actor(a.Id));
            Color colour = conveyor.bOn && conveyor.Speed != 0 ? Colour : Stopped;
            foreach (ConveyorPoint p in ConveyorDesign.WorldPoints(a))
            {
                batch.Dot(p.At, colour, selected ? 4 : 3);
            }

            if (path.Length <= 0f)
            {
                continue;
            }

            Strip(batch, path, conveyor.HalfWidth, colour, selected ? 2.5f : 1.5f);
            if (Animated?.Invoke(a) != true)
            {
                Arrows(batch, path, conveyor, 0, colour);
                still[conveyor] = colour;
            }

            batch.Label(ConveyorDesign.V(Sample(path, 0)), $"{a.Id} {conveyor.Speed:0} u/s{(conveyor.bOn ? "" : " (off)")}", colour, selected);
        }

        if (still.Count > 0)
        {
            var items = new List<ConveyorItemPlace>();
            Flow(all).Place(0, items);
            Items(batch, items, origin => still.TryGetValue(origin, out Color colour) ? colour : null);
        }
    }

    /// <summary>The game's item flow over the conveyors, at the start of a round.</summary>
    public static ConveyorItemFlow Flow(IReadOnlyList<(Actor Actor, Conveyor Conveyor)> conveyors) =>
        new(conveyors.Select(p => p.Conveyor).ToList(), c => ItemLength(c) * c.ItemScale);

    /// <summary>
    /// The length an item of <paramref name="c"/> takes up along a belt before its <c>ItemScale</c>, as the game
    /// measures it: the widest flat extent of its meshes. Meshes not loaded yet are asked for and count as
    /// <see cref="DefaultItemLength"/> until they arrive (<see cref="ItemSizesChanged"/>).
    /// </summary>
    public static float ItemLength(Conveyor c)
    {
        double length = 0;
        foreach (AssetRef asset in c.ItemMeshes)
        {
            if (asset.Path is not { } path)
            {
                continue;
            }

            if (StaticMeshes.Loaded(path) is { } mesh)
            {
                length = Math.Max(length, Math.Max(mesh.Max.X - mesh.Min.X, mesh.Max.Y - mesh.Min.Y));
            }
            else if (!StaticMeshes.IsMissing(path) && s_requested.Add(path))
            {
                StaticMeshes.Request(path, _ =>
                {
                    ItemSizesChanged?.Invoke();
                    ViewOverlays.Invalidate();
                });
            }
        }

        return length > 0 ? (float)length : DefaultItemLength;
    }

    /// <summary>The centre line and both edges, sampled along the path.</summary>
    public static void Strip(OverlayBatch batch, ConveyorPath path, float halfWidth, Color colour, float width)
    {
        var centre = new List<Vec3>();
        var left = new List<Vec3>();
        var right = new List<Vec3>();
        int n = Math.Max(2, (int)Math.Ceiling(path.Length / 32f));
        for (int i = 0; i <= n; i++)
        {
            path.Evaluate(path.Length * i / n, out FVector p, out FVector t);
            Vec3 at = ConveyorDesign.V(p);
            Vec3 side = new Vec3(-t.Y, t.X, 0).Normalized() * halfWidth;
            centre.Add(at);
            left.Add(at - side);
            right.Add(at + side);
        }

        batch.Polyline(centre, colour, width, dashed: true);
        batch.Polyline(left, colour, width);
        batch.Polyline(right, colour, width);
    }

    /// <summary>Arrows the way the belt runs, slid along by <paramref name="phase"/> (uu travelled).</summary>
    public static void Arrows(OverlayBatch batch, ConveyorPath path, Conveyor conveyor, double phase, Color colour)
    {
        int n = Math.Max(1, (int)(path.Length / ArrowSpacing));
        float sign = conveyor.Speed < 0 ? -1 : 1;
        float head = Math.Min(48, conveyor.HalfWidth);
        for (int i = 0; i < n; i++)
        {
            float along = ConveyorPath.Wrap((float)(path.Length * (i + 0.5) / n + phase), path.Length);
            path.Evaluate(along, out FVector p, out FVector t);
            Vec3 at = ConveyorDesign.V(p), dir = ConveyorDesign.V(t) * sign;
            batch.Arrow(at - dir * head, at + dir * head, colour, 2);
        }
    }

    /// <summary>
    /// Items as the game places them: a ring as wide as each one takes up, in the colour <paramref name="colourOf"/>
    /// gives the conveyor it came from (none: not drawn here).
    /// </summary>
    public static void Items(OverlayBatch batch, IEnumerable<ConveyorItemPlace> items, Func<Conveyor, Color?> colourOf)
    {
        foreach (ConveyorItemPlace item in items)
        {
            if (item.Scale > 0 && colourOf(item.Origin) is { } colour)
            {
                Vec3 at = ConveyorDesign.V(item.Location);
                batch.Ring(at + new Vec3(0, 0, 8), Vec3.UnitX, Vec3.UnitY, ItemLength(item.Origin) / 2 * item.Scale, colour, 1, segments: 12);
            }
        }
    }

    private static FVector Sample(ConveyorPath path, float along)
    {
        path.Evaluate(along, out FVector p, out _);
        return p;
    }
}
