using Godot;
using OpenScct.Content.Materials;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Assets.Import;
using OpenScct.Editor.Core.Identity;
using OpenScctEditor.Assets;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Checks;

// Compiled only with MaterialWindowChecks=true. Run on an isolated, invisible desktop.
[ShellPart(Order = 999)]
public sealed class NativeMaterialChecks : IShellPart
{
    public void Attach(MainWindow window, EditorContext context)
    {
        if (System.Environment.GetEnvironmentVariable("OSE_NATIVE_MATERIAL_CHECKS") == "1") _ = Run(window, context);
    }

    private static async Task Run(MainWindow main, EditorContext context)
    {
        async Task Frames(int count) { for (int i = 0; i < count; i++) await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame); }
        try
        {
            await Frames(3);
            string? fixture = System.Environment.GetEnvironmentVariable("OSE_NATIVE_MATERIAL_FIXTURE");
            string folder = fixture ?? Path.Combine(UserData.Folder, "native-material-fixture");
            AuthoredMaterial definition;
            string renderDir;
            if (fixture is not null)
            {
                var library = ProjectAssets.For(folder).FindMaterial("MyLevel.Textures.Material")!;
                definition = AuthoredMaterial.From(library.Flags["authored"])!; renderDir = library.RenderDir;
            }
            else
            {
                renderDir = Path.Combine(folder, "assets"); Directory.CreateDirectory(Path.Combine(renderDir, "textures"));
                File.WriteAllBytes(Path.Combine(renderDir, "textures/test.png"), new RgbaImage(8, 8, Enumerable.Range(0, 64).SelectMany(_ => new byte[] { 128, 128, 255, 255 }).ToArray()).EncodePng());
                definition = new();
                foreach (string slot in AuthoredMaterial.Slots) definition.Textures[slot] = new() { File = "textures/test.png", Srgb = true };
            }
            var draft = new TextureSetDraft { Name = "Material", Material = definition };
            foreach (var entry in definition.Textures) draft.Sources[entry.Key] = Path.Combine(renderDir, entry.Value.File);
            var dialog = new MaterialWindow(folder, [draft], _ => { }, "MyLevel.Textures.Material");
            Dialogs.Host!.AddChild(dialog);
            if (main.GetWindow().GuiEmbedSubwindows) throw new Exception("This check requires native windows");
            int checks = 0;
            void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
            void Fits(Rect2I usable)
            {
                Check(!dialog.IsEmbedded(), "The material dialog became embedded");
                var frame = new Rect2I(dialog.GetPositionWithDecorations(), dialog.GetSizeWithDecorations());
                Check(usable.Encloses(frame), $"Native frame {frame} escapes work area {usable}");
                Check(dialog.GetVisibleRect().Encloses(dialog.GetOkButton().GetGlobalRect()), "Save/Import button escapes the client area");
                var cancel = Descendants(dialog).OfType<Button>().Single(b => b.Text == "Cancel");
                Check(dialog.GetVisibleRect().Encloses(cancel.GetGlobalRect()), "Cancel button escapes the client area");
            }
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720), new Vector2I(1024, 768) })
                foreach (float scale in new[] { 1f, 1.5f, 2f })
                {
                    var usable = new Rect2I(new(40, 40), size);
                    dialog.ContentScaleFactor = scale;
                    dialog.OpenWithin(usable); await Frames(12); Fits(usable);
                    Vector2I initial = dialog.Size;
                    Descendants(dialog).OfType<Button>().Single(b => b.Text == "Use recommended colour spaces").EmitSignal(BaseButton.SignalName.Pressed);
                    await Frames(12); Fits(usable);
                    Check(dialog.Size == initial, "Rebuilding texture fields changed the native frame size");
                    GD.Print($"MATERIAL_NATIVE PASS work={size} scale={scale} frame={dialog.GetSizeWithDecorations()} client={dialog.Size}");
                    await main.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using Image image = dialog.GetTexture().GetImage(); image.SavePng(Path.Combine(UserData.Folder, $"material-{size.X}-{size.Y}-{scale}.png"));
                }
            GD.Print($"MATERIAL_NATIVE OK {checks} checks; native work areas, 100/150/200% scale, loaded textures, rebuilds and reachable buttons");
            dialog.QueueFree();
            main.GetTree().Quit();
        }
        catch (Exception ex) { GD.PrintErr("MATERIAL_NATIVE FAIL " + ex); main.GetTree().Quit(1); }
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren(true)) { yield return child; foreach (Node nested in Descendants(child)) yield return nested; }
    }
}
