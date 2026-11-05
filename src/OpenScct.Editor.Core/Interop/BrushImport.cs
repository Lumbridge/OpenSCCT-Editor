using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop;

/// <summary>How a stored Brush actor (T3D text or a binary .sdc) becomes a CSG <see cref="Brush"/>: the rules the
/// T3D and .sdc importers share.</summary>
public static class BrushImport
{
    /// <summary>
    /// A CSG brush from a Brush actor's typed properties: CsgOper, PolyFlags, the transform and the scales move into
    /// the brush's fields, every other property stays in <see cref="Brush.Props"/>. Null (with a finding) for a brush
    /// that is neither add nor subtract (the builder brush is CSG_Active).
    /// </summary>
    public static Brush? ToBrush(string id, string cls, PropSet props, BrushShape? shape, List<Finding> findings)
    {
        SvJsonNode? csg = props["CsgOper"];
        props.Remove("CsgOper");
        int oper = OperOf(csg);
        if (oper is not (1 or 2))
        {
            findings.Add(Finding.Warning("import.brushNotCsg",
                $"brush {id} has CsgOper {(csg is null ? "CSG_Active" : csg.ToString())}: it adds nothing to the level, skipped", ObjectRef.Brush(id)));
            return null;
        }

        var b = new Brush(id) { Oper = oper == 1 ? CsgOper.Add : CsgOper.Subtract, PolyFlags = props.GetInt("PolyFlags") };
        props.Remove("PolyFlags");
        var transform = new Actor(id, cls);
        InteropValues.TakeTransform(props, transform);
        b.Location = transform.Location;
        b.Rotation = transform.Rotation;
        b.PrePivot = transform.PrePivot;
        b.MainScale = TakeScale(props, "MainScale");
        b.PostScale = TakeScale(props, "PostScale");
        if (shape is not null)
        {
            b.Shape = shape;
        }

        b.Props = props;
        if (b.Shape.Faces.Count == 0)
        {
            findings.Add(Finding.Warning("import.emptyBrush", $"brush {id} has no polygons", ObjectRef.Brush(id)));
        }

        return b;
    }

    /// <summary>The scale part of a brush FScale; a sheer (SheerRate != 0) stays in the props for the export.</summary>
    public static Vec3 TakeScale(PropSet props, string key)
    {
        if (props[key] is not SvJsonObject o)
        {
            return Vec3.One;
        }

        props.Remove(key);
        Vec3 scale = o["Scale"] is SvJsonObject s ? InteropValues.VectorOr(s, Vec3.One) : Vec3.One;
        if (o["SheerRate"] is SvJsonNumber rate && PropValues.Num(rate) != 0)
        {
            var sheer = new SvJsonObject();
            sheer.Add("SheerAxis", o["SheerAxis"] ?? SvJsonNode.Integer(0));
            sheer.Add("SheerRate", rate);
            props.Set(key, sheer);
        }

        return scale;
    }

    /// <summary>The ECsgOper index of a CsgOper value (1 add, 2 subtract; 0 CSG_Active).</summary>
    public static int OperOf(SvJsonNode? node) => node switch
    {
        SvJsonNumber n when n.TryGetInt32(out int v) => v,
        SvJsonString s => s.Value.ToUpperInvariant() switch
        {
            "CSG_ADD" => 1,
            "CSG_SUBTRACT" => 2,
            _ => 0,
        },
        _ => 0,
    };
}
