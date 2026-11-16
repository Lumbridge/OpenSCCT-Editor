using System.Text.Json;
using Godot;
using OpenScct.Editor.Core.Doors;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Snap;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Prefabs;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Session;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Shell.Shots;

[ShellPart(Order = 121)]
public partial class DoorPlacementChecks : Node, IShellPart
{
    private readonly List<string> passed = [];
    private string output = "";
    public void Attach(MainWindow window, EditorContext context)
    {
        string? target = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--door-placement-checks="))?["--door-placement-checks=".Length..];
        if (target is null || !UserData.IsSeparate) return;
        output = Path.GetFullPath(target); window.AddChild(this);
        EditorLifetime.Defer(() => { _ = Run(context); });
    }
    private void Check(bool ok, string text)
    {
        if (!ok) throw new InvalidOperationException(text);
        passed.Add(text); GD.Print("DOOR CHECK: " + text);
    }
    private async Task Wait(double seconds = .2) => await ToSignal(GetTree().CreateTimer(seconds), Godot.Timer.SignalName.Timeout);
    private static Brush BoxBrush(string name, Vec3 size, Vec3 at) => new(name) { Oper = CsgOper.Add, Shape = Box.Shape(size), Location = at };
    private async Task Run(EditorContext context)
    {
        string? error = null;
        try
        {
            await Wait(1.5);
            var project = MapProject.CreateDefault("DoorPlacementCheck", "Door placement checks");
            project.Brushes.Add(new Brush("Room") { Oper = CsgOper.Subtract, Shape = Box.Shape(new(2000, 2000, 1000)), Location = new(0, 0, 500) });
            project.Brushes.Add(BoxBrush("Wall", new(64, 1000, 500), new(0, 0, 250)));
            context.ReplaceSession(new EditorSession(project));
            await Wait(.5);
            var tools = ToolManager.Instance!;
            tools.GridSnap = false; tools.NeighbourSnap = false;
            var view = ViewportLayout.Instance!.Views.First(v => !v.Cam.IsOrtho);
            view.Cam.Location = new(-600, 0, 200); view.Cam.Yaw = 0; view.Cam.Pitch = 0; view.Cam.Apply();
            var at = view.Cam.ToScreen(new(-32, 0, 24))!.Value;
            tools.Activate("doors");
            DoorTools.Tool.Input(view, new InputEventMouseMotion { Position = at });
            foreach (var (name, count) in new[] { ("double_sliding_door", 2), ("sliding_door", 1), ("glazed_door", 1), ("double_sliding_door", 2) })
            {
                PrefabsPanel.ShowPrefab(name);
                await Wait();
                var selected = DoorTools.Tool.Prefab;
                Check(selected is not null && selected.LeafCount == count, "Selected " + name + " immediately updates the outline to " + count + " leaf/leaves");
                Check(selected!.PreviewProject!.Actors.Where(a => a.Door is not null).All(a => !a.Door!.Motion.StartsWith("swing")), "Selected " + name + " uses sliding movement");
                Check(Math.Abs(Vec3.Dot(selected.Rotation.Axes().Forward, Vec3.UnitX)) > .99, "Selected " + name + " faces the wall instead of standing sideways");
            }
            var assembly = DoorTools.Tool.Prefab!;
            var palette = MainWindow.Instance!.LeftDock.FindChildren("*", "ItemList", true, false).OfType<ItemList>().First(l => l.GetPath().ToString().Contains("Prefabs"));
            Check(Enumerable.Range(0, palette.ItemCount).All(i => !palette.GetItemText(i).Contains("hinged", StringComparison.OrdinalIgnoreCase) && !palette.GetItemText(i).Contains("swing", StringComparison.OrdinalIgnoreCase)), "Swinging door presets are absent from the palette");
            var widthLabel = MainWindow.Instance.LeftDock.FindChildren("*", "Label", true, false).OfType<Label>().First(l => l.Text == "Width" && l.GetPath().ToString().Contains("Prefabs"));
            widthLabel.GetParent().GetChildren().OfType<SpinBox>().First().Value = 480;
            Check(assembly.PreviewProject!.Actors.Where(a => a.Door is not null).All(a => a.Door!.Width == 240), "Changing prefab width updates the outline without moving the pointer");
            var before = context.Project.Actors.Count(a => a.Door is not null);
            DoorTools.Tool.Input(view, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = at });
            Check(context.Project.Actors.Count(a => a.Door is not null) == before + 2, "Door-mode click places the selected double-door assembly");
            Check(context.Project.Prefabs.Count == 1, "Door-mode placement preserves the linked prefab instance");
            context.Run("edit.undo");
            PrefabsPanel.ShowPrefab("double_sliding_door");
            DoorTools.Tool.Input(view, new InputEventMouseMotion { Position = at });
            assembly = DoorTools.Tool.Prefab!;
            UiInput.Move(GetTree().Root, view.GetGlobalRect().Position + at);
            await Wait();
            var data = new PrefabDragData("Double sliding doors", _ => assembly.Commit()) { DoorAssembly = assembly };
            Check(AssetDropTarget.Instance!.CanDrop(view, at, Variant.From(data)), "Assembly drag has a valid oriented preview");
            Check(assembly.LeafCount == 2, "Assembly drag uses the same two-leaf outline as door mode");
            AssetDropTarget.Instance.Drop(view, at, Variant.From(data));
            Check(context.Project.Actors.Count(a => a.Door is not null) == before + 2, "Dropping places both linked door leaves");
            Check(context.Project.Actors.Where(a => a.Door is not null).All(a => Math.Abs(Vec3.Dot(a.Rotation.Axes().Forward, Vec3.UnitX)) > .99), "Dropped leaves stay parallel to the wall");
            await Wait(.3);
            await UiDriver.Instance!.Shot(ApiJson.Obj(("dir", ApiJson.Str(Path.GetDirectoryName(output)!)), ("name", ApiJson.Str("door-assembly"))));

            var cases = new[]
            {
                ("bottom", BoxBrush("Floor", new(128, 512, 16), new(0, 0, -8)), new Vec3(0, 0, 114), new Vec3(0, 0, -2)),
                ("top", BoxBrush("Ceiling", new(128, 512, 16), new(0, 0, 246)), new Vec3(0, 0, 116), new Vec3(0, 0, 2)),
                ("right side", BoxBrush("Side", new(128, 16, 1024), new(0, 112, 512)), new Vec3(0, 30, 112), new Vec3(0, 2, 0)),
                ("left side", BoxBrush("Side", new(128, 16, 1024), new(0, -112, 512)), new Vec3(0, -30, 112), new Vec3(0, -2, 0)),
            };
            foreach (var (name, target, location, expected) in cases)
            {
                var snapMap = MapProject.CreateDefault("SnapCheck", "Snap check"); snapMap.Brushes.Add(target);
                context.ReplaceSession(new EditorSession(snapMap));
                var actor = DoorCommands.Create(snapMap, new DoorDesign(), location);
                tools.NeighbourSnap = true; tools.NeighbourSnapMove = true;
                var snap = new NeighbourDrag(tools, view, SnapUse.Move, [], () => null);
                Vec3 offset = DoorTool.SnapPreview(view, [actor], [], snap, Rot.Zero);
                Check((offset - expected).Length < .01, "Door " + name + " edge snaps exactly to the neighbour (" + offset + ")");
                tools.NeighbourSnap = false;
                Check(DoorTool.SnapPreview(view, [actor], [], snap, Rot.Zero) == Vec3.Zero, "Door " + name + " edge stays free with snapping off");
            }
        }
        catch (Exception ex) { error = ex.ToString(); GD.PrintErr(error); }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = error is null, checks = passed, error }, new JsonSerializerOptions { WriteIndented = true }));
        EditorLifetime.Quit(GetTree(), error is null ? 0 : 1);
    }
}
