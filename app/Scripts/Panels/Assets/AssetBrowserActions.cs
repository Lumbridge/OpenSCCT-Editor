using Godot;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Assets.Import;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Assets;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;
using OpenScctEditor.Viewports.Render;

namespace OpenScctEditor.Panels;

/// <summary>
/// The asset browser's actions (the Assets menu, the command palette and <c>--run=</c>): place, apply, usages,
/// favourite, play, import and reindex, and dragging assets into the views. Edits go through undoable commands; an
/// import writes files into the map's assets folder and is not undone (placing what it imported is).
/// Headless driving: <c>--asset=&lt;path&gt;</c> selects an asset, <c>--asset-import=&lt;file&gt;</c> makes
/// assets.importMesh import that file instead of asking.
/// </summary>
public partial class AssetBrowserPanel
{
    private const double FallbackDistance = 512;
    private static readonly string[] s_importFilters = ["*.gltf, *.glb ; glTF", "*.obj ; Wavefront OBJ"];
    private AudioStreamPlayer? _player;
    private string? _argAsset = Arg("--asset=");

    private void RegisterActions()
    {
        bool IsSelected(AssetKind k) => Selected?.Kind == k;
        Context.AddAction(new EditorAction("assets.search", "Search Assets", "Assets", FocusSearch)
        {
            Order = 10, Description = "Show the asset browser with the cursor in its search box",
        });
        Context.AddAction(new EditorAction("assets.place", "Place Selected Mesh", "Assets", () => PlaceSelected(null))
        {
            Order = 100, IsEnabled = () => IsSelected(AssetKind.Mesh),
            Description = "Add a StaticMeshActor showing the selected mesh at the placement point",
        });
        Context.AddAction(new EditorAction("assets.applyMaterial", "Apply Material", "Assets", () => ApplySelected(null))
        {
            Order = 110,
            IsEnabled = () => (IsSelected(AssetKind.Material) || IsSelected(AssetKind.Texture))
                && (FacesPicked() || Context.Selection.Items.Any(r => r.Kind == ObjectKind.Brush)),
            Description = "Set the selected material or texture on the faces picked in the surface tool, or on every face of the selected brushes",
        });
        Context.AddAction(new EditorAction("assets.findUsages", "Find Usages in This Map", "Assets", FindUsages)
        {
            Order = 120, IsEnabled = () => Selected is not null,
        });
        Context.AddAction(new EditorAction("assets.favourite", "Toggle Favourite", "Assets", ToggleFavourite)
        {
            Order = 130, IsEnabled = () => SelectedEntry is not null,
        });
        Context.AddAction(new EditorAction("assets.playSound", "Play Selected Sound", "Assets", PlaySound)
        {
            Order = 140, IsEnabled = () => IsSelected(AssetKind.Sound),
        });
        Context.AddAction(new EditorAction("assets.importMesh", "Import Mesh…", "Assets", ImportMesh)
        {
            Order = 200, Description = "Import a glTF or OBJ mesh into this map's assets, with a collision hull",
        });
        Context.AddAction(new EditorAction("assets.reindex", "Reindex Assets", "Assets", () =>
        {
            AssetCatalog.Reset();
            AssetService.Instance.Reload();
            Context.SetStatus("Reindexing assets…");
        }) { Order = 300 });
    }

    /// <summary>Assets > Search Assets: brings the browser to the front of its dock (it may be behind another tab)
    /// and puts the cursor in the search box, its text selected so typing replaces it.</summary>
    private void FocusSearch()
    {
        DockTabs.Reveal(this);
        // After the menu or palette that ran the action has closed and given the focus back.
        EditorLifetime.Defer(() =>
        {
            if (IsInstanceValid(_search) && _search.IsVisibleInTree())
            {
                EditSearch(selectAll: true);
            }
        });
    }

    /// <summary>Selects the <c>--asset=</c> path once it resolves (the map opens after the panels are built, and
    /// map-local assets resolve against its folder).</summary>
    private void PreselectFromArgs()
    {
        if (_argAsset is not { } path)
        {
            return;
        }

        AssetResolver r = AssetService.Instance.Resolver;
        AssetKind? kind = r.FindMesh(path) is not null ? AssetKind.Mesh
            : r.FindMaterial(path) is not null ? AssetKind.Material
            : AssetService.Instance.Catalog.Find(path)?.Kind;
        if (kind is { } k)
        {
            Selected = (k, path);
            _argAsset = null;
        }
    }

