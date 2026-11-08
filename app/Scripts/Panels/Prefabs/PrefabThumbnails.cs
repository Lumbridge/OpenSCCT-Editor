using Godot;
using System.Globalization;
using System.Text;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Doors;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Windows;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Prefabs;
using OpenScctEditor.Assets;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Panels;

/// <summary>Shaded, fitted previews of actual prefab geometry, meshes and traversal pieces.</summary>
public sealed class PrefabThumbnails
{
    public const int Size = 112;
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
        string key = $"{mapFolder}:{EditorServices.ContentRoot}:{def.Source}:{def.Name}:{def.Hash}";
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
                EditorLifetime.TryEngine(() => drawn = Draw(expanded, job.Folder));
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

    private sealed record Face(Vec3[] Points, Color Colour, bool Edge);
    private static readonly Vec3 Eye = new(1, 1, .8);
    private static Vector2 Project(Vec3 v) => new((float)((v.Y - v.X) * .75), (float)((v.X + v.Y) * .3 - v.Z * .75));

    private static (Image? Picture, string Glyph) Draw(MapProject? p, string? folder)
    {
        if (p is null) return (null, Unknown);
        var faces = new List<Face>(); var lines = new List<(Vec3 A, Vec3 B)>();
        var resolver = AssetResolver.For(EditorServices.ContentRoot, folder);
        var colours = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        Color Finish(string? name)
        {
            if (name is null) return new Color(.5f, .62f, .7f);
            if (colours.TryGetValue(name, out var cached)) return cached;
            if (name.StartsWith("Editor.Door", StringComparison.Ordinal))
            { var c = DoorAssets.Colour(name); return new Color(c.R / 255f, c.G / 255f, c.B / 255f); }
            Color colour = new(.52f, .64f, .72f);
            if (resolver.FindMaterial(name) is { Texture: { } texture } material)
            {
                string path = Path.Combine(material.RenderDir, texture);
                if (File.Exists(path))
                {
                    using var image = Image.LoadFromFile(path);
                    if (image is not null && !image.IsEmpty())
                    {
                        colour = new Color(0, 0, 0, 1);
                        for (int y = 1; y <= 4; y++) for (int x = 1; x <= 4; x++)
                        { var sample = image.GetPixel(x * (image.GetWidth()-1) / 5, y * (image.GetHeight()-1) / 5); colour.R += sample.R / 16; colour.G += sample.G / 16; colour.B += sample.B / 16; }
                    }
                }
            }
            return colours[name] = colour;
        }
        void FaceOf(IEnumerable<Vec3> points, Color colour, bool edge, bool cavity = false, bool twoSided = false)
        {
            Vec3[] v = points.ToArray(); if (v.Length < 3) return;
            Vec3 n = OpenScct.Editor.Core.Geometry.Poly.Normal(v);
            if (cavity) n = -n;
            if (!twoSided && Vec3.Dot(n, Eye) <= 1e-6) return;
            double light = .65 + .3 * Math.Max(0, Vec3.Dot(n.Normalized(), new Vec3(.3, .5, 1).Normalized()));
            faces.Add(new(v, new Color(colour.R * (float)light, colour.G * (float)light, colour.B * (float)light, 1), edge));
        }
        foreach (var b in GeGeometry.ExpandValid(p).Brushes)
        foreach (var f in b.WorldFaces())
            FaceOf(f.Vertices, b.Oper == CsgOper.Subtract ? new Color(.35f, .51f, .6f) : Finish(f.Material), true, b.Oper == CsgOper.Subtract);
        foreach (var a in p.Actors)
        {
            if (a.Door is { } door)
            {
                foreach (var part in DoorGeometry.Leaf(door)) foreach (var f in part.Shape.Faces) FaceOf(f.Vertices.Select(a.LocalToWorld), Finish(part.Material), true);
                foreach (var b in DoorGeometry.Parts(a).Where(b => b.Oper == CsgOper.Add)) foreach (var f in b.WorldFaces()) FaceOf(f.Vertices, Finish(door.FrameMaterial), true);
                continue;
            }
            if (a.Window is { } window)
            {
                foreach (var f in WindowGeometry.Pane(window).Faces) FaceOf(f.Vertices.Select(a.LocalToWorld), new Color(.4f, .72f, .82f), true);
                foreach (var b in WindowGeometry.Parts(a).Where(b => b.Oper == CsgOper.Add)) foreach (var f in b.WorldFaces()) FaceOf(f.Vertices, Finish(f.Material), true);
                continue;
            }
            if (EditorServices.Classes?.IsA(a.Class, "Engine.Triggers") == true) continue;
            if (a.Shape is { } shape)
            { foreach (var f in shape.Faces) FaceOf(f.Vertices.Select(a.LocalToWorld), Finish(f.Material), true); continue; }
            string? asset = PropValues.AssetPath(a.Props["StaticMesh"] ?? EditorServices.Classes?.Default(a.Class, "StaticMesh"));
            if (asset is not null && resolver.FindViewMesh(asset) is { } mesh)
            {
                foreach (var section in MeshSections.Read(mesh))
                {
                    Vec3 Point(int index) => a.LocalToWorld(new Vec3(section.Positions[index*3]*100, section.Positions[index*3+2]*100, section.Positions[index*3+1]*100));
                    for (int i = 0; i+2 < section.Indices.Length && faces.Count < 16000; i+=3)
                        FaceOf(new[] { Point(section.Indices[i]), Point(section.Indices[i+2]), Point(section.Indices[i+1]) }, Finish(section.Material), false, twoSided: section.DoubleSided);
                }
            }
        }
        foreach (var g in p.Ges) for (int i=0; i+1 < Math.Min(g.Points.Count, 2); i++) lines.Add((g.Points[i], g.Points[i+1]));
        var vertices = faces.SelectMany(f => f.Points).Concat(lines.SelectMany(l => new[] { l.A, l.B })).ToList();
        if (vertices.Count == 0)
            return (null, p.Actors.GroupBy(a => PanelIcons.TypeIconName(a.Class)).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? Unknown);
        var points = vertices.Select(Project).ToArray(); var min = points.Aggregate((a,b) => a.Min(b)); var max = points.Aggregate((a,b) => a.Max(b));
        var extent = (max-min).Max(Vector2.One); float scale = (Size-24) / Math.Max(extent.X, extent.Y);
        Vector2 offset = new Vector2(Size/2f, Size/2f-3) - (min+max)/2*scale;
        string XY(Vec3 v) { var q = Project(v)*scale+offset; return string.Create(CultureInfo.InvariantCulture, $"{q.X:0.##},{q.Y:0.##}"); }
        var svg = new StringBuilder($"<svg xmlns='http://www.w3.org/2000/svg' width='{Size}' height='{Size}'><defs><linearGradient id='bg' x2='0' y2='1'><stop stop-color='#252e39'/><stop offset='1' stop-color='#151b24'/></linearGradient></defs><rect width='{Size}' height='{Size}' rx='8' fill='url(#bg)'/><ellipse cx='{Size/2}' cy='{Size-14}' rx='{Size*.34}' ry='5' fill='#0e131b' opacity='.6'/>");
        foreach (var f in faces.OrderBy(f => f.Points.Average(v => Vec3.Dot(v, Eye))))
            svg.Append($"<polygon points='{string.Join(' ', f.Points.Select(XY))}' fill='#{f.Colour.ToHtml(false)}' stroke='{(f.Edge ? "#c2d3db" : "none")}' stroke-opacity='.28' stroke-width='.55' stroke-linejoin='round'/>");
        foreach (var l in lines) svg.Append($"<polyline points='{XY(l.A)} {XY(l.B)}' stroke='#6ee0d3' stroke-width='1.5' fill='none'/>");
        svg.Append("</svg>"); var picture = new Image(); picture.LoadSvgFromString(svg.ToString()); return (picture, Unknown);
    }
}
