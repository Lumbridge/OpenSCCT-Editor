using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Viewports.Render;

/// <summary>When an actor shows an icon in the perspective view.</summary>
public enum IconUse
{
    /// <summary>Never: a volume or other brush-shaped actor, drawn by its own shape.</summary>
    None,

    /// <summary>Always: an actor with nothing else to draw (a light, a start, a trigger), instead of its box.</summary>
    Always,

    /// <summary>Only while selected, smaller: an actor drawn with its mesh (a static mesh, a camera, a conveyor).</summary>
    WhenSelected,
}

/// <summary>
/// The perspective view's actor icons (<see cref="ActorIcons"/>), all in one <see cref="MultiMesh"/> drawn in one call
/// with one material. Each icon is a screen-facing quad the vertex shader sizes in pixels: an icon has
/// <see cref="WorldSize"/> units in the world, but is never drawn larger than <see cref="SizePx"/> (near) nor smaller
/// than <see cref="MinFraction"/> of it, or <see cref="MinPixels"/> (far). Depth is judged at the actor, not per pixel:
/// the shader reads the opaque pass's depth at the actor and at four points close around it, and shows the whole icon
/// when any of them is open, or hides the whole icon when the world stands in front (a wall). A surface less than about
/// the icon's half size in front of the actor (<see cref="Pull"/>) does not count, so a light set into a ceiling or a
/// start standing on the floor shows whole and is never cut by the surface it sits on; an actor buried less than
/// <see cref="BuriedDepth"/> behind a surface (a start inside a crate) shows faint through it. Icons sort among
/// themselves by depth and carry a dark halo that keeps them readable on any surface. A selected icon is drawn in the
/// selection colour over everything, at full size however far; an actor greyed out of the match shows faint. The
/// instance buffer is written only when the drawn icons change (<see cref="WorldScene"/> marks it), never per frame.
/// </summary>
public sealed partial class ActorIconBatch : MultiMeshInstance3D
{
    /// <summary>How big an icon is in the world, in units, between the screen limits.</summary>
    public const double WorldSize = 80;

    /// <summary>The smallest an icon is drawn, as a part of <see cref="SizePx"/>.</summary>
    public const float MinFraction = 0.55f;

    /// <summary>The smallest a far icon is drawn, in UI pixels (or <see cref="SizePx"/> when that is smaller).</summary>
    public const float MinPixels = 10;

    /// <summary>The size of the icon of a selected mesh actor, as a part of a full icon.</summary>
    public const float SelectedMeshScale = 0.75f;

    /// <summary>How far behind a surface an actor may be and still show its icon, faint, in units: a start inside a
    /// crate or a light just behind a thin wall. Deeper (another room), the icon is hidden.</summary>
    public const double BuriedDepth = 320;

    /// <summary>The opacity of a faint icon: buried, or of an actor greyed out of the match.</summary>
    public const float FaintAlpha = 0.4f;

    /// <summary>Floats per instance: a 3×4 transform, a colour and the custom data (atlas cell, scale, on top).</summary>
    private const int Stride = 20;

    /// <summary>How near the camera a surface may be in front of an actor without hiding its icon, in units: about the
    /// icon's own half size, within these (the icon is drawn that much nearer too).</summary>
    private const double MinPull = 12, MaxPull = 40;

    private static readonly StringName s_sizeParam = "size_px";
    private static readonly StringName s_minParam = "min_px";
    private static ShaderMaterial? s_material;
    private static int s_size = 26;
    private static float s_pixelScale = 1;
    private float[] _buffer = [];
    private int _count;

