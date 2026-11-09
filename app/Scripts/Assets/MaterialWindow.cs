using System.Reflection;
using System.Text.Json.Nodes;
using Godot;
using OpenScct.Content.Materials;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Assets.Import;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports.Render;
using OpenScctGame.Render.Materials;

namespace OpenScctEditor.Assets;

/// <summary>Texture-set review and material authoring. Editing stays local to this window until Import/Save.</summary>
public partial class MaterialWindow : AcceptDialog
{
    private readonly string _folder;
    private readonly object _session;
    private readonly List<TextureSetDraft> _drafts;
    private readonly Action<string> _imported;
    private readonly string? _existing;
    private readonly ItemList _sets = new();
    private readonly VBoxContainer _properties = new();
    private readonly LineEdit _name = new();
    private readonly LineEdit _group = new() { Text = "Textures" };
    private readonly OptionButton _conflict = new();
    private readonly CheckBox _enabled = new() { Text = "Import this material", ButtonPressed = true };
    private readonly Label _status = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly MeshInstance3D _mesh = new();
    private readonly DirectionalLight3D _light = new() { RotationDegrees = new(-35, -30, 0), LightEnergy = 2 };
    private readonly Dictionary<string, Texture2D> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cancel = new();
    private ShaderMaterial? _preview;
    private bool _paused;
    private double _clock;
    private int _current;
    private bool _building;
    private bool _writing;
    private int _selectionTriangles;
    private static EditorContext Context => EditorContext.Instance;
    private TextureSetDraft Current => _drafts[_current];

    public MaterialWindow(string folder, IEnumerable<TextureSetDraft> drafts, Action<string> imported, string? existing = null)
    {
        _folder = folder; _session = Context.Session; _drafts = drafts.ToList(); _imported = imported; _existing = existing;
        Title = existing is null ? "Import Textures & Create Materials" : "Edit Material";
        Name = "MaterialAuthoring"; Exclusive = false; Transient = true; Unresizable = false; WrapControls = false;
        OkButtonText = existing is null ? "Import Materials" : "Save Material";
        DialogHideOnOk = false;
        AddCancelButton("Cancel");
        // A native dialog must not inherit a growing minimum from a wrapped status
        // label or the expanded texture tabs. Keep its frame bounded; scroll content.
        var bounds = new ScrollContainer { Name = "MaterialBounds" }; AddChild(bounds);
        var body = new VBoxContainer { Name = "MaterialBody", CustomMinimumSize = new(860, 540), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill }; bounds.AddChild(body);
        var row = new HBoxContainer { Name = "MaterialColumns", SizeFlagsVertical = Control.SizeFlags.ExpandFill }; body.AddChild(row);
        _sets.CustomMinimumSize = new(170, 0); _sets.SizeFlagsVertical = Control.SizeFlags.ExpandFill; row.AddChild(_sets);
        foreach (TextureSetDraft draft in _drafts) _sets.AddItem(draft.Name);
        _sets.ItemSelected += i => { StoreName(); _current = (int)i; BuildProperties(); };
        var middle = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 2 }; row.AddChild(middle);
        var header = new HBoxContainer(); middle.AddChild(header);
        header.AddChild(new Label { Text = "Name" }); _name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; header.AddChild(_name);
        header.AddChild(new Label { Text = "Group" }); _group.CustomMinimumSize = new(130, 0); header.AddChild(_group);
        foreach (string c in new[] { "Keep both (unique name)", "Replace existing", "Skip existing" }) _conflict.AddItem(c);
        _conflict.TooltipText = "Replacing a material updates all surfaces already using it."; middle.AddChild(_conflict);
        middle.AddChild(_enabled); _enabled.Toggled += value => { if (!_building) Current.Enabled = value; };
        var colourDefaults = new Button { Text = "Use recommended colour spaces", TooltipText = "Use sRGB for colour/emission/environment images and linear data for normal, height, roughness and masks. Save commits this change with Undo support." };
        colourDefaults.Pressed += () => { foreach (var entry in Current.Material.Textures) entry.Value.Srgb = entry.Key is "baseColor" or "emission" or "detailColor" or "environment"; BuildProperties(); };
        middle.AddChild(colourDefaults);
        var scroll = new ScrollContainer { Name = "MaterialProperties", SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(360, 300) };
        _properties.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; scroll.AddChild(_properties); middle.AddChild(scroll);
        var right = new VBoxContainer { CustomMinimumSize = new(260, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; row.AddChild(right);
        var viewportContainer = new SubViewportContainer { Name = "MaterialPreview", Stretch = true, CustomMinimumSize = new(250, 250), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var viewport = new SubViewport { Size = new(330, 380), OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false };
        viewportContainer.AddChild(viewport); right.AddChild(viewportContainer);
        var world = new Node3D(); viewport.AddChild(world);
        world.AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new(0.12f, 0.12f, 0.14f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = 0.3f } });
        var camera = new Camera3D { Position = new(0, 0, 3.0f), Fov = 45, KeepAspect = Camera3D.KeepAspectEnum.Width, Current = true }; world.AddChild(camera);
        _mesh.Mesh = new SphereMesh { Radius = 0.8f, Height = 1.6f, RadialSegments = 48, Rings = 32 }; world.AddChild(_mesh); world.AddChild(_light);
        var shapes = new OptionButton(); foreach (string s in new[] { "Sphere", "Cube", "Plane", "Current selection" }) shapes.AddItem(s);
        shapes.ItemSelected += i => { _selectionTriangles = 0; _mesh.Mesh = i switch { 1 => new BoxMesh { Size = Vector3.One * 1.4f }, 2 => new QuadMesh { Size = Vector2.One * 1.8f }, 3 => MaterialSelectionPreview.Build(out _selectionTriangles), _ => new SphereMesh { Radius = .8f, Height = 1.6f } }; RefreshPreview(); }; right.AddChild(shapes);
        AddNumber(right, "Light angle", -180, 180, 1, -30, v => _light.RotationDegrees = new(-35, (float)v, 0));
        var play = new Button { Text = "Pause animation" }; play.Pressed += () => { _paused = !_paused; play.Text = _paused ? "Play animation" : "Pause animation"; }; right.AddChild(play);
        var restart = new Button { Text = "Restart animation" }; restart.Pressed += () => _clock = 0; right.AddChild(restart);
        var presets = new OptionButton(); foreach (string p in new[] { "Choose preset…", "Matte stone", "Polished metal", "Glass", "Emissive sign", "Unlit screen" }) presets.AddItem(p);
        presets.ItemSelected += i => { var m = Current.Material; if (i == 1) { m.Metallic = 0; m.Roughness = .9f; } if (i == 2) { m.Metallic = 1; m.Roughness = .18f; } if (i == 3) { m.Surface = "translucent"; m.Opacity = .25f; m.Roughness = .1f; m.Refraction = .02f; } if (i == 4) m.EmissionEnergy = 3; if (i == 5) m.Unlit = true; BuildProperties(); }; right.AddChild(presets);
        body.AddChild(_status);
        Confirmed += Save;
        CloseRequested += Close;
        Canceled += Close;
        _sets.Select(0); BuildProperties();
    }

