using Godot;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Prefabs;
using OpenScctEditor.Assets;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Panels;

/// <summary>
/// Small pictures of prefabs for the Prefabs panel: the prefab expanded with its default parameters
/// (<see cref="PrefabPreview"/>) drawn as a wireframe from above at an angle, brushes in their CSG colours and actors
/// as dots, on a tile. A prefab of actors only shows the glyph of its kind of actor instead (dots alone read as an empty
/// picture). Drawn one at a time off the main thread and kept per prefab version (its hash), so a changed prefab gets a
/// new picture.
/// </summary>
public sealed class PrefabThumbnails
{
    public const int Size = 96;
    private static readonly Color s_actor = new(0.95f, 0.95f, 0.95f);
    private static readonly Color s_ge = new(0.4f, 0.9f, 0.9f);
    private readonly Dictionary<string, Texture2D?> _done = new(StringComparer.Ordinal);
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, string Name, string? Folder)> _work = new();
    private bool _running;
    private Texture2D? _pending;

    /// <summary>Raised on the main thread when a picture is ready.</summary>
    public event Action? Ready;

    /// <summary>An empty tile, shown while a picture is drawn.</summary>
    public Texture2D Pending => _pending ??= ImageTexture.CreateFromImage(PictureTile.Make(Size, EditorTheme.Tile));

    /// <summary>The picture of a prefab, <see cref="Pending"/> while it is drawn.</summary>
    public Texture2D Get(PrefabDefinition def, string? mapFolder)
    {
        string key = $"{def.Source}:{def.Name}:{def.Hash}";
        if (_done.TryGetValue(key, out Texture2D? texture) && texture is not null)
        {
            return texture;
        }

        if (_queued.Add(key))
        {
            _work.Enqueue((key, def.Name, mapFolder));
            Next();
        }

        return Pending;
    }

    private void Next()
    {
        if (_running || !_work.TryDequeue(out var job))
        {
            return;
        }

        _running = true;
        BackgroundJobs.Start(cancel =>
        {
            (Image? Picture, string Glyph) drawn = (null, Unknown);
            try
            {
                MapProject? expanded = PrefabPreview.Expand(job.Name, job.Folder, out _);
                // The picture is an engine image: never drawn once the editor is closing.
                EditorLifetime.TryEngine(() => drawn = Draw(expanded));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
            {
                drawn = (null, Unknown);
            }

            BackgroundJobs.Post(() =>
            {
                _done[job.Key] = drawn.Picture is { } picture ? ImageTexture.CreateFromImage(picture) : Glyph(drawn.Glyph);
                _queued.Remove(job.Key);
                _running = false;
                Ready?.Invoke();
                Next();
            });
        });
    }

    /// <summary>The share of the picture left empty on each side, so thin shapes never run edge to edge.</summary>
    private const float PictureMargin = 0.15f;

    /// <summary>The glyph of a prefab that does not expand.</summary>
    private const string Unknown = "type-group";

    /// <summary>A tile with a glyph on it, for a prefab with nothing to draw as lines.</summary>
    private static Texture2D Glyph(string name)
    {
        Image tile = PictureTile.Make(Size, EditorTheme.Tile);
        if (Icons.Get(name, EditorTheme.TextDim, EditorTheme.IconSizeTile)?.GetImage() is { } icon && !icon.IsEmpty())
        {
            if (icon.IsCompressed())
            {
                icon.Decompress();
            }

            icon.Convert(Image.Format.Rgba8);
            int w = Math.Min(icon.GetWidth(), Size), h = Math.Min(icon.GetHeight(), Size);
            tile.BlendRect(icon, new Rect2I(0, 0, w, h), new Vector2I((Size - w) / 2, (Size - h) / 2));
        }

        return ImageTexture.CreateFromImage(tile);
    }

    /// <summary>Draws the parts of an expanded prefab, fitted to the picture; or, for a prefab of actors only (or one
    /// that does not expand), names the glyph to show instead.</summary>
    private static (Image? Picture, string Glyph) Draw(MapProject? p)
    {
        if (p is null)
        {
            return (null, Unknown);
        }

        // Without brushes there is nothing to read as a shape (a ladder or zip line is one thin line): the glyph of
        // its kind instead, a GE's or the kind of actor most of its actors are.
        if (p.Brushes.Count == 0)
        {
            string glyph = p.Ges.Count > 0 ? PanelIcons.GeIconName(p.Ges[0].Kind)
                : p.Actors.GroupBy(a => PanelIcons.TypeIconName(a.Class)).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? Unknown;
            return (null, glyph);
        }

        var lines = new List<(Vec3 A, Vec3 B, Color C)>();
        foreach (Brush b in p.Brushes)
        {
            Color c = SceneStyle.BrushColor(b);
            foreach (BrushFace f in b.WorldFaces())
            {
                for (int i = 0; i < f.Vertices.Count; i++)
                {
                    lines.Add((f.Vertices[i], f.Vertices[(i + 1) % f.Vertices.Count], c));
                }
            }
        }

        foreach (Ge g in p.Ges)
        {
            for (int i = 0; i + 1 < g.Points.Count; i++)
            {
                lines.Add((g.Points[i], g.Points[i + 1], s_ge));
            }
        }

        if (lines.Count == 0)
        {
            return (null, Unknown);
        }

        List<Vec3> dots = p.Actors.Select(a => a.Location).ToList();
        var points = lines.SelectMany(l => new[] { l.A, l.B }).Concat(dots).Select(Project).ToList();

        Vector2 min = points.Aggregate(points[0], (m, v) => m.Min(v));
        Vector2 max = points.Aggregate(points[0], (m, v) => m.Max(v));
        Vector2 extent = (max - min).Max(new Vector2(1, 1));
        float scale = Size * (1 - 2 * PictureMargin) / Math.Max(extent.X, extent.Y);
        Vector2 offset = new Vector2(Size, Size) / 2 - (min + max) / 2 * scale;
        Vector2 ToPixel(Vec3 v) => Project(v) * scale + offset;

        Image image = PictureTile.Make(Size, EditorTheme.Tile);
        foreach ((Vec3 a, Vec3 b, Color c) in lines)
        {
            Line(image, ToPixel(a), ToPixel(b), c);
        }

        foreach (Vec3 d in dots)
        {
            Vector2 at = ToPixel(d);
            image.FillRect(new Rect2I((int)at.X - 1, (int)at.Y - 1, 3, 3), s_actor);
        }

        return (image, Unknown);
    }

    /// <summary>A view from the front-left, above: X right and down, Y left and down, Z up.</summary>
    private static Vector2 Project(Vec3 v) =>
        new((float)((v.X - v.Y) * 0.866), (float)((v.X + v.Y) * 0.5 - v.Z));

    private static void Line(Image image, Vector2 a, Vector2 b, Color c)
    {
        int steps = (int)Math.Ceiling(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)));
        for (int i = 0; i <= steps; i++)
        {
            Vector2 p = steps == 0 ? a : a.Lerp(b, i / (float)steps);
            int x = (int)Math.Round(p.X), y = (int)Math.Round(p.Y);
            if (x >= 0 && y >= 0 && x < Size && y < Size)
            {
                image.SetPixel(x, y, c);
            }
        }
    }
}
