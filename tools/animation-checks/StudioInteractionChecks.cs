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
        static bool SamePose(BoneTransform a, BoneTransform b) => System.Numerics.Vector3.Distance(a.Position, b.Position) < .0001f
            && System.Numerics.Vector3.Distance(a.Scale, b.Scale) < .0001f && MathF.Abs(System.Numerics.Quaternion.Dot(a.Rotation, b.Rotation)) > .99999f;
        SpinBox PoseField(string kind, char axis) => (SpinBox)_inspector.FindChild("Pose_" + kind + "_" + axis, true, false);
        bool FieldsMatchPose()
        {
            BoneTransform pose = State.Pose()[State.Bone];
            Vector3 angles = new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W).GetEuler() * (180 / MathF.PI);
            return Math.Abs(PoseField("Position", 'Y').Value - pose.Position.Y) < .00001 && Math.Abs(PoseField("Rotation", 'Z').Value - angles.Z) < .0001
                && Math.Abs(PoseField("Scale", 'X').Value - pose.Scale.X) < .00001;
        }
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
            var selectionClip = new OpenScct.Animation.AnimationClip { Name = "Selection scrub", Skeleton = State.Skeleton!.Id, Duration = 3 };
            AnimationEdits.AddPoseKeys(selectionClip, "Upper", State.Skeleton.Bones[1].Rest, 0);
            AnimationEdits.AddPoseKeys(selectionClip, "Upper", State.Skeleton.Bones[1].Rest with { Position = new(0, 1.5f, 0), Rotation = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, .5f), Scale = new(1.5f) }, 3);
            State.Session.Edit("Add selection sample", p => p.Clips.Add(selectionClip)); await Frames();
            TreeItem clipItem = _clips.GetRoot().GetChild(1);
            UiInput.Click(GetTree().Root, _clips.GlobalPosition + _clips.GetItemAreaRect(clipItem).GetCenter()); await Frames();
            Check(State.ClipId == selectionClip.Id && !State.Playing, "Project row selects a different clip without playing");
            Check(Math.Abs(_seek.MaxValue - selectionClip.Duration) < .001, "Project selection immediately updates the scrub range");
            State.SelectBone(1); await Frames();
            await Click(DiscoveryNavigation.Descendants(this).OfType<Button>().Single(b => b.Text == "▶"));
            Check(State.Time > 0 && FieldsMatchPose(), "stepping updates position, rotation and scale inspector fields");
            start = _seek.GlobalPosition + new Vector2(_seek.Size.X * .15f, _seek.Size.Y * .5f);
            end = _seek.GlobalPosition + new Vector2(_seek.Size.X * .75f, _seek.Size.Y * .5f);
            UiInput.Button(GetTree().Root, start, MouseButton.Left, true); await Frames();
            UiInput.Move(GetTree().Root, end, 5); await Frames();
            UiInput.Button(GetTree().Root, end, MouseButton.Left, false); await Frames();
            Check(State.Time > selectionClip.Duration * .5f && !State.Playing, "scrubbing works immediately after clicking a Project clip");
            Check(State.Pose()[1].Position.Y > 1, "selected clip pose follows the scrubbed time");
            Check(FieldsMatchPose(), "scrubbing updates all pose inspector fields");
            byte[] beforePlayback = AnimationJson.Write(State.Project);
            State.Playing = true; await Frames(8); State.Playing = false;
            Check(FieldsMatchPose() && beforePlayback.AsSpan().SequenceEqual(AnimationJson.Write(State.Project)), "playback updates inspector values without creating edits");
            clipItem = _clips.GetRoot().GetChild(0);
            string firstClipId = clipItem.GetMetadata(0).AsString();
            UiInput.Click(GetTree().Root, _clips.GlobalPosition + _clips.GetItemAreaRect(clipItem).GetCenter()); await Frames();
            Check(State.ClipId == firstClipId && Math.Abs(_seek.MaxValue - State.Duration) < .001 && State.Time == 0, "switching back updates the selected clip, range and playhead");
            UiInput.Button(GetTree().Root, rulerStart, MouseButton.Left, true); await Frames(); firstSample = State.Time;
            UiInput.Move(GetTree().Root, rulerEnd, 5); await Frames();
            UiInput.Button(GetTree().Root, rulerEnd, MouseButton.Left, false); await Frames();
            Check(State.Time > firstSample + .5f && !State.Playing, "timeline scrubbing works immediately after switching Project clips");
            State.SelectBone(0); await Frames();
            TreeItem upper = _rig.GetRoot().GetChild(0).GetChild(0);
            UiInput.Click(GetTree().Root, _rig.GlobalPosition + _rig.GetItemAreaRect(upper).GetCenter()); await Frames();
            Check(State.Bone == 1 && _stage.BonePoints.ContainsKey(1), "hierarchy click selects the stage bone");
            foreach (bool autoKey in new[] { true, false })
            foreach (string tool in new[] { "move", "rotate", "scale" })
            {
                if (State.AutoKey != autoKey) await Click(DiscoveryNavigation.Descendants(this).OfType<Button>().Single(b => b.Text == "Auto key"));
                await Click(_poseTools[tool]);
                Check(State.TransformTool == tool && _poseTools[tool].ButtonPressed && _poseTools.Count(p => p.Value.ButtonPressed) == 1, tool + " button has exclusive active feedback");
                var handle = _stage.Handles.OrderByDescending(h => _stage.Handles.Where(other => other.Axis != h.Axis)
                    .Select(other => Geometry2D.GetClosestPointToSegment((h.A + h.B) * .5f, other.A, other.B).DistanceSquaredTo((h.A + h.B) * .5f)).DefaultIfEmpty(100).Min()).First();
                Vector2 at = _stage.GlobalPosition + (handle.A + handle.B) * .5f;
                Vector2 to = at + (handle.B - handle.A).Normalized() * 32;
                BoneTransform before = State.Pose()[State.Bone];
                byte[] beforeDocument = AnimationJson.Write(State.Project);
                UiInput.Button(GetTree().Root, at, MouseButton.Left, true); await Frames();
                UiInput.Move(GetTree().Root, to, 5); await Frames();
                UiInput.Button(GetTree().Root, to, MouseButton.Left, false); await Frames();
                Check(State.Pose()[State.Bone] != before, tool + " handle drag changes the bone");
                BoneTransform after = State.Pose()[State.Bone];
                UiInput.Key("Ctrl+Z"); await Frames();
                Check(SamePose(State.Pose()[State.Bone], before), $"{tool} drag Undo with Auto key {autoKey}");
                UiInput.Key("Ctrl+Y"); await Frames();
                Check(SamePose(State.Pose()[State.Bone], after), $"{tool} drag Redo with Auto key {autoKey}");
                if (!autoKey) Check(beforeDocument.AsSpan().SequenceEqual(AnimationJson.Write(State.Project)), tool + " unkeyed Undo/Redo leaves clip keys unchanged");
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
            OpenScct.Animation.AnimationClip stockSelection = State.Project.Clips.First(c => c.Name.StartsWith("Draw", StringComparison.OrdinalIgnoreCase) && c.Duration > .1f);
            clipItem = _clips.GetRoot().GetFirstChild();
            while (clipItem.GetMetadata(0).AsString() != stockSelection.Id) clipItem = clipItem.GetNext();
            _clips.ScrollToItem(clipItem); await Frames();
            UiInput.Click(GetTree().Root, _clips.GlobalPosition + _clips.GetItemAreaRect(clipItem).GetCenter()); await Frames();
            Check(State.ClipId == stockSelection.Id && State.Time == 0 && !State.Playing && Math.Abs(_seek.MaxValue - stockSelection.Duration) < .001, "selecting a scrolled stock Project row updates transport immediately");
            var displayedBefore = _stage.BonePoints.ToDictionary(p => p.Key, p => p.Value);
            await Click(_seek);
            Check(State.Time > State.Duration * .4f && !State.Playing, "stock clip scrubs on first click after Project selection");
            Check(displayedBefore.Any(p => _stage.BonePoints.TryGetValue(p.Key, out Vector2 point) && p.Value.DistanceTo(point) > .1f), "rendered stock skeleton follows the selected clip's scrubbed pose");
            await Shot("project-selection-scrub");
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
            ImportPath(Path.Combine(OpenScct.Editor.Core.Services.EditorServices.ContentRoot!, "shared", "chars", "OpenScctSidearms", "desert_eagle_first.glb"));
            State.SelectClip(State.Project.Clips.First(c => c.Name == "FirstReloStFd2").Id);
            State.Seek(State.Duration * .7f);
            int magazine = State.Skeleton!.Bones.FindIndex(b => b.Name == "mag");
            State.SelectBone(magazine); State.SelectTool("move"); _stage.Frame(); await Frames();
            byte[] beforeMagazineEdits = AnimationJson.Write(State.Project);
            foreach (bool world in new[] { false, true })
            foreach (int zoom in new[] { 0, 4 })
            {
                State.WorldSpace = world; State.Seek(State.Duration * .7f); State.Notify(); _stage.Frame(); await Frames();
                for (int tick = 0; tick < zoom; tick++) UiInput.Click(GetTree().Root, _stage.GetGlobalRect().GetCenter(), MouseButton.WheelUp);
                await Frames();
                var target = _stage.Handles.SelectMany(h => new[] { .65f, .8f, 1f }.Select(f => (Handle: h, Point: h.A.Lerp(h.B, f))))
                    .Where(h => new Rect2(Vector2.Zero, _stage.Size).Grow(-12).HasPoint(h.Point))
                    .MaxBy(h => _stage.BonePoints.Where(p => p.Key != magazine).Min(p => p.Value.DistanceTo(h.Point)));
                Vector2 at = _stage.GlobalPosition + target.Point, direction = (target.Handle.B - target.Handle.A).Normalized();
                Vector2 before = _stage.BonePoints[magazine]; BoneTransform poseBefore = State.Pose()[magazine];
                UiInput.Button(GetTree().Root, at, MouseButton.Left, true); await Frames();
                Check(State.Bone == magazine, "Desert Eagle move handle keeps the magazine selected");
                UiInput.Move(GetTree().Root, at + direction * 30, 5); await Frames();
                UiInput.Button(GetTree().Root, at + direction * 30, MouseButton.Left, false); await Frames();
                float pixels = (_stage.BonePoints[magazine] - before).Dot(direction);
                Check(pixels is > 20 and < 40, $"Desert Eagle 30px move, world={world}, zoom={zoom}: {pixels:0.00}px");
                State.Session.Undo(); await Frames();
                Check(SamePose(State.Pose()[magazine], poseBefore), "Desert Eagle unkeyed magazine drag is undoable");
            }
            State.WorldSpace = false; State.Seek(State.Duration * .7f); State.Notify(); await Frames();
            System.Numerics.Vector3 MagazinePosition()
            {
                var rig = State.Project.Scene.Single(r => r.Id == State.RigId);
                return System.Numerics.Vector3.Transform(ClipEvaluator.World(State.Skeleton!, State.Pose())[magazine].Translation,
                    State.Skeleton!.DisplayTransform.Matrix * rig.Transform.Matrix);
            }
            Vector3 ArrowPoint(SpinBox field) => new(field.GlobalPosition.X + field.Size.X - 5, field.GlobalPosition.Y + 5, 0);
            async Task Arrow(SpinBox field)
            {
                OpenScctEditor.Panels.PanelUi.ScrollOf(_inspector).EnsureControlVisible(field); await Frames();
                Vector3 at = ArrowPoint(field); UiInput.Click(GetTree().Root, new(at.X, at.Y)); await Frames();
            }
            var positionBefore = MagazinePosition();
            await Arrow(PoseField("Position", 'X'));
            Check(Math.Abs(System.Numerics.Vector3.Distance(MagazinePosition(), positionBefore) - .01f) < .00003f, "Desert Eagle position spinner moves by one physical centimetre");
            EditorContext.Instance.Run("edit.undo"); await Frames();
            Check(System.Numerics.Vector3.Distance(MagazinePosition(), positionBefore) < .00001f, "position spinner supports Undo with Auto key off");
            float scaleBefore = State.Pose()[magazine].Scale.X;
            await Arrow(PoseField("Scale", 'X'));
            Check(Math.Abs(State.Pose()[magazine].Scale.X / scaleBefore - 1.05f) < .0001f, "scale spinner advances by five percent");
            State.Session.Undo(); await Frames();
            PoseField("Position", 'X').Value = .012345; await Frames();
            Check(Math.Abs(PoseField("Position", 'X').Value - .012345) < .000001, "pose spinners retain precise typed values independently of arrow steps");
            State.Session.Undo(); await Frames();
            Check(beforeMagazineEdits.AsSpan().SequenceEqual(AnimationJson.Write(State.Project)), "Desert Eagle draft drags and numeric edits never create clip keys");
            SpinBox heldField = PoseField("Position", 'Y');
            OpenScctEditor.Panels.PanelUi.ScrollOf(_inspector).EnsureControlVisible(heldField); await Frames();
            double heldBefore = heldField.Value;
            Rect2 heldRect = heldField.GetGlobalRect();
            Vector3 heldAt = ArrowPoint(heldField);
            UiInput.Button(GetTree().Root, new(heldAt.X, heldAt.Y), MouseButton.Left, true); await Frames(45);
            bool heldInput = Input.IsMouseButtonPressed(MouseButton.Left);
            Vector2 nativePointer = heldField.GetLocalMousePosition();
            UiInput.Button(GetTree().Root, new(heldAt.X, heldAt.Y), MouseButton.Left, false); await Frames();
            int repeatedSteps = (int)Math.Round((PoseField("Position", 'Y').Value - heldBefore) / .01);
            Check(heldInput && ReferenceEquals(heldField, PoseField("Position", 'Y')) && heldRect == heldField.GetGlobalRect(), "holding a pose spinner retains its control and layout");
            if (nativePointer.X > heldField.Size.X - 18 && nativePointer.X < heldField.Size.X && nativePointer.Y is >= 0 && nativePointer.Y < heldField.Size.Y / 2)
                Check(repeatedSteps > 1, "native spinner hover repeats the held arrow");
            else GD.Print($"SPINNER_REPEAT_NATIVE_HOVER_UNAVAILABLE local={nativePointer} steps={repeatedSteps}");
            for (int step = 0; step < repeatedSteps; step++) { State.Session.Undo(); await Frames(); }
            Check(Math.Abs(PoseField("Position", 'Y').Value - heldBefore) < .000002, "repeated numeric steps remain undoable");
            await Click(DiscoveryNavigation.Descendants(this).OfType<Button>().Single(b => b.Text == "Auto key"));
            SpinBox keyedField = PoseField("Position", 'Z'); BoneTransform keyedBefore = State.Pose()[magazine];
            await Arrow(keyedField);
            Check(ReferenceEquals(keyedField, PoseField("Position", 'Z')) && !SamePose(State.Pose()[magazine], keyedBefore), "keyed pose spinner edits keep their active control alive");
            State.Session.Undo(); await Frames();
            Check(SamePose(State.Pose()[magazine], keyedBefore), "keyed numeric edit Undo restores the magazine pose");
            await Click(DiscoveryNavigation.Descendants(this).OfType<Button>().Single(b => b.Text == "Auto key"));
            State.WorldSpace = true; State.Notify(); State.SelectTool("scale"); _stage.Frame(); await Frames();
            BoneTransform WorldMagazine()
            {
                var rig = State.Project.Scene.Single(r => r.Id == State.RigId);
                return BoneTransform.FromMatrix(ClipEvaluator.World(State.Skeleton!, State.Pose())[magazine] * State.Skeleton!.DisplayTransform.Matrix * rig.Transform.Matrix);
            }
            var scaleTarget = _stage.Handles.SelectMany(h => new[] { .8f, 1f }.Select(f => (Handle: h, Point: h.A.Lerp(h.B, f))))
                .Where(h => new Rect2(Vector2.Zero, _stage.Size).Grow(-12).HasPoint(h.Point))
                .MaxBy(h => _stage.BonePoints.Where(p => p.Key != magazine).Min(p => p.Value.DistanceTo(h.Point)));
            BoneTransform worldScaleBefore = WorldMagazine();
            Vector2 scaleAt = _stage.GlobalPosition + scaleTarget.Point, scaleDirection = (scaleTarget.Handle.B - scaleTarget.Handle.A).Normalized();
            UiInput.Button(GetTree().Root, scaleAt, MouseButton.Left, true); await Frames();
            UiInput.Move(GetTree().Root, scaleAt + scaleDirection * 20, 5); await Frames();
            UiInput.Button(GetTree().Root, scaleAt + scaleDirection * 20, MouseButton.Left, false); await Frames();
            float scaleRatio = Component(WorldMagazine().Scale, scaleTarget.Handle.Axis) / Component(worldScaleBefore.Scale, scaleTarget.Handle.Axis);
            Check(Math.Abs(scaleRatio - 1.2f) < .01f, "World scale handles use relative changes on imported centimetre rigs");
            State.Session.Undo(); await Frames();
            await Click(DiscoveryNavigation.Descendants(this).OfType<Button>().Single(b => b.Text == "First person"));
            Check(_stage.FirstPersonEye && _stage.ViewTransform.Origin.Length() < .00001f, "imported Desert Eagle first-person view uses the eye origin");
            Check((-_stage.ViewTransform.Basis.Z).Dot(Vector3.Right) > .999f, "Desert Eagle first-person view looks along the authored forward axis");
            await Shot("desert-eagle-eye-view");
            _tabs.CurrentTab = 1; await Frames();
            var curves = (AnimationTimeline)_tabs.GetCurrentTabControl();
            Control chart = (Control)curves.FindChild("AnimationCurves", true, false);
            int magazineKeys = curves.Tracks.Where(t => t.Bone == "mag").Sum(t => t.Keys.Count);
            Check(curves.KeyPoints.Count == magazineKeys && curves.KeyPoints.Values.All(p => curves.PlotRect.Grow(-6).HasPoint(p)), "Curves fits every imported Desert Eagle magazine key inside the plot");
            curves.SelectKeys(true); await Frames();
            Check(curves.SelectedKeyCount == magazineKeys, "Curves Select All selects only the visible bone keys");
            curves.SelectKeys(false);
            var channel = (OptionButton)curves.FindChild("CurveChannel", true, false);
            channel.Select(1); channel.EmitSignal(OptionButton.SignalName.ItemSelected, 1); await Frames();
            Check(curves.KeyPoints.Count == curves.Tracks.Where(t => t.Bone == "mag" && t.Channel == TrackChannel.Translation).Sum(t => t.Keys.Count), "curve channel filter reveals just magazine position keys");
            var component = (OptionButton)curves.FindChild("CurveComponent", true, false);
            component.Select(1); component.EmitSignal(OptionButton.SignalName.ItemSelected, 1); await Frames();
            Check(curves.KeyPoints.Count > 0 && curves.KeyPoints.Values.All(p => curves.PlotRect.Grow(-6).HasPoint(p)), "switching curve component automatically refits its values");
            State.Session.EditClip("Sparse magazine override", State.ClipId!, clip =>
            {
                var layer = new AnimationLayer { Name = "Magazine alignment", Mask = ["mag"] };
                var target = new OpenScct.Animation.AnimationClip { Tracks = layer.Tracks };
                AnimationEdits.AddPoseKeys(target, "mag", State.Skeleton!.Bones[magazine].Rest with { Position = new(20, -80, 40) }, .25f);
                AnimationEdits.AddPoseKeys(target, "mag", State.Skeleton!.Bones[magazine].Rest with { Position = new(40, 120, 80) }, MathF.Min(1.25f, State.Duration));
                clip.Layers.Add(layer);
            });
            State.Layer = State.CurrentClip!.Layers.Count - 1; State.Notify(); await Frames();
            Check(curves.KeyPoints.Count == 2 && curves.KeyPoints.Values.All(p => curves.PlotRect.Grow(-6).HasPoint(p)), "sparse override layer exposes both endpoint frames at large source-unit values");
            var split = (VSplitContainer)_tabs.GetParent(); int[] previousSplit = split.SplitOffsets;
            split.SplitOffsets = [previousSplit.FirstOrDefault() - 50]; await Frames();
            Check(curves.KeyPoints.Count == 2 && curves.KeyPoints.Values.All(p => curves.PlotRect.Grow(-6).HasPoint(p)), "resizing the curve panel keeps both endpoint keys in view");
            split.SplitOffsets = previousSplit; await Frames();
            await Shot("desert-eagle-magazine-curves");
            chart.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true }); await Frames();
            await Click(DiscoveryNavigation.Descendants(curves).OfType<Button>().Single(b => b.Text == "Frame keys"));
            Check(curves.KeyPoints.Count == 2 && curves.KeyPoints.Values.All(p => curves.PlotRect.Grow(-6).HasPoint(p)), "Frame keys restores a panned curve view");
            var firstKey = curves.KeyPoints.OrderBy(k => k.Key.Time).First();
            float beforeValue = curves.Tracks[firstKey.Key.Track].Keys.Single(k => k.Time == firstKey.Key.Time).Value.Y;
            Vector2 keyAt = chart.GlobalPosition + firstKey.Value, keyTo = keyAt - new Vector2(0, 12);
            UiInput.Button(GetTree().Root, keyAt, MouseButton.Left, true); await Frames();
            UiInput.Move(GetTree().Root, keyTo, 5); await Frames();
            UiInput.Button(GetTree().Root, keyTo, MouseButton.Left, false); await Frames();
            Check(curves.Tracks[firstKey.Key.Track].Keys.Single(k => k.Time == firstKey.Key.Time).Value.Y > beforeValue + 1, "curve key value dragging follows the fitted value scale");
            State.Session.Undo(); await Frames();
            Check(Math.Abs(curves.Tracks[firstKey.Key.Track].Keys.Single(k => k.Time == firstKey.Key.Time).Value.Y - beforeValue) < .001f, "Undo restores a dragged curve key");
            curves.SelectKeys(true); await Frames();
            Button smooth = (Button)curves.FindChild("AnimationSmoothBetween", true, false);
            string baseBeforeSmooth = System.Text.Json.JsonSerializer.Serialize(State.CurrentClip!.Tracks, AnimationJson.Options);
            await Click(smooth);
            Check(curves.Tracks.Single(t => t.Channel == TrackChannel.Translation).Keys[0].Interpolation == KeyInterpolation.Cubic
                && baseBeforeSmooth == System.Text.Json.JsonSerializer.Serialize(State.CurrentClip.Tracks, AnimationJson.Options), "Smooth between edits sparse active-layer keys without changing the base tracks");
            State.Session.Undo(); await Frames();
            await Click(DiscoveryNavigation.Descendants(curves).OfType<Button>().Single(b => b.Text == "Cubic"));
            Check(curves.Tracks.Single(t => t.Channel == TrackChannel.Translation).Keys.All(k => k.Interpolation == KeyInterpolation.Cubic), "visible sparse keys accept Cubic interpolation");
            State.Layer = -1; State.Notify(); await Frames();
            Check(curves.KeyPoints.Count > 2 && curves.SelectedKeyCount == 0, "switching back to base curves restores its keys and clears layer selection");
            _tabs.CurrentTab = 0;
            RevealTutorial("Curves|AnimationSmoothBetween"); await Frames();
            Check(_tabs.CurrentTab == 1 && NavigationHighlight.Instance!.TargetPath == smooth.GetPath().ToString(), "smoothing tutorial reveals and highlights the actual Curves button");
            NavigationHighlight.Instance!.Clear();
            Check(smooth.Disabled, "Smooth between explains that two keys on one track must be selected");
            State.Session.EditClip("Disable test layer", State.ClipId!, clip => clip.Layers.ForEach(l => l.Enabled = false)); await Frames();
            int magTrack = curves.Tracks.FindIndex(t => t.Bone == "mag" && t.Channel == TrackChannel.Translation);
            BoneTrack dense = curves.Tracks[magTrack];
            AnimationKey from = dense.Keys[dense.Keys.Count / 4], until = dense.Keys[dense.Keys.Count * 3 / 4];
            byte[] beforeSmooth = AnimationJson.Write(State.Project);
            int interior = dense.Keys.Count(k => k.Time > from.Time && k.Time < until.Time), denseCount = dense.Keys.Count;
            foreach (var endpoint in new[] { from, until })
            {
                UiInput.Click(GetTree().Root, chart.GlobalPosition + curves.KeyPoints[(magTrack, endpoint.Time)], MouseButton.Left, endpoint == from ? 0 : KeyModifierMask.MaskCtrl);
                await Frames();
                if (endpoint == from) Check(smooth.Disabled, "a single endpoint keeps Smooth between disabled");
            }
            Check(curves.SelectedKeyCount == 2 && !smooth.Disabled, "Ctrl-click selects the two actual Desert Eagle magazine endpoint keys");
            await Click(smooth);
            Check(curves.Tracks[magTrack].Keys.Count == denseCount - interior && !curves.Tracks[magTrack].Keys.Any(k => k.Time > from.Time && k.Time < until.Time), "Smooth between replaces dense imported position keys inside the selected interval");
            Check(curves.Tracks[magTrack].Keys.Single(k => k.Time == from.Time).Value == from.Value
                && curves.Tracks[magTrack].Keys.Single(k => k.Time == until.Time).Value == until.Value, "Smooth between preserves the magazine's selected endpoint poses");
            float quarter = from.Time + (until.Time - from.Time) * .25f;
            State.Seek(quarter); await Frames();
            var expectedPosition = ClipEvaluator.Xyz(System.Numerics.Vector4.Lerp(from.Value, until.Value, .15625f));
            Check(System.Numerics.Vector3.Distance(State.Pose()[magazine].Position, expectedPosition) < .00001f, "scrubbing between smoothed magazine endpoints follows the eased path");
            byte[] afterSmooth = AnimationJson.Write(State.Project);
            await Shot("desert-eagle-smooth-between");
            UiInput.Key("Ctrl+Z"); await Frames();
            Check(beforeSmooth.AsSpan().SequenceEqual(AnimationJson.Write(State.Project)), "one Undo restores every original dense key and the rest of the project");
            UiInput.Key("Ctrl+Y"); await Frames();
            Check(afterSmooth.AsSpan().SequenceEqual(AnimationJson.Write(State.Project)), "one Redo restores the smoothed interval");
            State.Session.Undo(); await Frames();
            _firstPerson.ShowControls(); ShowLibrary(true); await Frames();
            string sourceId = State.ClipId!; byte[] sourceKeys = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(State.CurrentClip, AnimationJson.Options);
            int countBeforeAction = State.Project.Clips.Count;
            Button newAction = (Button)_firstPerson.FindChild("NewFirstPersonActionClip", true, false);
            await Click(newAction);
            Check(State.Project.Clips.Count == countBeforeAction + 1 && State.ClipId != sourceId && State.CurrentClip!.Name.Contains("action"), "New action clip creates an editable copy without requiring a setup");
            Check(((TabContainer)FindChild("AnimationLibraryTabs", true, false)).CurrentTab == 0, "New action clip reveals its result in Project from Stock");
            Check(_clips.GetSelected()?.GetMetadata(0).AsString() == State.ClipId && new Rect2(Vector2.Zero, _clips.Size).Intersects(_clips.GetItemAreaRect(_clips.GetSelected()!)), "new action row is selected and scrolled into the Project list");
            Check(sourceKeys.AsSpan().SequenceEqual(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(State.Project.Clips.Single(c => c.Id == sourceId), AnimationJson.Options)), "New action clip preserves source motion and layers");
            State.Session.Undo(); await Frames();
            Check(State.Project.Clips.Count == countBeforeAction && State.ClipId == sourceId, "Undo removes the new action copy and restores source selection");
            State.Session.Redo(); await Frames();
            Check(State.Project.Clips.Count == countBeforeAction + 1, "Redo restores the action copy");
            var role = (OptionButton)_firstPerson.FindChild("FirstPersonRole", true, false);
            role.Select(1); role.EmitSignal(OptionButton.SignalName.ItemSelected, 1); await Frames();
            var weapon = (OptionButton)_firstPerson.FindChild("FirstPersonWeapon", true, false);
            weapon.Select(3); weapon.EmitSignal(OptionButton.SignalName.ItemSelected, 3); await Frames();
            await Click((Button)_firstPerson.FindChild("LoadFirstPersonSetup", true, false));
            Check(_firstPerson.Profile is { Weapon: "Desert Eagle", MainAsset: "OpenScctSidearms.desert_eagle_first" }, "Merc Desert Eagle setup loads the imported pistol");
            var action = (OptionButton)_firstPerson.FindChild("FirstPersonAction", true, false);
            action.Select(5); action.EmitSignal(OptionButton.SignalName.ItemSelected, 5); await Frames();
            await Click(newAction);
            Check(_firstPerson.Profile!.Actions[5].Clip == State.ClipId && State.CurrentClip!.Name == "Merc FP Reload weapon", "New action clip assigns the new copy to the selected Reload action");
            await Click(newAction);
            Check(State.CurrentClip!.Name == "Merc FP Reload weapon 2" && _firstPerson.Profile!.Actions[5].Clip == State.ClipId, "repeated action creation gives distinguishable names");
            await Shot("desert-eagle-new-action");
            GD.Print($"ANIMATION_UI_CHECKS_PASS {checks}"); GetTree().Quit();
        }
        catch (Exception ex) { GD.PrintErr("ANIMATION_UI_CHECKS_FAIL " + ex); GetTree().Quit(1); }
    }
}