    private static string? Arg(string prefix) =>
        OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    /// <summary>The collision an imported mesh gets (kept in the app settings).</summary>
    private static CollisionMode ImportCollision
    {
        get => (CollisionMode)Math.Clamp(AppSettings.GetInt("assets", "collision", 0), 0, Enum.GetValues<CollisionMode>().Length - 1);
        set => AppSettings.Set("assets", "collision", (int)value);
    }

    /// <summary>The "more" menu of the details column: importing a mesh (and the collision it gets) and reindexing.</summary>
    private void AddMoreMenu(Control parent)
    {
        const int import = 0, reindex = 1;
        var more = new MenuButton
        {
            ThemeTypeVariation = "IconButton",
            Icon = Icons.Get("more", Colors.White),
            IconAlignment = HorizontalAlignment.Center,
            Flat = false,
            FocusMode = FocusModeEnum.None,
            TooltipText = "More asset commands",
            CustomMinimumSize = new Vector2(EditorTheme.IconButtonSize, EditorTheme.IconButtonSize),
        };
        PopupMenu menu = more.GetPopup();
        PanelUi.CapPopup(menu);
        var collision = new PopupMenu();
        foreach (CollisionMode m in Enum.GetValues<CollisionMode>())
        {
            collision.AddRadioCheckItem(m switch
            {
                CollisionMode.Parts => "A hull per part",
                CollisionMode.Hull => "One hull",
                CollisionMode.Box => "Its box",
                _ => "Its own triangles",
            }, (int)m);
        }

        void SyncCollision()
        {
            for (int i = 0; i < collision.ItemCount; i++)
            {
                collision.SetItemChecked(i, collision.GetItemId(i) == (int)ImportCollision);
            }
        }

        collision.IdPressed += id => ImportCollision = (CollisionMode)(int)id;
        MenuToggles.KeepOpen(collision, SyncCollision);
        menu.AddIconItem(Icons.Get("import", EditorTheme.Text)!, "Import mesh\u2026", import);
        menu.SetItemTooltip(menu.ItemCount - 1, "Import a glTF or OBJ mesh into this map's assets");
        menu.AddSubmenuNodeItem("Imported mesh collision", collision);
        menu.SetItemTooltip(menu.ItemCount - 1, "Collision an imported mesh gets: hulls of its parts (UCX_ parts kept), one hull, its box, or its own triangles");
        menu.AddSeparator();
        menu.AddIconItem(Icons.Get("refresh", EditorTheme.Text)!, "Reindex", reindex);
        menu.SetItemTooltip(menu.ItemCount - 1, "Index the content again (after converting maps)");
        menu.AboutToPopup += SyncCollision;
        menu.IdPressed += id => Context.Run(id == import ? "assets.importMesh" : "assets.reindex");
        parent.AddChild(more);
    }

    // ---- place and apply ------------------------------------------------------------------------------------------

    /// <summary>Places the selected mesh at <paramref name="at"/> (or the placement point), its bottom on the point.</summary>
    private void PlaceSelected(Vec3? at)
    {
        if (Selected is not { Kind: AssetKind.Mesh } s)
        {
            Context.SetStatus("Select a mesh to place first.");
            return;
        }

        MapProject p = Context.Project;
        Vec3 point = at ?? PlacementPoint(p);
        // Snap across the floor only: the height is the surface's, so the mesh stands on it.
        point = point.Snap(p.Workspace.GridSize).With(2, point.Z);
        (Vec3 Min, Vec3 Max)? bounds = SelectedEntry is { Mesh: >= 0 } e && e.Size != Vec3.Zero ? (e.BoundsMin, e.BoundsMax)
            : StaticMeshes.Loaded(s.Path) is { } m ? (m.Min, m.Max) : null;
        Actor actor = AssetEdits.MeshActor(p, s.Path, point, bounds);
        Context.Execute(AssetEdits.PlaceMesh(actor));
        Context.Selection.Set([ObjectRef.Actor(actor.Id)]);
        Context.SetStatus($"Placed {actor.Id} ({s.Path}) at {actor.Location}.");
    }