    public ActorIconBatch()
    {
        Name = "ActorIcons";
        Layers = VisualLayers.ActorIcons;
        CastShadow = ShadowCastingSetting.Off;
        // The quads are sized in the vertex shader, so the instances' own bounds mean nothing: never cull the batch.
        CustomAabb = new Aabb(new Vector3(-5000, -5000, -5000), new Vector3(10000, 10000, 10000));
        MaterialOverride = Material;
        Multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            Mesh = Quad(),
        };
    }

    /// <summary>Whether the perspective view shows icons (View > Show > Actor Icons, <see cref="Preferences.ActorIcons"/>).</summary>
    public static bool Shown { get; set; } = true;

    /// <summary>The largest an icon is drawn, in UI pixels (<see cref="Preferences.ActorIconSize"/>).</summary>
    public static int SizePx
    {
        get => s_size;
        set
        {
            s_size = value;
            ApplySize();
        }
    }

    /// <summary>The icon an actor shows in the perspective view: none for a brush-shaped actor, a small one while
    /// selected for one drawn with a mesh (its static or skeletal mesh, or a conveyor's; one whose mesh does not ship
    /// keeps its box, as an imported map's static mesh actors do), else always.</summary>
    public static IconUse Use(Actor a) =>
        a.Shape is { Faces.Count: > 0 } ? IconUse.None
        : ConveyorDesign.Is(a) || StaticMeshDrawing.MeshPath(a) is not null || StaticMeshDrawing.SkeletalPath(a) is not null ? IconUse.WhenSelected
        : IconUse.Always;

    /// <summary>The size an icon is drawn at <paramref name="at"/>, in the view's UI pixels (picking uses it): a
    /// selected one at full size, a mesh actor's smaller.</summary>
    public static double ScreenPixels(ViewCamera cam, Vec3 at, IconUse use = IconUse.Always, bool selected = false)
    {
        double scale = use == IconUse.WhenSelected ? SelectedMeshScale : 1;
        return (selected ? s_size : Math.Clamp(WorldSize / cam.UnitsPerPixelAt(at), SmallestPx, s_size)) * scale;
    }

    /// <summary>How far in front of its actor a surface hides an icon drawn with <paramref name="pull"/>, in units (a
    /// nearer surface hides it, a farther one leaves it faint or whole).</summary>
    public static double HiddenBy(double pull) => pull + BuriedDepth;

    /// <summary>The smallest a far icon is drawn, in UI pixels.</summary>
    private static float SmallestPx => Math.Max(s_size * MinFraction, Math.Min(MinPixels, s_size));

    /// <summary>How far in front of its actor a surface may be without hiding an icon of <paramref name="pixels"/> at
    /// <paramref name="at"/>, in units, as the shader judges it (picking takes the icon there).</summary>
    public static double Pull(ViewCamera cam, Vec3 at, double pixels)
    {
        double depth = Math.Max(Vec3.Dot(at - cam.Location, cam.Axes.Forward), 1);
        return Math.Min(Math.Clamp(0.5 * pixels * cam.UnitsPerPixelAt(at), MinPull, MaxPull), depth * 0.5);
    }

    /// <summary>Takes the perspective view's rendered pixels per UI pixel (the UI scale), which icons are sized in.</summary>
    public static void SetPixelScale(float pixelScale)
    {
        pixelScale = pixelScale > 0 ? pixelScale : 1;
        if (!Mathf.IsEqualApprox(pixelScale, s_pixelScale))
        {
            s_pixelScale = pixelScale;
            ApplySize();
        }
    }

    /// <summary>Starts a new set of icons (the old ones stay drawn until <see cref="Commit"/>).</summary>
    public void Begin() => _count = 0;

    /// <summary>Adds an icon at <paramref name="position"/> (Godot space); a tint alpha below 1 draws it faint.</summary>
    public void Add(Vector3 position, Color tint, Vector2I cell, float scale, bool onTop)
    {
        int at = _count * Stride;
        if (at + Stride > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(256 * Stride, _buffer.Length * 2));
        }

        float[] b = _buffer;
        // Rows of the 3×4 transform: identity basis, the position in the last column.
        b[at] = 1;
        b[at + 1] = 0;
        b[at + 2] = 0;
        b[at + 3] = position.X;
        b[at + 4] = 0;
        b[at + 5] = 1;
        b[at + 6] = 0;
        b[at + 7] = position.Y;
        b[at + 8] = 0;
        b[at + 9] = 0;
        b[at + 10] = 1;
        b[at + 11] = position.Z;
        b[at + 12] = tint.R;
        b[at + 13] = tint.G;
        b[at + 14] = tint.B;
        b[at + 15] = tint.A;
        b[at + 16] = cell.X;
        b[at + 17] = cell.Y;
        b[at + 18] = scale;
        b[at + 19] = onTop ? 1 : 0;
        _count++;
    }

    /// <summary>Draws the icons added since <see cref="Begin"/>.</summary>
    public void Commit()
    {
        MultiMesh mm = Multimesh;
        int capacity = _buffer.Length / Stride;
        if (mm.InstanceCount != capacity)
        {
            mm.InstanceCount = capacity;
        }

        mm.VisibleInstanceCount = _count;
        if (capacity > 0)
        {
            mm.Buffer = _buffer;
        }
    }

    private static void ApplySize()
    {
        if (s_material is null)
        {
            return;
        }

        s_material.SetShaderParameter(s_sizeParam, s_size * s_pixelScale);
        s_material.SetShaderParameter(s_minParam, SmallestPx * s_pixelScale);
    }

    private static ShaderMaterial Material
    {
        get
        {
            if (s_material is not null)
            {
                return s_material;
            }

            Vector2I cells = ActorIcons.AtlasCells;
            s_material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
            s_material.SetShaderParameter("atlas", ActorIcons.Atlas);
            s_material.SetShaderParameter("cells", new Vector2(cells.X, cells.Y));
            s_material.SetShaderParameter("world_size", (float)(WorldSize / Space.UnitsPerMetre));
            s_material.SetShaderParameter("min_pull", (float)(MinPull / Space.UnitsPerMetre));
            s_material.SetShaderParameter("max_pull", (float)(MaxPull / Space.UnitsPerMetre));
            s_material.SetShaderParameter("buried", (float)(BuriedDepth / Space.UnitsPerMetre));
            s_material.SetShaderParameter("faint", FaintAlpha);
            ApplySize();
            return s_material;
        }
    }

    /// <summary>A unit quad facing +Z, UV origin at the top left.</summary>
    private static ArrayMesh Quad()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[] { new(-0.5f, -0.5f, 0), new(0.5f, -0.5f, 0), new(0.5f, 0.5f, 0), new(-0.5f, 0.5f, 0) };
        arrays[(int)Mesh.ArrayType.Normal] = new Vector3[] { Vector3.Back, Vector3.Back, Vector3.Back, Vector3.Back };
        arrays[(int)Mesh.ArrayType.TexUV] = new Vector2[] { new(0, 1), new(1, 1), new(1, 0), new(0, 0) };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 2, 1, 0, 3, 2 };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private const string ShaderCode = """
        shader_type spatial;
        render_mode unshaded, cull_disabled, shadows_disabled, depth_draw_always, fog_disabled;

        uniform sampler2D atlas : filter_linear_mipmap, repeat_disable;
        // The world as drawn so far (the opaque pass): an icon shows when the world does not hide its actor.
        uniform sampler2D depth_view : hint_depth_texture, filter_nearest, repeat_disable;
        uniform vec2 cells = vec2(8.0, 6.0);
        // Rendered pixels: the largest and smallest an icon is drawn.
        uniform float size_px = 26.0;
        uniform float min_px = 14.0;
        // Metres: the icon's size in the world between those limits, and how far it is pulled towards the camera.
        uniform float world_size = 0.8;
        uniform float min_pull = 0.12;
        uniform float max_pull = 0.4;
        // Metres: how far behind a surface an actor may be and still show its icon, faint; and how faint.
        uniform float buried = 3.2;
        uniform float faint = 0.4;
        varying vec2 icon_uv;
        varying vec4 tint;
        // xy: the actor's screen position (as SCREEN_UV); z: the distance a surface must be nearer than to hide it;
        // w: 1 when selected (always shown).
        varying flat vec4 probe;
        // The spread of the four outer probes around the actor, in screen UV.
        varying flat vec2 spread;

        vec3 to_linear(vec3 c) {
            return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
        }

        void vertex() {
            vec3 centre = (MODELVIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
            float depth = max(-centre.z, 0.01);
            // The projection flips Y (its [1][1] is negative): use its size, and its sign for screen up.
            float flip = sign(PROJECTION_MATRIX[1][1]);
            float px_per_m = abs(PROJECTION_MATRIX[1][1]) * VIEWPORT_SIZE.y * 0.5 / depth;
            // A selected icon stays at full size however far it is.
            float full = INSTANCE_CUSTOM.w > 0.5 ? size_px : clamp(world_size * px_per_m, min_px, size_px);
            float px = full * INSTANCE_CUSTOM.z;
            float pull = min(clamp(0.5 * px / px_per_m, min_pull, max_pull), depth * 0.5);
            vec4 actor = PROJECTION_MATRIX * vec4(centre, 1.0);
            probe = vec4(actor.xy / actor.w * 0.5 + 0.5, depth - pull, INSTANCE_CUSTOM.w);
            spread = vec2(0.3 * px) / VIEWPORT_SIZE;

            vec3 front = centre * (1.0 - pull / max(length(centre), 0.001));
            POSITION = PROJECTION_MATRIX * vec4(front, 1.0);
            POSITION.xy += vec2(VERTEX.x, VERTEX.y * flip) * px * 2.0 / VIEWPORT_SIZE * POSITION.w;
            // In front of the world (the fragment decides what the world hides), in their own depth order among
            // themselves; a selected icon in front of them all. Reversed depth: 1 is the near plane.
            float z = clamp(POSITION.z / POSITION.w, 0.0, 1.0);
            POSITION.z = (INSTANCE_CUSTOM.w > 0.5 ? 0.99999 : 0.99 + 0.0099 * z) * POSITION.w;

            icon_uv = (INSTANCE_CUSTOM.xy + UV) / cells;
            tint = vec4(to_linear(COLOR.rgb), COLOR.a);
        }

        // How far away the world is at a screen point (0 off screen, where nothing is seen).
        float world_at(vec2 uv, mat4 inv_projection) {
            if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0) {
                return 0.0;
            }

            float d = texture(depth_view, uv).r;
            vec4 v = inv_projection * vec4(uv * 2.0 - 1.0, d, 1.0);
            return -v.z / v.w;
        }

        void fragment() {
            vec4 t = texture(atlas, icon_uv);
            if (t.a < 0.5) {
                discard;
            }

            float alpha = tint.a;
            if (probe.w < 0.5) {
                // Seen if the world leaves the actor or a point around it open: a line or a thin rail across it does
                // not hide it, a wall does. Just behind a surface (buried in a crate), it shows faint.
                mat4 inv = INV_PROJECTION_MATRIX;
                float open = max(max(world_at(probe.xy, inv), world_at(probe.xy + vec2(spread.x, 0.0), inv)),
                    max(max(world_at(probe.xy - vec2(spread.x, 0.0), inv), world_at(probe.xy + vec2(0.0, spread.y), inv)),
                        world_at(probe.xy - vec2(0.0, spread.y), inv)));
                if (open < probe.z) {
                    if (open < probe.z - buried) {
                        discard;
                    }

                    alpha = min(alpha, faint);
                }
            }

            ALBEDO = t.rgb * tint.rgb;
            ALPHA = alpha;
        }
        """;
}
