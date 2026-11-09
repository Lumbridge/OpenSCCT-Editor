using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Doors;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Session;
using OpenScctEditor.Core;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Checks;

/// <summary>Exercises actual viewport-tool events against rendered Godot cameras, in an isolated background run.</summary>
[ShellPart(Order = 120)]
public sealed partial class GeInteractionChecks : Node, IShellPart
{
    private EditorContext _ctx = null!;
    private readonly List<string> _results = [];
    public void Attach(MainWindow window, EditorContext context)
    {
        if (!OS.GetCmdlineUserArgs().Contains("--ge-interaction-checks")) return;
        _ctx = context; window.AddChild(this);
        Callable.From(() => { _ = Run(); }).CallDeferred();
    }

    private async Task Run()
    {
        int code = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); _results.Add("PASS " + message); GD.Print("PASS " + message); }
        async Task Frames() { for (int i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        MapProject Map(bool ge)
        {
            var p = MapProject.CreateDefault("GeUIA", "GE interaction checks");
            p.Brushes.Add(new("Room") { Oper = CsgOper.Subtract, Location = new(0, 0, 512), Shape = Box.Shape(new(2048, 2048, 1024)) });
            p.Brushes.Add(new("Platform") { Oper = CsgOper.Add, Location = new(0, 0, 128), Shape = Box.Shape(new(512, 256, 256)) });
            if (ge) p.Ges.Add(new("Ledge", GeKinds.LedgeGrab) { Points = [new(-192, -128, 256), new(128, -128, 256)] });
            return p;
        }
        try
        {
            await Frames();
            ToolManager tools = ToolManager.Instance!;
            var draw = tools.Tools.OfType<GeDrawTool>().Single();
            var view = ViewportLayout.Instance!.Views.Single(v => v.Kind == ViewKind.Perspective);
            tools.NeighbourSnap = true; tools.NeighbourSnapMove = true; tools.NeighbourSnapResize = true;
            tools.GridSnap = true;
            Vec3 target = new(256, -128, 256);
            Vec3[] cameras = [new(-650, -650, 320), new(650, -650, 550), new(-400, -650, 900), new(0, -700, 300), new(700, -350, 800)];
            foreach (Vec3 camera in cameras)
            {
                _ctx.ReplaceSession(new EditorSession(Map(true)));
                tools.Activate("select");
                _ctx.Selection.Set([ObjectRef.Ge("Ledge")]);
                Vec3 direction = new Vec3(0, -128, 256) - camera;
                view.Cam.Location = camera; view.Cam.Yaw = Math.Atan2(direction.Y, direction.X) * Rot.UnitsPerTurn / Math.Tau;
                view.Cam.Pitch = Math.Atan2(direction.Z, Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y)) * Rot.UnitsPerTurn / Math.Tau;
                view.Cam.Apply(); await Frames(); _ = GameplayState.Instance.World;
                Vector2 start = view.Cam.ToScreen(new(128, -128, 256))!.Value, end = view.Cam.ToScreen(target)!.Value + new Vector2(2, -1);
                Check(tools.Active.Input(view, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = start }), "Select consumes the selected GE endpoint press at " + camera);
                tools.Active.Input(view, new InputEventMouseMotion { Position = end, Relative = end - start });
                tools.Active.Input(view, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = end });
                Check((_ctx.Project.FindGe("Ledge")!.Points[1] - target).Length < .01, "GE endpoint snaps exactly to the visible corner from " + camera + ": " + _ctx.Project.FindGe("Ledge")!.Points[1]);
                Check(_ctx.Project.Ges.Count == 1 && tools.Active.Id == "select", "Handle drag creates no GE and keeps Select active");
                _ctx.Session.Commands.Undo();
                Check(_ctx.Project.FindGe("Ledge")!.Points[1] == new Vec3(128, -128, 256), "Undo restores the endpoint");
                _ctx.Session.Commands.Redo();
                Check((_ctx.Project.FindGe("Ledge")!.Points[1] - target).Length < .01, "Redo restores the exact snapped endpoint");

                _ctx.ReplaceSession(new EditorSession(Map(false)));
                tools.GeKind = GeKinds.LedgeGrab; tools.Activate(draw.Id); _ = GameplayState.Instance.World;
                Vector2 lip = view.Cam.ToScreen(new(0, -128, 256))!.Value + new Vector2(0, -6);
                draw.Input(view, new InputEventMouseMotion { Position = lip });
                draw.Input(view, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = lip });
                draw.Input(view, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = lip });
                Check(_ctx.Project.Ges.Count == 1 && _ctx.Project.Ges[0].Points.Take(2).All(p => Math.Abs(p.Y + 128) < .01 && Math.Abs(p.Z - 256) < .01), "Ledge placement finds the visible edge with a six-pixel cursor miss from " + camera);
            }
            _ctx.ReplaceSession(new EditorSession(Map(true))); tools.Activate("select");
            _ctx.Project.FindGe("Ledge")!.Editor.Locked = true; _ctx.Selection.Set([ObjectRef.Ge("Ledge")]);
            Vector2 locked = view.Cam.ToScreen(new(128, -128, 256))!.Value;
            tools.Active.Input(view, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = locked });
            tools.Active.Input(view, new InputEventMouseMotion { Position = locked + new Vector2(30, 0) });
            tools.Active.Input(view, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = locked + new Vector2(30, 0) });
            Check(_ctx.Project.FindGe("Ledge")!.Points[1] == new Vec3(128, -128, 256), "Locked GEs have no draggable handles");
            foreach (int yaw in new[] {0,8192,16384}) foreach (double thickness in new[] {16.0,64.0,128.0})
            {
                var wallMap = Map(false); wallMap.Brushes.RemoveAt(1);
                Rot turn = new(0,yaw,0);
                wallMap.Brushes.Add(new("DoorWall") { Oper = CsgOper.Add, Shape = Box.Shape(new(512,thickness,512)), Location = new(0,0,256), Rotation = turn });
                _ctx.ReplaceSession(new EditorSession(wallMap));
                Vec3 point = turn.Rotate(new Vec3(0,-thickness/2,112));
                Vec3 camera = turn.Rotate(new Vec3(0,-700,220));
                Vec3 forward = point-camera;
                view.Cam.Location=camera; view.Cam.Yaw=Math.Atan2(forward.Y,forward.X)*Rot.UnitsPerTurn/Math.Tau;
                view.Cam.Pitch=Math.Atan2(forward.Z,Math.Sqrt(forward.X*forward.X+forward.Y*forward.Y))*Rot.UnitsPerTurn/Math.Tau;
                view.Cam.Apply(); await Frames();
                var settings = new DoorDesign { Thickness=8, FrameDepth=192 };
                var door = DoorTool.Preview(view,view.Cam.ToScreen(point)!.Value,settings,0);
                Check(door.Problem is null && door.Actor?.Door is { } design && Math.Abs(design.Thickness-thickness)<.01 && Math.Abs(design.FrameDepth-thickness-4)<.01,
                    $"Door panel and frame fit {thickness}-unit wall at yaw {yaw}: {door.Problem}");
                Check(settings.Thickness==8 && settings.FrameDepth==192,"Wall fitting leaves manual palette defaults unchanged");
            }
            _ctx.ReplaceSession(new EditorSession(Map(true)));
            _ctx.Project.FindGe("Ledge")!.Editor.Locked = false;
            _ctx.Selection.Set([ObjectRef.Ge("Ledge")]);
            await Frames();
            for (int i = 0; i < 90; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(Path.Combine(UserData.Folder, "ge-selected-handles.png"));
            tools.Activate("face");
            await Frames();
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(Path.Combine(UserData.Folder, "ge-interactions.png"));
        }
        catch (Exception ex) { _results.Add("FAIL " + ex); GD.PrintErr(ex); code = 1; }
        File.WriteAllLines(Path.Combine(UserData.Folder, "ge-interaction-checks.txt"), _results);
        EditorLifetime.Quit(GetTree(), code);
    }
}
