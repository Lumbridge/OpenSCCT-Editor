using Godot;
using OpenScct.Content.Materials;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Assets.Import;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Assets;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

public partial class AssetBrowserPanel
{
    private void RegisterMaterialActions()
    {
        Context.AddAction(new EditorAction("assets.importTextures", "Import Textures…", "Assets", () => ImportTextures(false))
            { Order = 210, Description = "Import images or a PBR texture set, review channels and create ready-to-use materials" });
        Context.AddAction(new EditorAction("assets.importAnimatedTexture", "Import Animated Texture…", "Assets", () => ImportTextures(true))
            { Order = 211, Description = "Import numbered frame images as a flipbook atlas with looping, ping-pong and FPS controls" });
        Context.AddAction(new EditorAction("assets.createMaterial", "Create Material…", "Assets", () => WithSavedMap(() => OpenMaterial([new TextureSetDraft()])) )
            { Order = 212, Description = "Create a material with colour, normal, bump, depth, shine, emission and animated texture controls" });
        Context.AddAction(new EditorAction("assets.editMaterial", "Edit Material…", "Assets", EditMaterial)
            { Order = 213, IsEnabled = () => Selected is { Kind: AssetKind.Material or AssetKind.Texture }, Description = "Edit a map-local material, or create an editable copy of a library material" });
        Context.AddAction(new EditorAction("assets.applyMeshMaterial", "Apply Material to Mesh Sections…", "Assets", ApplyMeshMaterial)
            { Order = 114, IsEnabled = () => Selected is { Kind: AssetKind.Material or AssetKind.Texture } && Context.Selection.Items.Any(r => r.Kind == OpenScct.Editor.Core.Model.ObjectKind.Actor),
                Description = "Apply the current material to all sections or one section of the selected static mesh actors" });
    }

    private void ApplyMeshMaterial()
    {
        if (Selected is not { } selected || AssetService.Instance.SurfaceMaterial(selected.Kind, selected.Path).Material is not { } material || Dialogs.Host is null) return;
        var dialog = new ConfirmationDialog { Title = "Apply Material to Mesh Sections", Exclusive = false, OkButtonText = "Apply" };
        var body = new VBoxContainer(); body.AddChild(new Label { Text = "Section index (−1 applies to every section)" });
        var section = new SpinBox { MinValue = -1, MaxValue = 255, Value = -1, Step = 1 }; body.AddChild(section); dialog.AddChild(body);
        body.AddChild(new Label { Text = "Displacement affects collision" });
        var collision = new OptionButton(); foreach (string label in new[] { "Use material default", "Visual only", "Use final displaced shape" }) collision.AddItem(label); body.AddChild(collision);
        var references = Context.Selection.Items.ToList(); object session = Context.Session;
        dialog.Confirmed += () => { if (ReferenceEquals(session, Context.Session) && AssetEdits.ApplyMeshMaterial(Context.Project, references, material, AssetService.Instance.Resolver, (int)section.Value, collision.Selected == 0 ? null : collision.Selected == 2) is { } command) Context.Execute(command); dialog.QueueFree(); };
        dialog.Canceled += dialog.QueueFree; Dialogs.Host.AddChild(dialog); dialog.PopupCentered(new(400, 230));
    }

    private void WithSavedMap(Action ready)
    {
        if (Context.Session.Folder is null) Documents.Instance?.Save(ready);
        else ready();
    }

    private void ImportTextures(bool sequence) => WithSavedMap(() =>
    {
        object session = Context.Session;
        void Picked(string[] files) => BackgroundJobs.Run("Review textures", () =>
        {
            if (sequence) return new List<TextureSetDraft> { TextureImporter.PreviewSequence(files) };
            return TextureImporter.Preview(files).ToList();
        }, drafts => { if (ReferenceEquals(session, Context.Session) && drafts.Count > 0) OpenMaterial(drafts); });
        if (Arg("--texture-import=") is { } file) Picked([file]);
        else Dialogs.OpenFiles(sequence ? "Choose animation frames" : "Import textures and material sets", ["*.png, *.jpg, *.jpeg, *.bmp, *.webp ; Images"], Picked);
    });

    private void EditMaterial() => WithSavedMap(() =>
    {
        if (Selected is not { } selected) return;
        string? path = AssetService.Instance.SurfaceMaterial(selected.Kind, selected.Path).Material;
        if (path is null || AssetService.Instance.Resolver.FindMaterial(path) is not { } library) return;
        AuthoredMaterial definition = AuthoredMaterial.From(library.Flags["authored"]) ?? new();
        if (definition.Textures.Count == 0 && library.Texture is { } baseTexture)
            definition.Textures["baseColor"] = new() { File = baseTexture, Srgb = true };
        var draft = new TextureSetDraft { Name = AssetPaths.Split(path).Name, Material = definition };
        foreach ((string slot, MaterialTexture binding) in definition.Textures) draft.Sources[slot] = Path.Combine(library.RenderDir, binding.File);
        OpenMaterial([draft], library.Origin == AssetPaths.LocalPackage ? path : null);
    });

    private void OpenMaterial(IEnumerable<TextureSetDraft> drafts, string? existing = null)
    {
        if (Context.Session.Folder is not { } folder || Dialogs.Host is null) return;
        if (Dialogs.Host.GetChildren().OfType<MaterialWindow>().FirstOrDefault(w => !w.IsQueuedForDeletion()) is { } open) { open.PopupCentered(); return; }
        var window = new MaterialWindow(folder, drafts, RevealMaterial, existing);
        Dialogs.Host.AddChild(window); window.PopupCentered(new(1100, 760));
    }

    private void RevealMaterial(string path)
    {
        _argAsset = path;
        _search.Text = ""; _treeFilter = null; _inMap.SetPressedNoSignal(false);
        _tabs.CurrentTab = 0;
        if (_origin.ItemCount > 0) _origin.Select(0);
        Selected = (AssetKind.Material, path);
        DockTabs.Reveal(this);
        AssetService.Instance.ProjectAssetsChanged();
    }
}
