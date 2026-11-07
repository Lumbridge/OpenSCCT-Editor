using System.Globalization;
using Godot;
using OpenScctEditor.Core;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Panels;

/// <summary>Searchable geometry palettes whose presets remain editable builder previews until committed.</summary>
[Panel("Geometry", DockSide.Left, Order = -18)]
public partial class GeometryPanel : EditorPanel
{
    private ItemList _list = null!;
    private OptionButton _category = null!;
    private Label _details = null!;
    private string _query = "";
    private readonly List<GeometryPreset> _shown = [];
    private readonly Dictionary<string, Texture2D> _pictures = new();

    protected override void Build()
    {
        PanelUi.Title(this, "Geometry");
        PanelUi.Caption(this, "Common floors, walls and shapes. Drag a preset into a view or double-click to position its builder preview.");
        var bar = PanelUi.Toolbar(this);
        _category = new OptionButton { CustomMinimumSize = new Vector2(140, 0) };
        _category.AddItem("All geometry");
        foreach (string category in GeometryPresets.All.Select(p => p.Category).Distinct()) _category.AddItem(category);
        bar.AddChild(_category);
        _category.ItemSelected += _ => Fill();
        PanelUi.Search(bar, "Search geometry", query => { _query = query; Fill(); }, "Search preset names, categories, shapes and dimensions");
        _list = new ItemList { IconMode = ItemList.IconModeEnum.Top, MaxColumns = 2, FixedColumnWidth = 140,
            FixedIconSize = new Vector2I(120, 80), CustomMinimumSize = new Vector2(0, 245), SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(_list);
        _list.Resized += () =>
        {
            float gap = 2 * _list.GetThemeConstant("h_separation");
            float room = _list.Size.X - _list.GetThemeStylebox("panel").GetMinimumSize().X - _list.GetVScrollBar().GetCombinedMinimumSize().X;
            _list.FixedColumnWidth = Math.Max(1, (int)(room / 2 - gap - 1));
            _list.FixedIconSize = new Vector2I(Math.Clamp(_list.FixedColumnWidth - 8, 1, 120), 80);
        };
        _list.ItemSelected += _ => Details();
        _list.ItemActivated += _ => Use();
        _list.SetDragForwarding(Callable.From<Vector2, Variant>(Drag), Callable.From((Vector2 _, Variant _) => false), Callable.From((Vector2 _, Variant _) => { }));
        _details = PanelUi.Caption(this, "");
        var actions = PanelUi.Toolbar(this);
        PanelUi.Button(actions, "Use builder preview", "Position and size this preset before Add/Subtract", Use);
        PanelUi.Button(actions, "Editable stairs", "Open the dedicated Stairs palette", () => DockTabs.ShowPanel("Stairs"));
        PanelUi.Caption(this, "Presets use Unreal units. Add/Subtract commits the preview; Escape cancels placement. Sheets are non-solid.");
        Fill();
    }

    private GeometryPreset? Selected => _list.GetSelectedItems() is { Length: > 0 } indices && indices[0] < _shown.Count ? _shown[indices[0]] : null;
    private void Fill()
    {
        GeometryPreset? keep = Selected;
        string? category = _category.Selected > 0 ? _category.GetItemText(_category.Selected) : null;
        _shown.Clear();
        _shown.AddRange(GeometryPresets.All.Where(p => (category is null || p.Category == category)
            && (_query.Length == 0 || $"{p.Title} {p.Category} {p.Kind} {p.Summary}".Contains(_query, StringComparison.OrdinalIgnoreCase))));
        _list.Clear();
        foreach (GeometryPreset preset in _shown)
        {
            if (!_pictures.TryGetValue(preset.Title, out Texture2D? picture))
                _pictures[preset.Title] = picture = Thumbnail(preset.Build());
            int i = _list.AddItem(preset.Title, picture);
            _list.SetItemTooltip(i, $"{preset.Category}: {preset.Title}\n{preset.Summary}\n\nDrag to a view or double-click. This loads a preview; Add/Subtract creates geometry.");
        }
        if (_shown.Count > 0) _list.Select(Math.Max(0, keep is null ? 0 : _shown.IndexOf(keep)));
        Details();
    }
    private void Details() => _details.Text = Selected is { } preset ? preset.Summary : "No matching presets";
    private BrushTool Tool => (BrushTool)ToolManager.Instance!.Tools.First(t => t is BrushTool);
    private void Use()
    {
        if (Selected is { } preset) Tool.LoadPreset(preset.Build(ToolManager.Instance!.Builder.Body?.Invoke()));
    }
    private Variant Drag(Vector2 at)
    {
        int index = _list.GetItemAtPosition(at, true);
        if (index < 0) return default;
        _list.Select(index);
        GeometryPreset preset = _shown[index];
        BuilderSpec spec = preset.Build(ToolManager.Instance!.Builder.Body?.Invoke());
        var session = Context.Session;
        _list.SetDragPreview(new Label { Text = preset.Title + " builder preview" });
        return Variant.From(new PrefabDragData(preset.Title, point =>
        {
            if (session == Context.Session) Tool.LoadPreset(spec, point);
        }) { GeometryPreview = spec });
    }

    private static Texture2D Thumbnail(BuilderSpec spec)
    {
        var shape = BuilderCatalog.Get(spec.Kind).Build(spec).SelectMany(p => p.Shape.Faces).ToList();
        Vector2 Project(Vec3 p) => new((float)(p.X - p.Y) * .6f, (float)(p.X + p.Y) * .25f - (float)p.Z * .7f);
        Vector2[] points = shape.SelectMany(f => f.Vertices).Select(Project).ToArray();
        float minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        float scale = Math.Min(108 / Math.Max(1, maxX - minX), 68 / Math.Max(1, maxY - minY));
        var paths = new System.Text.StringBuilder();
        foreach (BrushFace face in GeometryThumbnailFaces.Visible(shape))
        {
            string vertices = string.Join(' ', face.Vertices.Select(Project).Select(p =>
                string.Create(CultureInfo.InvariantCulture, $"{(p.X-(minX+maxX)/2)*scale+60:0.##},{(p.Y-(minY+maxY)/2)*scale+40:0.##}")));
            Vec3 normal = face.Normal();
            string fill = normal.Z > .5 ? "#658d9e" : normal.X > .5 ? "#3f6577" : "#2b4b5d";
            paths.Append($"<polygon points='{vertices}' fill='{fill}' stroke='#a3d1e0' stroke-width='0.8'/>");
        }
        var image = new Image();
        image.LoadSvgFromString($"<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80'>{paths}</svg>");
        return ImageTexture.CreateFromImage(image);
    }
}
