using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Validation;

namespace OpenScctEditor.Tools;

/// <summary>
/// The builder brush (UnrealEd's red brush): a builder spec, a location and a rotation. Add and Subtract copy it into
/// the map as a real brush that keeps the spec, so the piece stays parametric. Shapes come from the
/// <see cref="IShapeBuilder"/> services; the cube also works without one.
/// </summary>
public sealed class BuilderBrush
{
    /// <summary>Hand-made shapes (a copied brush with no builder spec, or the pieces Intersect/Deintersect left),
    /// used as-is until the kind or a parameter changes.</summary>
    private List<BrushShape>? _custom;

    public BuilderBrush()
    {
        Spec = DefaultSpec("cube");
    }

    public BuilderSpec Spec { get; private set; }

    /// <summary>The open map's body numbers: a new vent, doorway or stair takes its body-following sizes from them.</summary>
    public Func<DesignNumbers>? Body { get; set; }

    public Vec3 Location { get; set; } = new(0, 0, 128);

    public Rot Rotation { get; set; }

    /// <summary>Raised when the spec, location or rotation changes.</summary>
    public event Action? Changed;

    /// <summary>Raised when the parameters were replaced from outside the parameter fields (new kind, copied
    /// brush, box drawn), so the fields show the new values.</summary>
    public event Action? Reshaped;

    /// <summary>Builder kinds on offer: every shape service's kinds, and the cube.</summary>
    public static IReadOnlyList<string> Kinds() =>
        EditorServices.ShapeBuilders.SelectMany(b => b.Kinds).Append("cube").Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A builder kind as menus and the options row show it, in Title Case: linearStair becomes "Linear Stair".</summary>
    public static string KindTitle(string kind) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Panels.PanelUi.Words(kind));

    public static BuilderSpec DefaultSpec(string kind, DesignNumbers? body = null)
    {
        if (body is not null && BuilderCatalog.Find(kind) is { } known)
        {
            return known.Defaults(body);
        }

        if (EditorServices.ShapeBuilder(kind) is { } builder)
        {
            return builder.Defaults(kind);
        }

        var cube = new BuilderSpec("cube");
        cube.Numbers["breadth"] = cube.Numbers["width"] = cube.Numbers["height"] = 256;
        return cube;
    }

    public void SetKind(string kind)
    {
        Spec = DefaultSpec(kind, Body?.Invoke());
        _custom = null;
        Changed?.Invoke();
        Reshaped?.Invoke();
    }

    public void SetNumber(string key, double value)
    {
        Spec.Numbers[key] = value;
        _custom = null;
        Changed?.Invoke();
    }

    /// <summary>Takes the spec of an existing brush (Polygons > To Brush), or a copy of its shape when it has none.
    /// A brush with a pivot or scale is copied as its exact world shape (the builder has neither).</summary>
    public void CopyFrom(Brush b)
    {
        bool plain = b.PrePivot == Vec3.Zero && b.MainScale == Vec3.One && b.PostScale == Vec3.One;
        Spec = b.Shape.Builder?.Clone() ?? DefaultSpec("cube");
        Location = b.Location;
        if (plain)
        {
            _custom = b.Shape.Builder is null ? [b.Shape.Clone()] : null;
            Rotation = b.Rotation;
        }
        else
        {
            BrushShape baked = b.Shape.Clone();
            baked.Builder = null;
            baked.Faces = b.WorldFaces().Select(f =>
            {
                f.Vertices = f.Vertices.Select(v => v - b.Location).ToList();
                f.TextureBase -= b.Location;
                return f;
            }).ToList();
            _custom = [baked];
            Rotation = Rot.Zero;
        }

        Changed?.Invoke();
        Reshaped?.Invoke();
    }

    public void Moved() => Changed?.Invoke();

    /// <summary>True when the builder holds hand-made pieces rather than a parametric spec.</summary>
    public bool IsCustom => _custom is not null;

    /// <summary>Why the last <see cref="Pieces"/> made no shape (parameters the builder rejects), or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Replaces the shape with hand-made pieces in the builder's frame (Intersect, Deintersect).</summary>
    public void SetPieces(IReadOnlyList<BrushShape> pieces)
    {
        _custom = pieces.Select(p => p.Clone()).ToList();
        Changed?.Invoke();
        Reshaped?.Invoke();
    }

    /// <summary>Each piece as a brush placed in the world (for clipping against the map).</summary>
    public IEnumerable<Brush> WorldPieces() =>
        Pieces().Select(p => new Brush("builder")
        {
            Shape = p.Shape,
            Oper = p.Oper,
            Location = Location + Rotation.Rotate(p.Offset),
            Rotation = Rotation,
        });

    /// <summary>Sets several size parameters at once (a box drawn in a 2D view) and moves the brush.</summary>
    public void Resize(IReadOnlyDictionary<string, double> numbers, Vec3 location)
    {
        // Only the builder's own parameters: a box drag on a cylinder must not add cube sizes it would reject.
        foreach ((string key, double value) in numbers)
        {
            if (Spec.Numbers.ContainsKey(key))
            {
                Spec.Numbers[key] = value;
            }
        }

        _custom = null;
        Location = location;
        Rotation = Rot.Zero;
        Changed?.Invoke();
        Reshaped?.Invoke();
    }

    /// <summary>The pieces the builder makes now, in CSG order, each with its own operation and offset.</summary>
    public IReadOnlyList<(BrushShape Shape, CsgOper Oper, Vec3 Offset)> Pieces()
    {
        Problem = null;
        if (_custom is not null)
        {
            return _custom.Select(s => (s.Clone(), CsgOper.Add, Vec3.Zero)).ToList();
        }

        if (EditorServices.ShapeBuilder(Spec.Kind) is { } builder)
        {
            try
            {
                return builder.Build(Spec);
            }
            catch (ArgumentException ex)
            {
                // Parameters that make no shape (often half-typed): nothing to show or place until they do.
                Problem = ex.Message;
                return [];
            }
        }

        var size = new Vec3(Spec.Get("breadth", 256), Spec.Get("width", 256), Spec.Get("height", 256));
        BrushShape shape = Box.Shape(size);
        shape.Builder = Spec.Clone();
        return [(shape, CsgOper.Add, Vec3.Zero)];
    }

    /// <summary>All pieces merged into one shape, for drawing.</summary>
    public BrushShape DisplayShape()
    {
        var shape = new BrushShape();
        foreach ((BrushShape s, CsgOper _, Vec3 offset) in Pieces())
        {
            foreach (BrushFace f in s.Faces)
            {
                BrushFace c = f.Clone();
                c.Vertices = f.Vertices.Select(v => v + offset).ToList();
                shape.Faces.Add(c);
            }
        }

        return shape;
    }

    /// <summary>The brushes Add or Subtract makes: one per piece. Hand-made pieces and single-piece specs take
    /// <paramref name="oper"/>; multi-piece specs (blockout pieces) keep each piece's own operation.</summary>
    public List<Brush> MakeBrushes(MapProject project, CsgOper oper, int polyFlags)
    {
        var pieces = Pieces();
        bool ownOpers = _custom is null && pieces.Count > 1;
        var ids = new FreshIds(project);
        var result = new List<Brush>();
        foreach ((BrushShape shape, CsgOper pieceOper, Vec3 offset) in pieces)
        {
            result.Add(new Brush(ids.Next("Brush"))
            {
                Shape = shape.Clone(),
                Oper = ownOpers ? pieceOper : oper,
                PolyFlags = polyFlags,
                Location = Location + Rotation.Rotate(offset),
                Rotation = Rotation,
            });
        }

        return result;
    }
}