    public void ShowEditor()
    {
        Rect2I usable = IsEmbedded()
            ? new Rect2I(Vector2I.Zero, (Vector2I)GetParent().GetViewport().GetVisibleRect().Size)
            : DisplayServer.ScreenGetUsableRect(Visible ? CurrentScreen : GetParent().GetWindow().CurrentScreen);
        OpenWithin(usable);
    }

    internal void OpenWithin(Rect2I usable)
    {
        Vector2I maximum = new(Math.Max(1, usable.Size.X - 32), Math.Max(1, usable.Size.Y - 80));
        MaxSize = Vector2I.Zero;
        MinSize = new(Math.Min(900, maximum.X), Math.Min(650, maximum.Y));
        MaxSize = maximum;
        Vector2I wanted = Visible ? Size : new(1100, 760);
        PopupCentered(new(Math.Clamp(wanted.X, MinSize.X, maximum.X), Math.Clamp(wanted.Y, MinSize.Y, maximum.Y)));
        CenterFrame(usable);
        EditorLifetime.Defer(() => { if (IsInstanceValid(this) && Visible) CenterFrame(usable); });
    }

    private void CenterFrame(Rect2I usable)
    {
        // Decorations are only reliable once the native window exists, especially
        // on displays with a different Windows DPI. Include them in the final cap.
        Vector2I decoration = GetSizeWithDecorations() - Size;
        Vector2I maximum = new(Math.Max(1, usable.Size.X - Math.Max(0, decoration.X) - 24), Math.Max(1, usable.Size.Y - Math.Max(0, decoration.Y) - 24));
        MaxSize = Vector2I.Zero;
        MinSize = new(Math.Min(900, maximum.X), Math.Min(650, maximum.Y));
        MaxSize = maximum;
        Size = new(Math.Clamp(Size.X, MinSize.X, maximum.X), Math.Clamp(Size.Y, MinSize.Y, maximum.Y));
        Vector2I inset = Position - GetPositionWithDecorations();
        Position = usable.Position + (usable.Size - GetSizeWithDecorations()) / 2 + inset;
    }

