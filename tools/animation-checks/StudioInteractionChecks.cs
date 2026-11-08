using Godot;
using OpenScct.Animation;
using OpenScct.Editor.Core.Animation;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Shell.Shots;

namespace OpenScctEditor.Animation;

// Run in an isolated background editor with --animation-ui-checks. Uses the real controls and synthetic input.
public partial class AnimationStudio
{
    private async Task RunInteractionChecks()
    {
        int checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks++; GD.Print("PASS " + message); }
        async Task Frames(int count = 4) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Click(Control control) { UiInput.Click(GetTree().Root, control.GetGlobalRect().GetCenter()); await Frames(); }
        async Task Shot(string name)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(Path.Combine(UserData.Folder, name + ".png"));
        }
        try
        {
            await Frames();
            AnimationWorkspacePart.ShowWorkspace(false);
            CommandPalette.Instance!.OpenSearch("commands: Animation Example"); await Frames();
            await Click(DiscoveryNavigation.Descendants(CommandPalette.Instance).OfType<Button>().Single(b => b.Text == "Run"));
            Check(AnimationWorkspacePart.Active && State.Project.Scene.Count == 1, "palette Run opens example in visible studio");
            Check(!State.Playing && State.Time == 0, "new animation has never played");
            Vector2 start = _seek.GlobalPosition + new Vector2(_seek.Size.X * .15f, _seek.Size.Y * .5f);
            Vector2 end = _seek.GlobalPosition + new Vector2(_seek.Size.X * .75f, _seek.Size.Y * .5f);
            UiInput.Button(GetTree().Root, start, MouseButton.Left, true); await Frames();
            UiInput.Move(GetTree().Root, end, 5); await Frames();
            UiInput.Button(GetTree().Root, end, MouseButton.Left, false); await Frames();
            Check(State.Time > State.Duration * .5f && !State.Playing, "dragging scrub slider works before first Play");
            Control ruler = (Control)_timeline.FindChild("AnimationDopeSheet", true, false);
            Vector2 rulerStart = ruler.GlobalPosition + new Vector2(190, 10), rulerEnd = rulerStart + new Vector2(100, 0);
            UiInput.Button(GetTree().Root, rulerStart, MouseButton.Left, true); await Frames(); float firstSample = State.Time;
            UiInput.Move(GetTree().Root, rulerEnd, 5); await Frames();
            UiInput.Button(GetTree().Root, rulerEnd, MouseButton.Left, false); await Frames();
            Check(State.Time > firstSample + .5f, "timeline ruler scrubs continuously before first Play");
            TreeItem upper = _rig.GetRoot().GetChild(0).GetChild(0);
            UiInput.Click(GetTree().Root, _rig.GlobalPosition + _rig.GetItemAreaRect(upper).GetCenter()); await Frames();
            Check(State.Bone == 1 && _stage.BonePoints.ContainsKey(1), "hierarchy click selects the stage bone");
            foreach (string tool in new[] { "move", "rotate", "scale" })
            {
                await Click(_poseTools[tool]);
                Check(State.TransformTool == tool && _poseTools[tool].ButtonPressed && _poseTools.Count(p => p.Value.ButtonPressed) == 1, tool + " button has exclusive active feedback");
                var handle = _stage.Handles.OrderByDescending(h => _stage.Handles.Where(other => other.Axis != h.Axis)
                    .Select(other => Geometry2D.GetClosestPointToSegment((h.A + h.B) * .5f, other.A, other.B).DistanceSquaredTo((h.A + h.B) * .5f)).DefaultIfEmpty(100).Min()).First();
                Vector2 at = _stage.GlobalPosition + (handle.A + handle.B) * .5f;
                Vector2 to = at + (handle.B - handle.A).Normalized() * 32;
                BoneTransform before = State.Pose()[State.Bone];
                UiInput.Button(GetTree().Root, at, MouseButton.Left, true); await Frames();
                UiInput.Move(GetTree().Root, to, 5); await Frames();
                UiInput.Button(GetTree().Root, to, MouseButton.Left, false); await Frames();
                Check(State.Pose()[State.Bone] != before, tool + " handle drag changes and keys the bone");
            }
            await Shot("example-tools");
            string rig = State.RigId!;
            State.Session.RemoveRig(rig); await Frames();
            Check(_stage.Empty, "removing last scene rig clears models, bones and handles");
            State.Session.Undo(); await Frames(); Check(State.Project.Scene.Count == 1 && !_stage.Empty, "Undo restores removed scene item");
            State.Open(new()); await Frames(); Check(_stage.Empty && _rig.GetRoot().GetChildCount() == 0, "New clears old stage and hierarchy");
            AnimationWorkspacePart.ShowWorkspace(false);
            DiscoveryNavigation.ShowAction(EditorContext.Instance.FindAction("animation.example")!); await Frames();
            Check(AnimationWorkspacePart.Active && State.Project.Scene.Count == 0, "Show me reveals studio without creating an example");
            Control? located = GetNodeOrNull<Control>(NavigationHighlight.Instance!.TargetPath!);
            Check(located is Button { Text: "Create example rig" }, "Show me highlights the exact example creation button");
            Check(NavigationHighlight.Instance.Active, "Show me highlights example creation control");
            NavigationHighlight.Instance._Input(new InputEventKey { Pressed = true, Keycode = Key.Escape });
            Check(!NavigationHighlight.Instance.Active, "Escape dismisses palette highlight");
            foreach (EditorTutorial lesson in TutorialCatalog.All.Where(t => t.Id.StartsWith("animation-")))
            {
                TutorialRunner.Instance!.Start(lesson); await Frames();
                Check(AnimationWorkspacePart.Active && TutorialRunner.Instance.LessonId == lesson.Id, "tutorial opens " + lesson.Id);
                await Shot(lesson.Id); TutorialRunner.Instance.Cancel();
            }
            State.Open(new()); ImportStock(true); await Frames();
            Check(State.Project.Scene.Count == 1, "stock Spy imported");
            float feet = ClipEvaluator.World(State.Skeleton!, State.Pose()).Min(b => (AnimationStage.ToGodot(State.Skeleton!.DisplayTransform) * AnimationStage.Vec(b.Translation)).Y);
            Check(_stage.GroundHeight < feet, "stock character feet are above stage floor");
            Check(_stage.BonePoints.Values.All(p => new Rect2(Vector2.Zero, _stage.Size).HasPoint(p)), "stock character fits in framed viewport");
            await Shot("stock-grounding");
            var replacement = AnimationJson.Clone(State.Project);
            replacement.Clips = [AnimationJson.Clone(State.CurrentClip!)];
            foreach (RigInstance instance in replacement.Scene) instance.Clip = replacement.Clips[0].Id;
            replacement.Bindings = [new() { Target = "SPerso.ATT_01", Slot = replacement.Clips[0].Name, Clip = replacement.Clips[0].Id }];
            string overlay = Path.Combine(UserData.Folder, "replacement-test");
            AnimationPackBuild.Write(replacement, State.Session.Folder, overlay);
            AnimationPracticeMap.Build(Path.Combine(overlay, "practice-maps"));
            Check(File.Exists(Path.Combine(overlay, "practice-maps", "TestRange", "manifest.json")), "replacement playtest has its own compiled practice map");
            OpenFirstPerson(FirstPersonRole.Spy); await Frames();
            Check(_firstPerson.Active && _stage.FirstPersonProfileId is not null && _stage.FirstPersonEye, "first-person setup loads in eye view");
            State.Open(new()); await Frames();
            Check(_stage.Empty && _stage.FirstPersonProfileId is null && !_firstPerson.Active, "New also clears first-person setup and its overlays");
            GD.Print($"ANIMATION_UI_CHECKS_PASS {checks}"); GetTree().Quit();
        }
        catch (Exception ex) { GD.PrintErr("ANIMATION_UI_CHECKS_FAIL " + ex); GetTree().Quit(1); }
    }
}
