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
    private string? _folder;

    public void Attach(MainWindow window, EditorContext context)
    {
        _folder = AssetService.ProjectFolder;
        window.AddChild(AssetThumbnails.Create());
        context.SessionReplaced += OnSessionReplaced;
        AssetService.Instance.AssetsChanged += () =>
        {
            ClearCaches();
            ViewportLayout.Instance?.Scene.Rebuild(context.Project, AssetService.ProjectFolder);
        };
        AssetService.Instance.Reload();
    }

    private void OnSessionReplaced()
    {
        string? folder = AssetService.ProjectFolder;
        if (!string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
        {
            // Map-local assets differ per project folder, so resolved materials and meshes are stale.
            _folder = folder;
            ClearCaches();
        }

        AssetService.Instance.Reload();
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
