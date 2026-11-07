using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Assets;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Panels;

/// <summary>A searchable palette of light presets, dragged onto any editor viewport.</summary>
[Panel("Lights", DockSide.Bottom, Order = 4)]
public partial class LightsPanel : EditorPanel
{
    private HFlowContainer _tiles = null!;
    private Label _count = null!;
    private HFlowContainer _fixtures = null!;
    private TabBar _tabs = null!;
    private string _query = "";
    private readonly List<(Control Tile, string Search)> _fixtureTiles = [];
    private Label _fixtureHint = null!;

    protected override void Build()
    {
        HFlowContainer bar = PanelUi.Toolbar(this);
        _tabs = new TabBar { ClipTabs = false, ThemeTypeVariation = "SubTabs", FocusMode = FocusModeEnum.None };
        _tabs.AddTab("Fixtures");
        _tabs.AddTab("Raw Lights");
        _tabs.TabChanged += _ => Filter(_query);
        bar.AddChild(_tabs);
        PanelUi.Search(bar, "Search lights", Filter, "Search light names and descriptions.");
        _count = PanelUi.Caption(bar, "", wrap: false);
        VBoxContainer scroll = PanelUi.Scroll(this);
        _fixtureHint = PanelUi.Caption(scroll, "");
        _fixtures = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_fixtures);
        _tiles = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _tiles.AddThemeConstantOverride("h_separation", EditorTheme.SpaceS);
        _tiles.AddThemeConstantOverride("v_separation", EditorTheme.SpaceS);
        scroll.AddChild(_tiles);
        foreach (LightPreset preset in LightPresets.All)
        {
            Vec3 rgb = LightColour.Chroma(preset.Hue, preset.Saturation);
            var tile = new PanelContainer
            {
                Name = preset.Title.Replace(" ", ""),
                CustomMinimumSize = new Vector2(130, 100),
                MouseFilter = MouseFilterEnum.Stop,
                MouseDefaultCursorShape = CursorShape.Drag,
                TooltipText = $"{preset.Title}\n{preset.Summary}\n\nDrag into a view to place. Select the light to edit it in Inspector.\n"
                    + (preset.Type == 1 ? "Build lighting to update baked lightmaps." : "Animated lighting runs in the game."),
            };
            tile.AddThemeStyleboxOverride("panel", EditorTheme.Box(EditorTheme.PanelLight,
                padX: EditorTheme.SpaceS, padY: EditorTheme.SpaceS));
            var content = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            tile.AddChild(content);
            content.AddChild(new TextureRect
            {
                Texture = Icons.Get(preset.Icon, new Color((float)rgb.X, (float)rgb.Y, (float)rgb.Z), 40),
                CustomMinimumSize = new Vector2(40, 48),
                StretchMode = TextureRect.StretchModeEnum.KeepCentered,
                MouseFilter = MouseFilterEnum.Ignore,
            });
            content.AddChild(new Label { Text = preset.Title, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore });
            tile.SetDragForwarding(Callable.From<Vector2, Variant>(_ => StartDrag(tile, preset)),
                Callable.From<Vector2, Variant, bool>((_, _) => false), Callable.From<Vector2, Variant>((_, _) => { }));
            _tiles.AddChild(tile);
        }
        PanelUi.Caption(this, "Drag into a view. Fixtures place mesh + light together; R rotates while dragging. Edit lights in Inspector.");
        AssetService.Instance.CatalogChanged += RebuildFixtures;
        TreeExiting += () => AssetService.Instance.CatalogChanged -= RebuildFixtures;
        RebuildFixtures();
        Filter("");
    }

    private void RebuildFixtures()
    {
        foreach (Node child in _fixtures.GetChildren()) { _fixtures.RemoveChild(child); child.QueueFree(); }
        _fixtureTiles.Clear();
        // Curated complete fixtures, excluding broken lamps, beam-only meshes and lightmap helper geometry.
        (string Match, string Title, string Mount, string Light)[] choices =
        [
            ("aqua_lampeplaf", "Ceiling Spotlight", "ceiling", "Spot"),
            ("BNK_plaflight", "Office Pendant", "ceiling", "Point"),
            ("CLU_neon_ceiling_intact", "Ceiling Fluorescent", "ceiling", "Cool Point"),
            ("DTK_Light_suspendue", "Hanging Lamp", "ceiling", "Warm Point"),
            ("LightWall_01_CHB", "Wall Lamp", "wall", "Warm Point"),
            ("LightWall_02_CHB", "Wall Fluorescent", "wall", "Cool Point"),
            ("DTK_tripod_light", "Tripod Work Light", "floor", "Point"),
            ("CLU_Lantern01", "Lantern", "floor", "Warm Point"),
            ("squ_lampeCamping", "Camping Lamp", "floor", "Warm Point"),
        ];
        foreach (var choice in choices)
        {
            AssetEntry? mesh = AssetService.Instance.Catalog.All.FirstOrDefault(e => e.Kind == AssetKind.Mesh
                && e.Name.Equals(choice.Match, StringComparison.OrdinalIgnoreCase));
            if (mesh is null) continue;
            LightPreset light = LightPresets.All.Single(p => p.Title == choice.Light);
            var tile = new PanelContainer
            {
                CustomMinimumSize = new Vector2(150, 120), MouseFilter = MouseFilterEnum.Stop,
                MouseDefaultCursorShape = CursorShape.Drag,
                TooltipText = $"{choice.Title} · mesh + {light.Title}\n{mesh.Path}\n\nDrop on a {choice.Mount}. R rotates 45°.\n"
                    + "Places the mesh and a separate editable light, selected together, in one undo step.\nBuild lighting to update baked lightmaps.",
            };
            tile.AddThemeStyleboxOverride("panel", EditorTheme.Box(EditorTheme.PanelLight, padX: EditorTheme.SpaceS, padY: EditorTheme.SpaceS));
            var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            tile.AddChild(box);
            var picture = new TextureRect
            {
                Texture = Icons.Get("type-light", EditorTheme.Text, 40), CustomMinimumSize = new Vector2(64, 64),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            box.AddChild(picture);
            box.AddChild(new Label { Text = choice.Title, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore });
            box.AddChild(new Label { Text = $"{choice.Mount} · mesh + light", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore });
            tile.SetDragForwarding(Callable.From<Vector2, Variant>(_ => AssetDragData.StartFixture(tile, mesh, light, picture.Texture)),
                Callable.From<Vector2, Variant, bool>((_, _) => false), Callable.From<Vector2, Variant>((_, _) => { }));
            _fixtures.AddChild(tile);
            _fixtureTiles.Add((tile, $"{choice.Title} {choice.Mount} {mesh.Path} {light.Title}"));
            AssetThumbnails.Instance?.Request(mesh, texture => { if (IsInstanceValid(picture)) picture.Texture = texture; },
                () => IsInstanceValid(picture) && picture.IsInsideTree());
        }
        Filter(_query);
    }

    private Variant StartDrag(Control tile, LightPreset preset)
    {
        var preview = new Label { Text = preset.Title, MouseFilter = MouseFilterEnum.Ignore };
        tile.SetDragPreview(preview);
        return Variant.From(new PrefabDragData(preset.Title, point => Place(preset, point), surfaceOffset: 16));
    }

    private void Place(LightPreset preset, Vec3 point)
    {
        Actor actor = preset.Create(Context.Project, point);
        Context.Execute(new AddObjectsCommand($"Place {preset.Title}", actors: [actor]));
        Context.Selection.Set([ObjectRef.Actor(actor.Id)]);
        string message = $"Placed {preset.Title} ({actor.Id}) at {point}. Adjust it in Inspector.";
        Context.SetStatus(message);
        Context.Log(message);
    }

    private void Filter(string query)
    {
        if (_tiles is null) return;
        _query = query;
        bool fixtures = _tabs.CurrentTab == 0;
        _tiles.Visible = !fixtures;
        _fixtures.Visible = fixtures;
        _fixtureHint.Visible = fixtures;
        _fixtureHint.Text = _fixtureTiles.Count == 0
            ? (AssetService.Instance.Loading ? "Loading fixture meshes…" : "Install map content packs to make fixture meshes available.")
            : "Complete fixtures from installed content. Drop on the indicated surface; each includes an editable light.";
        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int count = 0;
        if (fixtures)
        {
            foreach (var entry in _fixtureTiles)
            {
                entry.Tile.Visible = words.All(word => entry.Search.Contains(word, StringComparison.OrdinalIgnoreCase));
                if (entry.Tile.Visible) count++;
            }
            _count.Text = count == 0 ? "No matching fixtures" : PanelUi.Count(count, "fixture");
            return;
        }
        for (int i = 0; i < LightPresets.All.Count; i++)
        {
            LightPreset preset = LightPresets.All[i];
            bool match = words.All(word => $"{preset.Title} {preset.Summary}".Contains(word, StringComparison.OrdinalIgnoreCase));
            _tiles.GetChild<Control>(i).Visible = match;
            if (match) count++;
        }
        _count.Text = count == 0 ? "No matching lights" : PanelUi.Count(count, "light");
    }
}
