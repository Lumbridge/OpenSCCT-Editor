using Godot;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Model;
using CatalogDefaults = OpenScct.Editor.Core.Gameplay.ClassDefaults;

namespace OpenScctEditor.Shell;

/// <summary>
/// The icon of each kind of actor (<see cref="ActorKinds"/>: lights, starts, cameras, objectives, triggers…), for any
/// UI that lists or shows actors: <see cref="For(string)"/> gives a class's icon as a 16 px <see cref="Icons"/>
/// texture, and the perspective view draws the same drawings as billboards from <see cref="Atlas"/>. The drawings are
/// <see cref="Icons"/>' <c>type-*</c> line drawings, so they follow the design system (docs/UI-DESIGN.md §1.11, §4).
/// </summary>
public static class ActorIcons
{
    /// <summary>Pixels per atlas cell (the 16-unit drawing at 4×, sharp up to the largest icon size).</summary>
    public const int CellPixels = 64;

    /// <summary>Atlas cells per row.</summary>
    public const int Columns = 8;

    /// <summary>The dark halo around each atlas drawing, in cell pixels, so an icon reads on any background.</summary>
    private const int HaloPixels = 3;

    private static readonly ActorKind[] s_kinds = Enum.GetValues<ActorKind>();
    private static ImageTexture? s_atlas;

    /// <summary>The <see cref="Icons"/> drawing of a kind.</summary>
    public static string IconName(ActorKind kind) => kind switch
    {
        ActorKind.PointLight => "type-light",
        ActorKind.SpotLight => "type-light-spot",
        ActorKind.SunLight => "type-light-sun",
        ActorKind.TriggerLight => "type-light-trigger",
        ActorKind.PlayerStart => "type-start",
        ActorKind.SpyStart => "type-start-spy",
        ActorKind.MercStart => "type-start-merc",
        ActorKind.Camera => "type-camera",
        ActorKind.CameraNetwork => "type-camnet",
        ActorKind.Laser => "type-laser",
        ActorKind.MotionSensor => "type-sensor-motion",
        ActorKind.PresenceSensor => "type-sensor-presence",
        ActorKind.Alarm => "type-alarm",
        ActorKind.Mine => "type-mine",
        ActorKind.Computer => "type-laptop",
        ActorKind.BombTarget => "type-bomb",
        ActorKind.Disk => "type-disk",
        ActorKind.Flag => "type-flag",
        ActorKind.FlagDropZone => "type-flag-zone",
        ActorKind.Objective => "type-objective",
        ActorKind.Mission => "type-mission",
        ActorKind.AmmoStation => "type-ammo",
        ActorKind.HealthStation => "type-health",
        ActorKind.Switch => "type-switch",
        ActorKind.Door => "type-door",
        ActorKind.Lift => "type-lift",
        ActorKind.Mover => "type-mover",
        ActorKind.Trigger => "type-trigger",
        ActorKind.Message => "type-message",
        ActorKind.Volume => "type-volume",
        ActorKind.Zone => "type-zone",
        ActorKind.Sound => "type-sound",
        ActorKind.Emitter => "type-emitter",
        ActorKind.Projector => "type-projector",
        ActorKind.MagicEvent => "type-event",
        ActorKind.Keypoint => "pin",
        ActorKind.Note => "type-note",
        ActorKind.ZoneTip => "type-tip",
        ActorKind.HelpPath => "type-path",
        ActorKind.Conveyor => "type-conveyor",
        ActorKind.Glass => "type-glass",
        ActorKind.GameplayObject => "type-gameplay",
        ActorKind.StaticMesh => "type-mesh",
        ActorKind.Pawn => "type-pawn",
        _ => "type-actor",
    };

    /// <summary>The kind of a class (full or short name), following its super chain in the class catalog.</summary>
    public static ActorKind KindOf(string className) => ActorKinds.OfClass(className, CatalogDefaults.Catalog);

    /// <summary>The kind of an actor: its class's, refined by its values (a spot light, a mercenary start).</summary>
    public static ActorKind KindOf(Actor a) => ActorKinds.Of(a, CatalogDefaults.Catalog);

    /// <summary>A class's icon in the passive glyph colour (<see cref="EditorTheme.TextDim"/>), 16 px.</summary>
    public static Texture2D For(string className) => For(className, EditorTheme.TextDim);