    /// <summary>Applies the selected material or texture (a texture as the material that shows it) to
    /// <paramref name="target"/>, or the faces picked in the surface tool, else every face of the selected brushes.
    /// Brushes of an imported baked world are read-only and skipped, and so are brushes whose faces all show it
    /// already: the status bar says so, and nothing that would change nothing becomes an undo step.</summary>
    private void ApplySelected(ObjectRef? target)
    {
        if (Selected is not { Kind: AssetKind.Material or AssetKind.Texture } s)
        {
            Context.SetStatus("Select a material or texture to apply first.");
            return;
        }

        (string? material, string why) = AssetService.Instance.SurfaceMaterial(s.Kind, s.Path);
        string name = AssetPaths.Split(s.Path).Name;
        if (material is null)
        {
            Context.SetStatus($"Apply {name}: {why}.");
            return;
        }

        if (target is null && (FacesPicked() || (InSurfaceTool && GeometrySelection.Instance.BakedSurface is not null)))
        {
            SurfaceCommands.ApplyMaterial(material);
            return;
        }

        MapProject p = Context.Project;
        IReadOnlyList<ObjectRef> refs = target is { } t ? [t] : Context.Selection.Items;
        List<Brush> brushes = refs.Where(r => r.Kind == ObjectKind.Brush).Select(r => p.FindBrush(r.Id)).OfType<Brush>().Distinct().ToList();
        if (brushes.Count == 0)
        {
            Context.SetStatus("Pick faces (surface tool) or select brushes to apply it to.");
            return;
        }

        List<Brush> editable = brushes.Where(b => !BakedContent.SurfacesLocked(p, b)).ToList();
        int locked = brushes.Count - editable.Count;
        string skipped = locked > 0 ? $" ({PanelUi.Count(locked, "read-only brush", "read-only brushes")} of the imported baked world skipped; "
            + $"{SurfaceCommands.BuildRenderHint})" : "";
        if (editable.Count == 0)
        {
            Context.SetStatus($"Apply {name}: read-only. {(brushes.Count == 1 ? "That brush is" : "Those brushes are")} part of the imported baked world: "
                + SurfaceCommands.BuildRenderHint);
            return;
        }

        List<Brush> changing = editable.Where(b => b.Shape.Faces.Any(f => !string.Equals(f.Material, material, StringComparison.OrdinalIgnoreCase))).ToList();
        if (changing.Count == 0 || AssetEdits.ApplyMaterial(p, changing.Select(b => ObjectRef.Brush(b.Id)), material) is not { } command)
        {
            Context.SetStatus($"Apply {name}: already on every face of {(editable.Count == 1 ? "that brush" : $"those {editable.Count} brushes")}{skipped}.");
            return;
        }

        Context.Execute(command);
        int same = editable.Count - changing.Count;
        Context.SetStatus($"Applied {material} to {PanelUi.Count(changing.Count, "brush", "brushes")}"
            + (same > 0 ? $" ({same} already showed it)" : "") + $"{skipped}.");
    }

    /// <summary>True in the surface tool with brush faces picked: materials and textures go on those faces. Picked faces
    /// are shown in that tool only (and leaving it drops them), so nothing goes on faces the views do not show.</summary>
    private static bool FacesPicked() => InSurfaceTool && GeometrySelection.Instance.Faces.Count > 0;

    private static bool InSurfaceTool => ToolManager.Instance?.Active is FaceTool;

    /// <summary>The placement point: the context's own, else the surface under the active view's centre, else in
    /// front of the camera.</summary>
    private Vec3 PlacementPoint(MapProject p)
    {
        if (Context.PlacementPoint is { } point)
        {
            return point();
        }

        if (ViewportLayout.Instance?.Active is { } view && view.CursorPoint(view.Size / 2) is { } hit)
        {
            return hit;
        }

        Workspace w = p.Workspace;
        return w.CameraLocation + w.CameraRotation.Axes().Forward * FallbackDistance;
    }

