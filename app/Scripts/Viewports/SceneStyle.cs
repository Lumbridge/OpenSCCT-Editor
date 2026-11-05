using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Viewports;

/// <summary>Colours and shared materials of the viewports. Brush colours follow UnrealEd: add blue, subtract
/// orange, semisolid pink, non-solid green, portals yellow, the builder brush red. Preferences can change the brush,
/// selection, grid and background colours (<see cref="Defaults"/> holds the stock ones). Yellow is kept for the
/// selection: overlays use the muted <see cref="OverlayGuide"/>, <see cref="OverlayLink"/> and <see cref="OverlayText"/>
/// (docs/UI-DESIGN.md, "Viewport colours").</summary>
public static class SceneStyle
{
    public static Color AddBrush { get; set; } = new(0.38f, 0.55f, 1f);
    public static Color SubtractBrush { get; set; } = new(1f, 0.62f, 0.2f);
    public static Color SemisolidBrush { get; set; } = new(1f, 0.45f, 0.85f);
    public static Color NonSolidBrush { get; set; } = new(0.45f, 0.85f, 0.35f);
    public static Color PortalBrush { get; set; } = new(0.95f, 0.9f, 0.3f);
    public static Color BuilderBrush { get; set; } = new(1f, 0.25f, 0.25f);
    public static Color Selection { get; set; } = new(1f, 0.85f, 0.25f);
    public static Color Hover { get; set; } = new(0.6f, 0.95f, 1f);
    public static Color GridMinor { get; set; } = new(0.13f, 0.135f, 0.155f);
    public static Color GridMajor { get; set; } = new(0.2f, 0.21f, 0.25f);
    public static readonly Color AxisX = EditorTheme.AxisX;
    public static readonly Color AxisY = EditorTheme.AxisY;
    public static readonly Color AxisZ = EditorTheme.AxisZ;

    /// <summary>Picked brush faces (the face tool): filled at <see cref="FaceFillAlpha"/>, outlined at
    /// <see cref="FaceLineAlpha"/>. Blue, so picked faces never read as selected objects.</summary>
    public static readonly Color FaceSelection = new(0.3f, 0.62f, 1f);

    public const float FaceFillAlpha = 0.22f;

    /// <summary>The face a dragged material or texture would go on (and the picked faces it would go on with it): a bright
    /// green, apart from the picked faces' blue and the hover's cyan.</summary>
    public static readonly Color DropTarget = new(0.62f, 1f, 0.3f);

    /// <summary>The neighbour face a dragged object sticks to (neighbour snap): filled at <see cref="SnapFillAlpha"/> and
    /// outlined at <see cref="SnapLineAlpha"/> (2 px), the moving face outlined in the same colour so the two read as a
    /// pair. Violet: yellow is the selection, blue the picked faces, green the drop target, cyan the hover, and magenta
    /// would read as the semisolid brush pink.</summary>
    public static readonly Color SnapTarget = new(0.80f, 0.45f, 1f);

    public const float SnapFillAlpha = 0.30f;

    public const float SnapLineAlpha = 0.95f;

    /// <summary>The dashed guide along the shared plane (or the lined-up direction of a turn) while stuck.</summary>
    public static readonly Color SnapGuide = new(SnapTarget, 0.8f);
    public const float FaceLineAlpha = 0.9f;

    /// <summary>The fill of a picked face in the 2D views: lighter than in the 3D view, as faces seen flat on cover the
    /// brushes and actors behind them.</summary>
    public const float FaceFillAlpha2D = 0.12f;

    /// <summary>A 2D view fills a picked face only when it looks onto it at least this squarely (the cosine between
    /// the view and the face): faces seen edge-on or from behind are outlined only.</summary>
    public const double FaceFacing2D = 0.5;

    /// <summary>The clip tool's preview of the part a clip throws away: a light wash and a dashed outline in the error
    /// colour, so the kept part, the grid and the clip points stay readable.</summary>
    public static readonly Color ClipDiscardFill = new(EditorTheme.Error, 0.12f);

    public static readonly Color ClipDiscardLine = new(EditorTheme.Error, 0.7f);

    /// <summary>The fill over a selected brush in the 2D views.</summary>
    public const float SelectionFillAlpha = 0.1f;

    /// <summary>The fill over the other objects of a multiple selection (only the primary one gets
    /// <see cref="SelectionFillAlpha"/>): none, so overlapping selections never add up to a wash over the view.</summary>
    public const float SelectionMultiFillAlpha = 0f;

    /// <summary>Selected objects are outlined this wide (pixels) over the 3D lines.</summary>
    public const float SelectionWidth = 2;

    /// <summary>Dashed guides, frustums, help paths and light or sound reach: muted, so they never look selected.</summary>
    public static readonly Color OverlayGuide = new(0.62f, 0.6f, 0.42f, 0.55f);

    /// <summary>Event and trigger links and their arrows.</summary>
    public static readonly Color OverlayLink = new(0.45f, 0.65f, 1f, 0.8f);

    /// <summary>World labels that describe rather than mark a team or a problem (zone tips, mission text, radii).</summary>
    public static readonly Color OverlayText = new(0.8f, 0.78f, 0.62f);

    /// <summary>An overlay mark for something switched off (a light the sequence turns off): a cool grey.</summary>
    public static readonly Color OverlayOff = new(0.45f, 0.5f, 0.6f);

    /// <summary>Spy (team 0) and mercenary (team 1) starts and team labels.</summary>
    public static readonly Color TeamSpy = new(0.3f, 0.95f, 0.45f);
    public static readonly Color TeamMerc = new(1f, 0.35f, 0.3f);
    public static Color PerspectiveBackground { get; set; } = new(0.11f, 0.115f, 0.13f);
    public static Color OrthoBackground { get; set; } = new(0.075f, 0.08f, 0.09f);