    /// <summary>A class's icon in <paramref name="color"/>, <paramref name="size"/> UI pixels square.</summary>
    public static Texture2D For(string className, Color color, int size = EditorTheme.IconSize) => For(KindOf(className), color, size);

    /// <summary>An actor's icon (its own values can make a light a spot light, a start a mercenary one).</summary>
    public static Texture2D For(Actor a, Color color, int size = EditorTheme.IconSize) => For(KindOf(a), color, size);

    /// <summary>A kind's icon in <paramref name="color"/>, <paramref name="size"/> UI pixels square.</summary>
    public static Texture2D For(ActorKind kind, Color color, int size = EditorTheme.IconSize) =>
        Icons.Get(IconName(kind), color, size) ?? Icons.Get("type-actor", color, size)!;

    /// <summary>The atlas cell (column, row) of a kind in <see cref="Atlas"/>.</summary>
    public static Vector2I Cell(ActorKind kind) => new((int)kind % Columns, (int)kind / Columns);

    /// <summary>Columns and rows of <see cref="Atlas"/>.</summary>
    public static Vector2I AtlasCells => new(Columns, (s_kinds.Length + Columns - 1) / Columns);

    /// <summary>
    /// Every kind's drawing in one texture for the views' billboards (<see cref="Cell"/>): white strokes with a dark
    /// halo, on transparent, so a tint colours the drawing and the halo keeps it readable over light and dark
    /// surfaces alike. Made once, with mipmaps.
    /// </summary>
    public static Texture2D Atlas => s_atlas ??= MakeAtlas();

    private static ImageTexture MakeAtlas()
    {
        Vector2I cells = AtlasCells;
        Image atlas = Image.CreateEmpty(cells.X * CellPixels, cells.Y * CellPixels, false, Image.Format.Rgba8);
        foreach (ActorKind kind in s_kinds)
        {
            if (Drawing(kind) is { } cell)
            {
                Vector2I at = Cell(kind) * CellPixels;
                atlas.BlitRect(cell, new Rect2I(0, 0, CellPixels, CellPixels), at);
            }
        }

        atlas.GenerateMipmaps();
        return ImageTexture.CreateFromImage(atlas);
    }

    /// <summary>One cell: the drawing in white at <see cref="CellPixels"/>, with a black halo grown around it.</summary>
    private static Image? Drawing(ActorKind kind)
    {
        string? body = Icons.Body(IconName(kind)) ?? Icons.Body("type-actor");
        if (body is null)
        {
            return null;
        }

        const string white = "#ffffff";
        string svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{CellPixels}\" height=\"{CellPixels}\" viewBox=\"0 0 16 16\" fill=\"none\" " +
            $"stroke=\"{white}\" stroke-width=\"1.4\" stroke-linecap=\"round\" stroke-linejoin=\"round\">" +
            body.Replace("\"C\"", "\"" + white + "\"", StringComparison.Ordinal) + "</svg>";
        var image = new Image();
        if (image.LoadSvgFromString(svg) != Error.Ok || image.GetWidth() != CellPixels)
        {
            return null;
        }

        image.Convert(Image.Format.Rgba8);
        byte[] src = image.GetData();
        var dst = new byte[src.Length];
        const int n = CellPixels;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int glyph = src[(((y * n) + x) * 4) + 3];
                int halo = glyph;
                for (int dy = -HaloPixels; dy <= HaloPixels && halo < 255; dy++)
                {
                    for (int dx = -HaloPixels; dx <= HaloPixels; dx++)
                    {
                        int sx = x + dx, sy = y + dy;
                        if ((dx * dx) + (dy * dy) <= HaloPixels * HaloPixels && sx >= 0 && sy >= 0 && sx < n && sy < n)
                        {
                            halo = Math.Max(halo, src[(((sy * n) + sx) * 4) + 3]);
                        }
                    }
                }

                // White where the drawing is, black in its halo; transparent pixels stay black so mipmaps darken the edge.
                int i = ((y * n) + x) * 4;
                dst[i] = dst[i + 1] = dst[i + 2] = (byte)(halo == 0 ? 0 : glyph * 255 / halo);
                dst[i + 3] = (byte)halo;
            }
        }

        return Image.CreateFromData(n, n, false, Image.Format.Rgba8, dst);
    }
}