    public override void _Process(double delta)
    {
        if (!ReferenceEquals(Context.Session, _session)) { Close(); return; }
        if (!_paused) _clock += delta;
        if (_preview is not null) { _preview.SetShaderParameter("preview_paused", true); _preview.SetShaderParameter("preview_time", (float)_clock); }
    }

    private void Close() { if (_writing) _cancel.Cancel(); QueueFree(); }
    public override void _ExitTree() { _cancel.Cancel(); _cancel.Dispose(); }
    private void StoreName() { if (_drafts.Count > _current) { Current.Name = _name.Text; Current.Conflict = (TextureConflict)_conflict.Selected; } }

    private void BuildProperties()
    {
        _building = true;
        foreach (Node child in _properties.GetChildren()) { _properties.RemoveChild(child); child.QueueFree(); }
        _name.Text = Current.Name;
        _conflict.Select((int)Current.Conflict); _enabled.SetPressedNoSignal(Current.Enabled);
        var tabs = new TabContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; _properties.AddChild(tabs);
        VBoxContainer Tab(string label) { var tab = new VBoxContainer { Name = label }; tabs.AddChild(tab); return tab; }
        VBoxContainer maps = Tab("Textures");
        foreach (string slot in AuthoredMaterial.Slots)
        {
            var line = new VBoxContainer();
            var fold = new Button { Text = slot == "height" ? "height (displacement map)" : Words(slot), ToggleMode = true, ButtonPressed = Current.Material.Textures.ContainsKey(slot), Alignment = HorizontalAlignment.Left };
            if (slot == "height") fold.TooltipText = "Height textures are often named _Displacement. Set Parallax Depth or real Displacement in Depth to enable relief.";
            maps.AddChild(fold); maps.AddChild(line); line.Visible = fold.ButtonPressed; fold.Toggled += value => line.Visible = value;
            var buttons = new HBoxContainer(); line.AddChild(buttons);
            var pick = new Button { Text = slot + ": " + (Current.Sources.TryGetValue(slot, out string? source) ? Path.GetFileName(source) : Current.Material.Textures.TryGetValue(slot, out MaterialTexture? t) ? Path.GetFileName(t.File) : "Choose…"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            buttons.AddChild(pick);
            pick.Pressed += () => Dialogs.OpenFile("Choose " + slot + " map", ["*.png, *.jpg, *.jpeg, *.bmp, *.webp ; Images"], file =>
            {
                Current.Sources[slot] = file; Current.Generated.Remove(slot);
                Current.Errors.Clear();
                Current.Material.Textures[slot] = new() { File = "pending/" + slot + ".png", Srgb = slot is "baseColor" or "emission" or "detailColor" or "environment" };
                if (slot == "metallic") Current.Material.Metallic = 1;
                if (slot == "roughness") Current.Material.Roughness = 1;
                if (slot == "emission" && Current.Material.EmissionEnergy == 0) Current.Material.EmissionEnergy = 1;
                BuildProperties();
            }, owner: this);
            var remove = new Button { Text = "×", TooltipText = "Remove map assignment" }; buttons.AddChild(remove);
            remove.Pressed += () => { Current.Sources.Remove(slot); Current.Generated.Remove(slot); Current.Material.Textures.Remove(slot); Current.Errors.Clear(); BuildProperties(); };
            if (Current.Material.Textures.TryGetValue(slot, out MaterialTexture? binding))
            {
                var options = new HBoxContainer(); line.AddChild(options);
                if (slot is "height" or "roughness" or "metallic" or "specular" or "ao" or "opacity")
                {
                    var channel = new OptionButton(); foreach (string c in new[] { "r", "g", "b", "a", "luminance" }) channel.AddItem(c);
                    channel.Select(Array.IndexOf(new[] { "r", "g", "b", "a", "luminance" }, binding.Channel));
                    channel.ItemSelected += i => { binding.Channel = channel.GetItemText((int)i); RefreshPreview(); }; options.AddChild(channel);
                    AddToggle(options, "Invert", binding.Invert, v => { binding.Invert = v; RefreshPreview(); });
                }
                var colourSpace = new OptionButton { TooltipText = "Colour/emission images normally use sRGB. Normal, height, roughness, metallic and mask maps normally use linear data." };
                colourSpace.AddItem("Linear (data)"); colourSpace.AddItem("sRGB (colour)"); colourSpace.Select(binding.Srgb ? 1 : 0);
                colourSpace.ItemSelected += i => { binding.Srgb = i == 1; RefreshPreview(); }; options.AddChild(colourSpace);
            }
        }
        VBoxContainer surface = Tab("Surface"), depth = Tab("Depth"), animation = Tab("Texture Animation");
        var mode = new OptionButton(); foreach (string s in new[] { "Opaque", "Cutout", "Translucent" }) mode.AddItem(s);
        mode.Select(Current.Material.Surface == "masked" ? 1 : Current.Material.Surface == "translucent" ? 2 : 0);
        mode.ItemSelected += i => { Current.Material.Surface = i == 1 ? "masked" : i == 2 ? "translucent" : "opaque"; RefreshPreview(); }; surface.AddChild(mode);
        foreach (PropertyInfo property in typeof(AuthoredMaterial).GetProperties())
        {
            VBoxContainer target = property.Name is "NormalStrength" or "NormalDirectX" or "BumpFromHeight" or "ParallaxDepth" or "ParallaxSteps" or "Subdivision" or "Displacement" or "HeightMidpoint" or "DisplacementCollision" ? depth : surface;
            if (property.PropertyType == typeof(float))
            {
                if (property.Name == "Rotation") { AddNumber(target, "Mapping rotation (degrees)", -360, 360, 1, Current.Material.Rotation * 180 / Math.PI, v => { Current.Material.Rotation = (float)(v * Math.PI / 180); RefreshPreview(); }); continue; }
                double min = property.Name is "OffsetU" or "OffsetV" or "Rotation" or "Displacement" or "TileU" or "TileV" ? -1000 : 0;
                double max = property.Name is "Opacity" or "Roughness" or "Metallic" or "Specular" or "Cutoff" or "AoStrength" or "Clearcoat" or "ClearcoatGloss" or "Anisotropy" or "HeightMidpoint" ? 1 : 1000;
                AddNumber(target, Words(property.Name), min, max, .01, (float)property.GetValue(Current.Material)!, v => { property.SetValue(Current.Material, (float)v); RefreshPreview(); });
            }
            else if (property.PropertyType == typeof(bool))
                AddToggle(target, property.Name switch { "NormalDirectX" => "DirectX normals (off = OpenGL)", "BumpFromHeight" => "Generate normals from height", "DisplacementCollision" => "Displacement affects collision (default)", "BakeEmission" => "Bake emitted illumination", _ => Words(property.Name) }, (bool)property.GetValue(Current.Material)!, v => { property.SetValue(Current.Material, v); RefreshPreview(); });
            else if (property.PropertyType == typeof(float[]) && property.GetValue(Current.Material) is float[] { Length: 3 } color)
            {
                var button = new ColorPickerButton { Text = Words(property.Name), Color = new(color[0], color[1], color[2]), EditAlpha = false };
                button.ColorChanged += c => { property.SetValue(Current.Material, new float[] { c.R, c.G, c.B }); RefreshPreview(); }; surface.AddChild(button);
            }
        }
        AddNumber(depth, "Parallax quality", 4, 64, 1, Current.Material.ParallaxSteps, v => { Current.Material.ParallaxSteps = (int)v; RefreshPreview(); });
        AddNumber(depth, "Subdivision level", 0, 6, 1, Current.Material.Subdivision, v => { Current.Material.Subdivision = (int)v; RefreshPreview(); });
        depth.AddChild(new Label { Text = "A height map needs Parallax Depth or Displacement to add relief. Normals and roughness need Lit Preview in the map view. Displacement uses map units; collision is optional.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        foreach (PropertyInfo p in typeof(MaterialAnimation).GetProperties())
        {
            if (p.Name == "Rotate") { AddNumber(animation, "Rotation speed (degrees/s)", -3600, 3600, 1, Current.Material.Animation.Rotate * 180 / Math.PI, v => { Current.Material.Animation.Rotate = (float)(v * Math.PI / 180); RefreshPreview(); }); continue; }
            if (p.PropertyType == typeof(bool)) AddToggle(animation, Words(p.Name), (bool)p.GetValue(Current.Material.Animation)!, v => { p.SetValue(Current.Material.Animation, v); RefreshPreview(); });
            else AddNumber(animation, Words(p.Name), p.Name is "ScrollU" or "ScrollV" or "Phase" ? -1000 : p.Name is "Columns" or "Rows" or "Frames" ? 1 : 0,
                p.Name is "Columns" or "Rows" ? 128 : p.Name == "Fps" ? 240 : 16384, p.PropertyType == typeof(int) ? 1 : .01,
                Convert.ToDouble(p.GetValue(Current.Material.Animation)), v => { p.SetValue(Current.Material.Animation, p.PropertyType == typeof(int) ? (object)(int)v : (float)v); RefreshPreview(); });
        }
        animation.AddChild(new Label { Text = "Atlases play row by row. All material maps use the same frame. FPS 0 freezes playback; phase is measured in frames.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        _building = false; RefreshPreview();
    }

    private static string Words(string text) => System.Text.RegularExpressions.Regex.Replace(text, "([a-z])([A-Z])", "$1 $2");
    private static void AddToggle(Control target, string text, bool value, Action<bool> changed)
    { var field = new CheckBox { Text = text, ButtonPressed = value }; field.Toggled += value => changed(value); target.AddChild(field); }
    private static void AddNumber(Control target, string name, double min, double max, double step, double value, Action<double> changed)
    {
        var line = new HBoxContainer(); target.AddChild(line); line.AddChild(new Label { Text = name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var field = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new(120, 0) }; field.ValueChanged += value => changed(value); line.AddChild(field);
    }

    private void RefreshPreview()
    {
        if (_building || !IsInstanceValid(_mesh)) return;
        var problems = Current.Material.Problems();
        _status.Text = string.Join(" · ", Current.Errors.Concat(problems));
        string[] dataAsColour = Current.Material.Textures.Where(pair => pair.Value.Srgb && pair.Key is not ("baseColor" or "emission" or "detailColor" or "environment")).Select(pair => Words(pair.Key)).ToArray();
        if (dataAsColour.Length > 0) _status.Text += " Linear data recommended for: " + string.Join(", ", dataAsColour) + ". Use recommended colour spaces to correct them.";
        if (Current.Material.Textures.ContainsKey("height") && Current.Material.ParallaxDepth == 0 && Current.Material.Displacement == 0)
            _status.Text += " Height relief is off: set Parallax Depth for apparent depth, or Displacement and Subdivision for geometry.";
        GetOkButton().Disabled = (Current.Enabled && problems.Count > 0) || _writing;
        if (problems.Count == 0 && Current.Material.Displacement != 0)
            _status.Text += $" Geometry: {1L << (Current.Material.Subdivision * 2)} triangles per source triangle (patch limit 262144).";
        if (_selectionTriangles > 0) _status.Text += $" Selection: {_selectionTriangles} source triangles; up to {_selectionTriangles * (1L << (Current.Material.Subdivision * 2))} generated triangles.";
        StoreyClip.Register();
        Texture2D? Resolve(string slot, MaterialTexture binding)
        {
            binding = binding with { };
            MaterialAnimation layout = Current.Material.Animation with { };
            string key = Current.Sources.TryGetValue(slot, out string? source) ? source : Path.Combine(_folder, "assets", binding.File);
            string imageKey = key + ":" + AuthoredTextureImage.Key(slot, binding, layout);
            if (slot is not null && Current.Generated.TryGetValue(slot, out byte[]? generated))
            { imageKey = "generated:" + _current + ":" + slot + ":" + AuthoredTextureImage.Key(slot, binding, layout); if (!_images.ContainsKey(imageKey)) _images[imageKey] = FromPng(generated, slot, binding, layout); }
            if (_images.TryGetValue(imageKey, out Texture2D? known)) return known;
            if (File.Exists(key) && _pending.Add(imageKey))
            {
                string capturedKey = key;
                string capturedImageKey = imageKey;
                BackgroundJobs.Start(cancel =>
                {
                    try
                    {
                        byte[] png = File.ReadAllBytes(capturedKey);
                        if (OpenScct.Editor.Core.Identity.RgbaImage.FormatOf(png) != "PNG") png = OpenScct.Editor.Core.Identity.RgbaImage.Decode(png).EncodePng();
                        BackgroundJobs.Post(() => { if (!IsInstanceValid(this)) return; _images[capturedImageKey] = FromPng(png, slot ?? "baseColor", binding, layout); RefreshPreview(); });
                    }
                    catch (Exception ex) { BackgroundJobs.Post(() => { if (IsInstanceValid(this)) _status.Text = ex.Message; }); }
                });
            }
            return null;
        }
        _preview = AuthoredPreview.Make(Current.Material, _ => null, depthPreview: true, authoredTexture: Resolve);
        _mesh.MaterialOverride = _preview;
    }

    private static Texture2D FromPng(byte[] png, string slot, MaterialTexture binding, MaterialAnimation animation)
    { using var image = AuthoredTextureImage.Decode(png, slot, binding, animation); return ImageTexture.CreateFromImage(image); }

    private void Save()
    {
        if (_writing || !ReferenceEquals(Context.Session, _session)) return;
        StoreName(); _writing = true; GetOkButton().Disabled = true; _status.Text = "Importing materials…";
        JsonObject before = ProjectAssets.For(_folder).Edit();
        var captured = _drafts.Where(d => d.Enabled).Select(d =>
        {
            var copy = new TextureSetDraft { Name = d.Name, Material = d.Material.Copy(), Conflict = d.Conflict };
            foreach (var pair in d.Sources) copy.Sources[pair.Key] = pair.Value;
            foreach (var pair in d.Generated) copy.Generated[pair.Key] = pair.Value;
            copy.Errors.AddRange(d.Errors); return copy;
        }).ToList();
        if (captured.Count == 0) { _writing = false; GetOkButton().Disabled = false; _status.Text = "Enable at least one material to import."; return; }
        string group = _group.Text;
        TextureConflict conflict = _existing is null ? (TextureConflict)_conflict.Selected : TextureConflict.Replace;
        if (_existing is not null) { string[] parts = _existing.Split('.'); group = string.Join('.', parts.Skip(1).SkipLast(1)); captured[0].Name = parts[^1]; }
        CancellationToken token = _cancel.Token;
        BackgroundJobs.Run("Import", quit =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, quit);
            var results = new List<TextureImportResult>(); var errors = new List<string>(); var completed = new List<int>();
            foreach (TextureSetDraft draft in captured)
            {
                if (linked.IsCancellationRequested) break;
                try { results.Add(TextureImporter.Import(_folder, draft, group, _existing is null ? draft.Conflict : conflict, linked.Token)); completed.Add(captured.IndexOf(draft)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(draft.Name + ": " + ex.Message); }
                catch (OperationCanceledException) { break; }
            }
            return (results, errors, completed, after: ProjectAssets.For(_folder).Edit());
        }, result =>
        {
            // Imports already committed to this saved project's folder; never apply their selection to another map.
            if (!ReferenceEquals(Context.Session, _session)) return;
            if (result.results.Any(r => !r.Skipped))
            {
                void Restore(JsonObject manifest)
                {
                    JsonObject current = ProjectAssets.For(_folder).Edit();
                    foreach (string kind in new[] { "materials", "textures" })
                    {
                        JsonObject entries = (current[kind] ??= new JsonObject()).AsObject();
                        foreach (string path in result.results.Where(r => !r.Skipped).Select(r => r.Path))
                        {
                            foreach (string key in entries.Select(k => k.Key).Concat((manifest[kind] as JsonObject ?? []).Select(k => k.Key)).Where(key => key == path || key.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase)).Distinct().ToList())
                            { if (manifest[kind]?[key] is { } value) entries[key] = value.DeepClone(); else entries.Remove(key); }
                        }
                    }
                    ProjectAssets.Save(_folder, current); AssetService.Instance.ProjectAssetsChanged();
                }
                Context.Execute(new DelegateCommand(_existing is null ? "Import materials" : "Edit material", _ => Restore(result.after), _ => Restore(before)));
                _imported(result.results.First(r => !r.Skipped).Path);
            }
            foreach (string error in result.errors) Context.Log(error, Severity.Error);
            if (!IsInstanceValid(this)) return;
            var enabledDrafts = _drafts.Where(d => d.Enabled).ToList();
            foreach (int i in result.completed) enabledDrafts[i].Enabled = false;
            _enabled.SetPressedNoSignal(Current.Enabled);
            _writing = false; _status.Text = result.errors.Count > 0 ? string.Join("\n", result.errors) : $"Imported {result.results.Count(r => !r.Skipped)} material(s).";
            GetOkButton().Disabled = false;
            if (result.errors.Count == 0) QueueFree();
        });
    }
}
