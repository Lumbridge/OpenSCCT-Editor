using Godot;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Viewports;

/// <summary>
/// Converts between the map's Unreal space and Godot space, exactly as the game does (OpenSCCT UeSpace): UE
/// (x, y, z) in units becomes Godot (x, z, y) / 100 in metres. Swapping Y and Z turns UE's left-handed space into
/// Godot's right-handed one, so views are never mirrored. Directions are swapped but not scaled.
/// </summary>
public static class Space
{
    public const double UnitsPerMetre = 100;

    public static Vector3 ToGodot(Vec3 v) => new((float)(v.X / UnitsPerMetre), (float)(v.Z / UnitsPerMetre), (float)(v.Y / UnitsPerMetre));

    public static Vector3 DirToGodot(Vec3 d) => new((float)d.X, (float)d.Z, (float)d.Y);

    public static Vec3 ToUe(Vector3 p) => new(p.X * UnitsPerMetre, p.Z * UnitsPerMetre, p.Y * UnitsPerMetre);

    public static Vec3 DirToUe(Vector3 d) => new(d.X, d.Z, d.Y);

    /// <summary>The Godot basis of a UE rotation (columns: rotated UE X, Z and Y, mapped), a proper rotation.</summary>
    public static Basis RotationToGodot(Rot r)
    {
        (Vec3 f, Vec3 right, Vec3 up) = r.Axes();
        return new Basis(DirToGodot(f), DirToGodot(up), DirToGodot(right));
    }
}

/// <summary>The four editor views.</summary>
public enum ViewKind
{
    Perspective,
    Top,
    Front,
    Side,
}

/// <summary>How a view draws the map.</summary>
public enum RenderMode
{
    /// <summary>Brush edges coloured by CSG type, actor and GE lines.</summary>
    Wireframe,

    /// <summary>Wireframe plus translucent brush volumes in the CSG colours (add blue, subtract orange...).</summary>
    BrushColors,

    /// <summary>Solid surfaces with flat per-material colours, no lighting.</summary>
    Unlit,

    /// <summary>Solid surfaces lit by the map's lights (a preview, not the game's baked lighting).</summary>
    Lit,

    /// <summary>The Lit Preview bake's lightmap texels as a checkerboard, coloured by texel size against the map's
    /// default (green as dense as a default face, red denser, blue coarser).</summary>
    LightmapDensity,
}

/// <summary>
/// Godot visual layers the scene uses, so each view picks what it draws with its camera's cull mask instead of the
/// scene being built once per view.
/// </summary>
public static class VisualLayers
{
    /// <summary>Actors, GEs and the builder brush: drawn in every mode.</summary>
    public const uint Common = 1u << 0;

    public const uint BrushWire = 1u << 1;

    public const uint CsgVolumes = 1u << 2;

    public const uint Unlit = 1u << 3;

    public const uint Lit = 1u << 4;

    /// <summary>Selection outlines: drawn in every mode, over everything.</summary>
    public const uint Selected = 1u << 5;

    public const uint Density = 1u << 6;

    /// <summary>The picked brush faces of the surface tool, filled in 3D: in the perspective view only, in every mode
    /// (the 2D views outline them on their overlay).</summary>
    public const uint PickedFaces = 1u << 7;

    /// <summary>Actor icons and the direction arrows drawn with them: in the perspective view only, while View > Show >
    /// Actor Icons is on (<see cref="ForView"/>).</summary>
    public const uint ActorIcons = 1u << 8;

    /// <summary>The box and arrow of an actor drawn as an icon: in the 2D views, and in the perspective view while icons
    /// are off. Selected, they join <see cref="Selected"/> and show in every view.</summary>
    public const uint ActorBoxes = 1u << 9;

    /// <summary>The grid of one view (each view's grid differs, so each has its own layer).</summary>
    public static uint Grid(ViewKind view) => 1u << (10 + (int)view);

    public static uint ForMode(RenderMode mode) => Common | Selected | mode switch
    {
        RenderMode.Wireframe => BrushWire,
        RenderMode.BrushColors => BrushWire | CsgVolumes,
        RenderMode.Unlit => Unlit,
        RenderMode.LightmapDensity => Density,
        _ => Lit,
    };

    /// <summary>What a view draws: its mode's layers, its grid, and the perspective view's own layers (picked faces,
    /// and actor icons or, with icons off, the boxes they stand for).</summary>
    public static uint ForView(ViewKind view, RenderMode mode, bool actorIcons) => ForMode(mode) | Grid(view) | (view == ViewKind.Perspective
        ? PickedFaces | (actorIcons ? ActorIcons : ActorBoxes)
        : ActorBoxes);
}

/// <summary>Screen axes of the orthographic views, in UE axes (the stock editor's Top XY, Front XZ, Side YZ).</summary>
public static class ViewAxes
{
    /// <summary>The UE directions of screen right and screen up; null for the perspective view.</summary>
    public static (Vec3 Right, Vec3 Up)? For(ViewKind view) => view switch
    {
        ViewKind.Top => (Vec3.UnitX, new Vec3(0, -1, 0)),
        ViewKind.Front => (Vec3.UnitX, Vec3.UnitZ),
        ViewKind.Side => (Vec3.UnitY, Vec3.UnitZ),
        _ => null,
    };

    /// <summary>The UE axis index a view looks along (its depth axis): Top Z, Front Y, Side X.</summary>
    public static int DepthAxis(ViewKind view) => view switch
    {
        ViewKind.Top => 2,
        ViewKind.Front => 1,
        ViewKind.Side => 0,
        _ => -1,
    };
}
