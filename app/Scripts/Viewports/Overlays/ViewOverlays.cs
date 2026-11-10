using System.Text;
using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Viewports.Overlays;

/// <summary>What an overlay reads when it collects its shapes.</summary>
public sealed record OverlayContext(EditorContext Editor, MapProject Project, IReadOnlySet<ObjectRef> Selected)
{
    public bool IsVisible(EditorMeta meta) => ObjectFilter.IsVisible(Project, meta);

    public bool IsSelected(ObjectRef r) => Selected.Contains(r);
}

/// <summary>A switchable layer of annotations drawn over every view (GE directions, team starts, camera cones...).</summary>
public interface IViewOverlay
{
    /// <summary>Stable id (the toggle action is <c>view.overlay.&lt;id&gt;</c>).</summary>
    string Id { get; }

    string Title { get; }

    bool OnByDefault { get; }

    /// <summary>Adds the overlay's world-space shapes for the current map and selection.</summary>
    void Collect(OverlayContext c, OverlayBatch batch);
}

/// <summary>
/// The overlay registry: switches (kept in the app settings), shapes cached until the map, the selection or a switch
/// changes, and the one call the view overlay makes to draw them all.
/// </summary>
public static class ViewOverlays
{
    private static readonly List<IViewOverlay> s_overlays = new();
    private static readonly Dictionary<string, OverlayBatch> s_cache = new();

    public static IReadOnlyList<IViewOverlay> All => s_overlays;

    /// <summary>Raised after an overlay was switched on or off.</summary>
    public static event Action? Switched;

    public static void Register(IViewOverlay overlay)
    {
        s_overlays.RemoveAll(o => o.Id == overlay.Id);
        s_overlays.Add(overlay);
        Invalidate();
    }

    public static bool IsOn(IViewOverlay o) => AppSettings.GetBool("overlays", o.Id, o.OnByDefault);

    public static void Set(IViewOverlay o, bool on)
    {
        AppSettings.Set("overlays", o.Id, on);
        Invalidate();
        Switched?.Invoke();
    }

    /// <summary>Drops the cached shapes (the selection outline's too) and redraws the views.</summary>
    public static void Invalidate()
    {
        s_cache.Clear();
        SelectionOutline.Invalidate();
        ViewportLayout.Instance?.RedrawAll();
    }

    /// <summary>Draws every switched-on overlay into a view (called from the view's 2D layer); no label lands on the
    /// view's own <paramref name="chrome"/>.</summary>
    public static void Draw(EditorViewport view, Control canvas, IEnumerable<Rect2> chrome)
    {
        var labels = new LabelLayout();
        labels.Reserve(chrome);
        foreach (IViewOverlay o in s_overlays.Where(IsOn))
        {
            Batch(o).Draw(view.Cam, canvas, labels);
        }

        labels.Draw(canvas);
    }

    /// <summary>One line per overlay: on or off and how many shapes it has now (for scripts).</summary>
    public static string Report()
    {
        var sb = new StringBuilder();
        foreach (IViewOverlay o in s_overlays)
        {
            sb.Append(sb.Length > 0 ? "; " : "").Append(o.Id).Append(IsOn(o) ? " on " : " off ").Append(Batch(o).Count);
        }

        return sb.ToString();
    }

    private static OverlayBatch Batch(IViewOverlay o)
    {
        if (s_cache.TryGetValue(o.Id, out OverlayBatch? batch))
        {
            return batch;
        }

        EditorContext ctx = EditorContext.Instance;
        var c = new OverlayContext(ctx, ctx.Project, ctx.Selection.Items.ToHashSet());
        batch = new OverlayBatch();
        try
        {
            o.Collect(c, batch);
        }
        catch (Exception e)
        {
            // An overlay must never take the views down; say what broke once per rebuild.
            ctx.Log($"Overlay {o.Id} failed: {e.Message}", OpenScct.Editor.Core.Services.Severity.Warning);
        }

        s_cache[o.Id] = batch;
        return batch;
    }
}

/// <summary>Registers the built-in overlays and their View/Overlays switches, and keeps the cache fresh.</summary>
[ShellPart(Order = -19)]
public sealed class OverlayPart : IShellPart
{
    public void Attach(MainWindow window, EditorContext context)
    {
        IViewOverlay[] builtIn =
        [
            new GeOverlay(), new TeamStartOverlay(), new SecurityOverlay(), new LinkOverlay(), new LightRadiusOverlay(),
            new MoverOverlay(), new VolumeOverlay(), new ZoneOverlay(), new CameraNetworkOverlay(), new TipsOverlay(), new MagicEventOverlay(),
            new StealthOverlay(), new LightRoleOverlay(), new MercSightOverlay(), new ReachOverlay(), new NavGraphOverlay(), new RouteOverlay(),
            new FindingShapesOverlay(), new ConveyorOverlay(),
        ];
        foreach (IViewOverlay o in builtIn)
        {
            ViewOverlays.Register(o);
        }

        context.ProjectChanged += _ => ViewOverlays.Invalidate();
        context.SelectionChanged += ViewOverlays.Invalidate;
        context.SessionReplaced += ViewOverlays.Invalidate;
        // The overlays draw only what the storey shown keeps.
        StoreyFilter.Changed += ViewOverlays.Invalidate;
        context.FindingsChanged += ViewOverlays.Invalidate;
        int order = 100;
        foreach (IViewOverlay overlay in ViewOverlays.All)
        {
            IViewOverlay o = overlay;
            context.AddAction(new EditorAction("view.overlay." + o.Id, o.Title, "View/Overlays", () =>
            {
                ViewOverlays.Set(o, !ViewOverlays.IsOn(o));
                context.SetStatus($"{o.Title} overlay {(ViewOverlays.IsOn(o) ? "on" : "off")}");
            })
            {
                Order = order++,
                IsChecked = () => ViewOverlays.IsOn(o),
            });
        }

        VisualLayers.MeshOutlinesShown = AppSettings.GetBool("overlays", "meshOutlines", true);
        context.AddAction(new EditorAction("view.overlay.meshOutlines", "Mesh Outlines", "View/Overlays", () =>
        {
            VisualLayers.MeshOutlinesShown = !VisualLayers.MeshOutlinesShown;
            AppSettings.Set("overlays", "meshOutlines", VisualLayers.MeshOutlinesShown);
            foreach (EditorViewport view in ViewportLayout.Instance?.Views ?? [])
            {
                view.ApplyCullMask();
                view.Redraw();
            }

            context.SetStatus($"Mesh outlines {(VisualLayers.MeshOutlinesShown ? "on" : "off")}");
        })
        {
            Order = 190,
            IsChecked = () => VisualLayers.MeshOutlinesShown,
            Description = "Draws the white box round each static mesh in the textured views (a selected mesh keeps its outline; wireframe views always draw it)",
        });

        context.AddAction(new EditorAction("view.overlays.report", "Report Overlay Shapes", "View/Overlays",
            () => context.SetStatus(ViewOverlays.Report()))
        {
            Order = 200,
            Description = "Shows each overlay's switch and how many shapes it draws now",
        });
    }
}
