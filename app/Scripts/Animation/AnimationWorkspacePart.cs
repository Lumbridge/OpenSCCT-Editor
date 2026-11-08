using Godot;
using OpenScctEditor.Panels;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Animation;

[ShellPart(Order = 90)]
public sealed class AnimationWorkspacePart : IShellPart
{
    public static AnimationStudio? Studio { get; private set; }
    public static bool Active => Studio?.Visible == true;
    private static Action<bool>? _show;
    public static void ShowWorkspace(bool animation = true) => _show?.Invoke(animation);
    public void Attach(MainWindow window, EditorContext context)
    {
        Studio = new AnimationStudio { Name = "AnimationStudio", Visible = false };
        window.WorkspaceArea.AddChild(Studio);
        var previousVisibility = new Dictionary<CanvasItem, bool>();
        Button map = null!, animation = null!;
        map = PanelUi.Button(window.ToolRow, "Map", "Map workspace", () => Show(false));
        animation = PanelUi.Button(window.ToolRow, "Animation", "Animation Studio", () => Show(true));
        map.Name = "MapWorkspace"; animation.Name = "AnimationWorkspace";
        context.AddAction(new EditorAction("animation.workspace", "Animation Studio", "View/Workspace", () => Show(true)) { Order = 120 });
        context.AddAction(new EditorAction("animation.map", "Map Workspace", "View/Workspace", () => Show(false)) { Order = 130 });
        _show = Show;
        context.AddAction(new EditorAction("animation.example", "Animation Example", "Assets/Animation", () => { Show(true); Studio.CreateExample(); }) { Description = "Open Animation Studio and create a three-bone example to learn posing and keyframes." });
        context.AddAction(new EditorAction("animation.spy", "Import Spy Rig", "Assets/Animation", () => { Show(true); Studio.ImportStock(true); }));
        context.AddAction(new EditorAction("animation.merc", "Import Merc Rig", "Assets/Animation", () => { Show(true); Studio.ImportStock(false); }));
        context.AddAction(new EditorAction("animation.firstPerson.spy", "Spy First Person", "Assets/Animation", () => { Show(true); Studio.OpenFirstPerson(OpenScct.Animation.FirstPersonRole.Spy); }));
        context.AddAction(new EditorAction("animation.firstPerson.merc", "Merc First Person", "Assets/Animation", () => { Show(true); Studio.OpenFirstPerson(OpenScct.Animation.FirstPersonRole.Merc); }));
        string[] arguments = OS.GetCmdlineUserArgs();
        if (arguments.Any(a => a.StartsWith("--animation", StringComparison.Ordinal)))
            Callable.From(() =>
            {
                Show(true);
                string? role = arguments.FirstOrDefault(a => a.StartsWith("--animation-first-person=", StringComparison.Ordinal))?.Split('=')[1];
                if (Enum.TryParse(role, true, out OpenScct.Animation.FirstPersonRole firstPerson)) Studio.OpenFirstPerson(firstPerson);
            }).CallDeferred();
        void Show(bool animationMode)
        {
            window.MapWorkspace.Visible = !animationMode; Studio!.Visible = animationMode;
            if (animationMode)
            {
                foreach (CanvasItem item in window.ToolRow.GetChildren().OfType<CanvasItem>())
                {
                    if (item == map || item == animation) continue;
                    previousVisibility.TryAdd(item, item.Visible); item.Hide();
                }
            }
            else
            {
                foreach ((CanvasItem item, bool visible) in previousVisibility) if (GodotObject.IsInstanceValid(item)) item.Visible = visible;
                previousVisibility.Clear();
            }
            if (animationMode) Studio.GrabFocus();
        }
    }
}
