using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Validation;

namespace OpenScct.Editor.Core.Geometry.Builders;

/// <summary>One brush a builder made: its shape, CSG operation and offset from the piece origin.</summary>
public readonly record struct BuiltPart(BrushShape Shape, CsgOper Oper, Vec3 Offset);

/// <summary>
/// One kind of parametric brush (a UE2 brush builder such as the cylinder, or a blockout piece such as a room).
/// A kind declares its parameters and turns a spec into brush parts. Every part carries the complete normalised
/// spec, so the piece regenerates from its parameters instead of decomposing into loose polygons.
/// </summary>
public abstract class BuilderKind
{
    /// <summary>The material every built face gets (also a parameter of every kind).</summary>
    public static readonly BuilderParam MaterialParam = BuilderParam.Text("material", Starter.DefaultMaterial, "Material of every face");

    private IReadOnlyList<BuilderParam>? _params;

    /// <summary>The spec kind (<c>cube</c>, <c>linearStair</c>, <c>room</c>...).</summary>
    public abstract string Kind { get; }

    public abstract string Summary { get; }

    /// <summary>Blockout pieces choose each part's CSG operation (carve or shell). Plain builders return
    /// <see cref="CsgOper.Add"/> parts that take the operation the user places them with.</summary>
    public virtual bool IsBlockout => false;

    /// <summary>The CSG operation a plain builder's brush gets when the user names none: subtract (UE2 maps start
    /// solid), add for sheets, which only mean something added.</summary>
    public virtual CsgOper DefaultOper => CsgOper.Subtract;

    /// <summary>Where the piece's origin is, for the UI and scripts.</summary>
    public abstract string Origin { get; }

    public IReadOnlyList<BuilderParam> Params => _params ??= [.. DeclareParams(), MaterialParam];

    /// <summary>The complete default spec.</summary>
    public BuilderSpec Defaults() => BuilderArgs.From(this, new BuilderSpec(Kind)).Spec;

    /// <summary>The complete default spec for a project's body (<see cref="DesignNumbers.Of"/>).</summary>
    public BuilderSpec Defaults(DesignNumbers body) => BuilderArgs.From(this, WithBodyDefaults(new BuilderSpec(Kind), body)).Spec;

    /// <summary>Parameter defaults that follow the body: a vent's height from the spy crouch gap, a doorway's from the
    /// standing clearance. With the default body they equal the declared defaults. Empty for most kinds.</summary>
    public virtual IEnumerable<(string Name, double Value)> BodyDefaults(DesignNumbers body) => [];

    /// <summary>A copy of <paramref name="spec"/> with the body-following parameters it leaves out filled in.</summary>
    public BuilderSpec WithBodyDefaults(BuilderSpec spec, DesignNumbers body)
    {
        BuilderSpec filled = spec.Clone();
        foreach ((string name, double value) in BodyDefaults(body))
        {
            if (!filled.Numbers.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
            {
                filled.Numbers[name] = value;
            }
        }

        return filled;
    }

    /// <summary>Builds the parts of a spec, in CSG order. Parts share the piece origin (offset zero).</summary>
    public IReadOnlyList<BuiltPart> Build(BuilderSpec spec)
    {
        BuilderArgs args = BuilderArgs.From(this, spec);
        List<Part> parts = Make(args).Where(p => p.Polygons.Count > 0).ToList();
        if (parts.Count == 0)
        {
            throw new ArgumentException($"{Kind}: these parameters make no geometry");
        }

        var built = new List<BuiltPart>(parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            Part part = parts[i];
            BuilderSpec stamp = args.Spec.Clone();
            if (parts.Count > 1)
            {
                stamp.Numbers[BuilderArgs.PartKey] = i;
            }

            var shape = new BrushShape { Builder = stamp };
            foreach (List<Vec3> polygon in part.Polygons)
            {
                BrushFace face = FaceKit.Make(args.Material, polygon.Select(Poly.Tidy));
                face.Flags = part.Flags;
                shape.Faces.Add(face);
            }

            built.Add(new BuiltPart(shape, part.Oper, Vec3.Zero));
        }

        return built;
    }

    protected abstract IEnumerable<BuilderParam> DeclareParams();

    protected abstract IEnumerable<Part> Make(BuilderArgs a);

    /// <summary>A part under construction.</summary>
    protected readonly record struct Part(List<List<Vec3>> Polygons, CsgOper Oper = CsgOper.Add, int Flags = 0);

    protected static Part Solid(List<List<Vec3>> polygons, CsgOper oper = CsgOper.Add) => new(polygons, oper);

    protected static Part BoxPart(Vec3 min, Vec3 max, CsgOper oper = CsgOper.Add) =>
        new(max.X > min.X && max.Y > min.Y && max.Z > min.Z ? ConvexSolid.Box(min, max) : [], oper);
}
