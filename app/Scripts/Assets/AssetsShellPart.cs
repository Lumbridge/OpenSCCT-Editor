using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;
using OpenScctEditor.Viewports.Render;

namespace OpenScctEditor.Assets;

/// <summary>
/// Starts the asset service with the window: indexes the open map's assets, keeps the drawn materials and meshes in
/// step with the project folder (they resolve against it), and redraws the views after an import. Attaches before the
/// viewport layout, so a new map's caches are reset before its scene is drawn.
/// </summary>
[ShellPart(Order = -22)]
public sealed class AssetsShellPart : IShellPart
{
    private string? _source;

    public void Attach(MainWindow window, EditorContext context)
    {
        _source = AssetService.BakedSourceMap;
        window.AddChild(AssetThumbnails.Create());
        window.AddChild(new AuthoredClock());
        context.SessionReplaced += OnSessionReplaced;
        context.ProjectChanged += _ => OnProjectChanged();
        AssetService.Instance.AssetsChanged += () =>
        {
            ClearCaches();
            ViewportLayout.Instance?.Scene.Rebuild(context.Project, AssetService.ProjectFolder);
        };
        AssetService.Instance.Reload();
    }

    private void OnSessionReplaced()
    {
        // Reopening the same map must see assets imported by a separate CLI while it was open.
        _source = AssetService.BakedSourceMap;
        ClearCaches();
        AssetService.Instance.ProjectAssetsChanged(redraw: false);
    }

    /// <summary>An edit, undo or redo (or a script) that changes the baked world's source map, or drops the baked world:
    /// meshes found in the old source, or missing without it, are stale, so resolve again and redraw.</summary>
    private void OnProjectChanged()
    {
        string? source = AssetService.BakedSourceMap;
        if (!string.Equals(source, _source, StringComparison.OrdinalIgnoreCase))
        {
            _source = source;
            AssetService.Instance.ProjectAssetsChanged();
        }
    }

    private static void ClearCaches()
    {
        ViewMaterials.Clear();
        StaticMeshes.Clear();
        TextureCache.Clear();
        StaticMeshDrawing.Reset();
        AssetThumbnails.Instance?.Clear();
    }
}