    /// <summary>The colours Preferences can change, by key, with their stock values.</summary>
    public static readonly IReadOnlyList<(string Key, string Title, Color Default)> Defaults =
    [
        ("add", "Add brush", AddBrush),
        ("subtract", "Subtract brush", SubtractBrush),
        ("semisolid", "Semisolid brush", SemisolidBrush),
        ("nonsolid", "Non-solid brush", NonSolidBrush),
        ("portal", "Zone portal", PortalBrush),
        ("builder", "Builder brush", BuilderBrush),
        ("selection", "Selection", Selection),
        ("hover", "Hover", Hover),
        ("grid_minor", "Grid lines", GridMinor),
        ("grid_major", "Major grid lines", GridMajor),
        ("background_3d", "3D view background", PerspectiveBackground),
        ("background_2d", "2D view background", OrthoBackground),
    ];

    private static readonly Dictionary<string, Action<Color>> s_setters = new(StringComparer.Ordinal)
    {
        ["add"] = c => AddBrush = c,
        ["subtract"] = c => SubtractBrush = c,
        ["semisolid"] = c => SemisolidBrush = c,
        ["nonsolid"] = c => NonSolidBrush = c,
        ["portal"] = c => PortalBrush = c,
        ["builder"] = c => BuilderBrush = c,
        ["selection"] = c => Selection = c,
        ["hover"] = c => Hover = c,
        ["grid_minor"] = c => GridMinor = c,
        ["grid_major"] = c => GridMajor = c,
        ["background_3d"] = c => PerspectiveBackground = c,
        ["background_2d"] = c => OrthoBackground = c,
    };

    private static StandardMaterial3D? s_lines;
    private static StandardMaterial3D? s_selectedLines;
    private static StandardMaterial3D? s_grid;
    private static readonly Dictionary<(bool Lit, bool TwoSided), StandardMaterial3D> s_surfaces = new();
    private static readonly Dictionary<Color, StandardMaterial3D> s_volumes = new();

    /// <summary>Sets a colour by its <see cref="Defaults"/> key (unknown keys are ignored).</summary>
    public static void Set(string key, Color c)
    {
        if (s_setters.TryGetValue(key, out Action<Color>? set))
        {
            set(c);
        }
    }

    /// <summary>Drops the shared materials that bake a colour in, so they are made again with the current colours.</summary>
    public static void ResetMaterials() => s_selectedLines = null;

    /// <summary>Wire colour of a brush by its CSG type and flags.</summary>
    public static Color BrushColor(Brush b) =>
        b.IsPortal ? PortalBrush
        : b.IsNotSolid ? NonSolidBrush
        : b.IsSemisolid ? SemisolidBrush
        : b.Oper == CsgOper.Add ? AddBrush
        : SubtractBrush;

    public static Color AxisColor(int axis) => axis switch { 0 => AxisX, 1 => AxisY, _ => AxisZ };

    /// <summary>A guide line, brighter for the selected object it belongs to.</summary>
    public static Color Guide(bool selected) => selected ? new Color(OverlayGuide.Lightened(0.25f), 0.95f) : OverlayGuide;

    /// <summary>A colour as the lines of an object that is not selected show it (selected ones use it at full strength).</summary>
    public static Color Unselected(Color c) => new(c, c.A * 0.6f);

    /// <summary>Unshaded lines coloured per vertex. Line and grid colours are written as sRGB, like the UI's; without
    /// the flag Godot takes vertex colours as linear and draws them much too bright.</summary>
    public static StandardMaterial3D Lines => s_lines ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
    };

    /// <summary>Selection outlines: drawn over everything so the selection is always visible.</summary>
    public static StandardMaterial3D SelectedLines => s_selectedLines ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = Selection,
        NoDepthTest = true,
        RenderPriority = 10,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
    };

    public static StandardMaterial3D Grid => s_grid ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
    };

    /// <summary>Solid surfaces coloured per vertex: unshaded, or lit by the map's lights. Meshes are wound so the
    /// visible side is the front (subtractive brushes are wound inwards), so one-sided surfaces cull back faces.</summary>
    public static StandardMaterial3D Surface(bool lit, bool twoSided = false)
    {
        if (!s_surfaces.TryGetValue((lit, twoSided), out StandardMaterial3D? m))
        {
            m = new StandardMaterial3D
            {
                ShadingMode = lit ? BaseMaterial3D.ShadingModeEnum.PerPixel : BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                CullMode = twoSided ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Back,
                Roughness = 1,
                MetallicSpecular = 0.1f,
            };
            s_surfaces[(lit, twoSided)] = m;
        }

        return m;
    }

    /// <summary>Translucent CSG volume of one colour (the brush-colours mode).</summary>
    public static StandardMaterial3D Volume(Color c)
    {
        if (!s_volumes.TryGetValue(c, out StandardMaterial3D? m))
        {
            m = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(c, 0.16f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            };
            s_volumes[c] = m;
        }

        return m;
    }

    /// <summary>A stable colour for a material path, so surfaces of one material read as one in the unlit view.</summary>
    public static Color MaterialColor(string? material)
    {
        if (string.IsNullOrEmpty(material))
        {
            return new Color(0.6f, 0.6f, 0.62f);
        }

        uint h = 2166136261;
        foreach (char ch in material.ToUpperInvariant())
        {
            h = (h ^ ch) * 16777619;
        }

        float hue = (h & 0xFFFF) / 65535f;
        float sat = 0.18f + ((h >> 16) & 0xFF) / 255f * 0.22f;
        float val = 0.55f + ((h >> 24) & 0xFF) / 255f * 0.3f;
        return Color.FromHsv(hue, sat, val);
    }
}