    // ---- usages, favourites, sounds -------------------------------------------------------------------------------

    private void FindUsages()
    {
        if (Selected is not { } s)
        {
            return;
        }

        List<AssetUse> uses = AssetEdits.Usages(Context.Project, s.Path);
        if (uses.Count == 0)
        {
            Context.SetStatus($"Nothing in this map uses {s.Path}.");
            return;
        }

        foreach (AssetUse u in uses)
        {
            Context.Log($"{s.Path}: {u.Object.Kind} {u.Object.Id} ({u.How})");
        }

        List<ObjectRef> refs = uses.Select(u => u.Object).ToList();
        Context.Selection.Set(refs);
        Context.Focus(refs);
        Context.SetStatus($"{uses.Count} object(s) use {s.Path}.");
    }

    private void ToggleFavourite()
    {
        if (SelectedEntry is { } e)
        {
            AssetService.Instance.ToggleFavourite(e);
            Context.SetStatus(AssetService.Instance.IsFavourite(e) ? $"Added {e.Name} to the favourites." : $"Removed {e.Name} from the favourites.");
        }
    }

    private void PlaySound()
    {
        if (SelectedEntry is not { Kind: AssetKind.Sound, File: { } file } e || !File.Exists(file))
        {
            Context.SetStatus("This sound has no file to play.");
            return;
        }

        AudioStream? stream = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".wav" => AudioStreamWav.LoadFromFile(file),
            ".ogg" => AudioStreamOggVorbis.LoadFromFile(file),
            ".mp3" => AudioStreamMP3.LoadFromFile(file),
            _ => null,
        };
        if (stream is null)
        {
            Context.SetStatus($"Cannot play {Path.GetFileName(file)}.");
            return;
        }

        if (_player is null)
        {
            _player = new AudioStreamPlayer { Name = "AssetPreview" };
            AddChild(_player);
        }

        _player.Stream = stream;
        _player.Play();
        Context.SetStatus($"Playing {e.Path} ({e.Duration:0.0} s).");
    }

    private void StopSound() => _player?.Stop();

    // ---- import -----------------------------------------------------------------------------------------------------

    private void ImportMesh()
    {
        if (Context.Session.Folder is null)
        {
            Context.SetStatus("Save the map first: imported meshes go into its assets folder.");
            return;
        }

        if (Arg("--asset-import=") is { } file)
        {
            Import(file);
            return;
        }

        Dialogs.OpenFile("Import mesh", s_importFilters, Import);
    }

    private void Import(string file)
    {
        string? folder = Context.Session.Folder;
        if (folder is null)
        {
            return;
        }

        var options = new MeshImportOptions { Collision = ImportCollision };
        try
        {
            MeshImportResult r = MeshImporter.Import(folder, file, options);
            foreach (string note in r.Notes)
            {
                Context.Log($"Import {Path.GetFileName(file)}: {note}", Severity.Warning);
            }

            Context.Log($"Imported {r.Path}: {r.Triangles} triangles, {r.Hulls} collision hull(s) ({r.CollisionTriangles} triangles).");
            Selected = (AssetKind.Mesh, r.Path);
            AssetService.Instance.ProjectAssetsChanged();
            Context.SetStatus($"Imported {r.Path}.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException or ArgumentException or System.Text.Json.JsonException)
        {
            Context.Log($"Import {Path.GetFileName(file)} failed: {e.Message}", Severity.Error);
            Context.SetStatus($"Import failed: {e.Message}");
        }
    }

    // ---- drag and drop ------------------------------------------------------------------------------------------------

    /// <summary>Starts dragging a mesh, material or texture: <see cref="AssetDragData"/> with the tile's thumbnail. The
    /// views take the drop (<see cref="AssetDropTarget"/>).</summary>
    private Variant DragData(Vector2 at)
    {
        int index = _list.GetItemAtPosition(at, exact: true);
        if (index < 0 || index >= _shown.Count || !AssetDrag.CanDrag(_shown[index].Kind))
        {
            return default;
        }

        _dragged = true;
        Select(index);
        return AssetDragData.Start(_list, AssetDrag.Of(_shown[index]), _list.GetItemIcon(index));
    }
}
