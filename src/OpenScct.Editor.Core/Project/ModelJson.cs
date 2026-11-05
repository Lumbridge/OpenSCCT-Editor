using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Project;

/// <summary>
/// The JSON form of the model, shared by the project files, change documents and API queries. Compact on purpose:
/// vectors are <c>[x, y, z]</c>, rotators <c>[pitch, yaw, roll]</c>, and members at their default value are left out.
/// </summary>
public static class ModelJson
{
    // ---- writers ---------------------------------------------------------------------------------------------

    public static SvJsonNode Vec(Vec3 v) =>
        new SvJsonArray([SvJsonNode.Float((float)v.X), SvJsonNode.Float((float)v.Y), SvJsonNode.Float((float)v.Z)]);

    public static SvJsonNode RotJ(Rot r) =>
        new SvJsonArray([SvJsonNode.Integer(r.Pitch), SvJsonNode.Integer(r.Yaw), SvJsonNode.Integer(r.Roll)]);

    public static SvJsonObject ActorToJson(Actor a, bool includeEditor = true)
    {
        var o = new SvJsonObject();
        o.Add("id", SvJsonNode.String(a.Id));
        o.Add("class", SvJsonNode.String(a.Class));
        o.Add("loc", Vec(a.Location));
        if (a.Rotation != Rot.Zero)
        {
            o.Add("rot", RotJ(a.Rotation));
        }

        if (a.DrawScale != 1)
        {
            o.Add("scale", SvJsonNode.Float((float)a.DrawScale));
        }

        if (a.DrawScale3D != Vec3.One)
        {
            o.Add("scale3d", Vec(a.DrawScale3D));
        }

        if (a.PrePivot != Vec3.Zero)
        {
            o.Add("prePivot", Vec(a.PrePivot));
        }

        if (a.Props.Count > 0)
        {
            o.Add("props", a.Props.ToJson());
        }

        if (a.Shape is not null)
        {
            o.Add("shape", ShapeToJson(a.Shape));
        }

        if (a.Match is { IsEmpty: false } match)
        {
            o.Add("match", MatchJson.ToJson(match));
        }

        if (a.Legs is { } legs)
        {
            o.Add("legs", MoverLegsJson.ToJson(legs));
        }

        if (includeEditor && !a.Editor.IsDefault)
        {
            o.Add("editor", MetaToJson(a.Editor));
        }

        if (a.Source is not null)
        {
            o.Add("src", SvJsonNode.String(a.Source));
        }

        return o;
    }

    public static SvJsonObject BrushToJson(Brush b, bool includeEditor = true)
    {
        var o = new SvJsonObject();
        o.Add("id", SvJsonNode.String(b.Id));
        o.Add("csg", SvJsonNode.String(b.Oper == CsgOper.Add ? "add" : "subtract"));
        if (b.PolyFlags != 0)
        {
            o.Add("flags", SvJsonNode.Integer(b.PolyFlags));
        }

        o.Add("loc", Vec(b.Location));
        if (b.Rotation != Rot.Zero)
        {
            o.Add("rot", RotJ(b.Rotation));
        }

        if (b.PrePivot != Vec3.Zero)
        {
            o.Add("prePivot", Vec(b.PrePivot));
        }

        if (b.MainScale != Vec3.One)
        {
            o.Add("mainScale", Vec(b.MainScale));
        }

        if (b.PostScale != Vec3.One)
        {
            o.Add("postScale", Vec(b.PostScale));
        }

        o.Add("shape", ShapeToJson(b.Shape));
        if (b.Props.Count > 0)
        {
            o.Add("props", b.Props.ToJson());
        }

        if (includeEditor && !b.Editor.IsDefault)
        {
            o.Add("editor", MetaToJson(b.Editor));
        }

        if (b.Source is not null)
        {
            o.Add("src", SvJsonNode.String(b.Source));
        }

        return o;
    }

    public static SvJsonObject ShapeToJson(BrushShape s)
    {
        var o = new SvJsonObject();
        if (s.Builder is not null)
        {
            o.Add("builder", BuilderToJson(s.Builder));
        }

        o.Add("faces", new SvJsonArray(s.Faces.Select(FaceToJson)));
        return o;
    }

    public static SvJsonObject BuilderToJson(BuilderSpec b)
    {
        var o = new SvJsonObject();
        o.Add("kind", SvJsonNode.String(b.Kind));
        foreach (KeyValuePair<string, double> kv in b.Numbers)
        {
            o.TryAdd(kv.Key, SvJsonNode.Float((float)kv.Value));
        }

        foreach (KeyValuePair<string, string> kv in b.Strings)
        {
            o.TryAdd(kv.Key, SvJsonNode.String(kv.Value));
        }

        return o;
    }

    public static SvJsonObject FaceToJson(BrushFace f)
    {
        var o = new SvJsonObject();
        o.Add("verts", new SvJsonArray(f.Vertices.Select(Vec)));
        if (f.Material is not null)
        {
            o.Add("mat", SvJsonNode.String(f.Material));
        }

        if (f.Flags != 0)
        {
            o.Add("flags", SvJsonNode.Integer(f.Flags));
        }

        if (f.TextureU != Vec3.Zero || f.TextureV != Vec3.Zero)
        {
            o.Add("texBase", Vec(f.TextureBase));
            o.Add("texU", Vec(f.TextureU));
            o.Add("texV", Vec(f.TextureV));
        }

        if (f.PanU != 0 || f.PanV != 0)
        {
            o.Add("pan", new SvJsonArray([SvJsonNode.Integer(f.PanU), SvJsonNode.Integer(f.PanV)]));
        }

        if (f.LightMapScale != 32)
        {
            o.Add("lmScale", SvJsonNode.Float((float)f.LightMapScale));
        }

        if (f.LightMapScalePsx2 is double psx2)
        {
            o.Add("lmScalePsx2", SvJsonNode.Float((float)psx2));
        }

        if (f.ItemName is not null)
        {
            o.Add("item", SvJsonNode.String(f.ItemName));
        }

        if (f.Link >= 0)
        {
            o.Add("link", SvJsonNode.Integer(f.Link));
        }

        return o;
    }

    public static SvJsonObject GeToJson(Ge g, bool includeEditor = true)
    {
        var o = new SvJsonObject();
        o.Add("id", SvJsonNode.String(g.Id));
        o.Add("kind", SvJsonNode.String(g.Kind));
        o.Add("points", new SvJsonArray(g.Points.Select(Vec)));
        if (g.Mask != 0)
        {
            o.Add("mask", SvJsonNode.Integer(g.Mask));
        }

        if (g.Platform != 0)
        {
            o.Add("platform", SvJsonNode.Integer(g.Platform));
        }

        if (g.Flags != 0)
        {
            o.Add("flags", SvJsonNode.Integer(g.Flags));
        }

        if (includeEditor && !g.Editor.IsDefault)
        {
            o.Add("editor", MetaToJson(g.Editor));
        }

        if (g.Source is not null)
        {
            o.Add("src", SvJsonNode.String(g.Source));
        }

        return o;
    }

    public static SvJsonObject MetaToJson(EditorMeta m)
    {
        var o = new SvJsonObject();
        if (m.Layer.Length > 0)
        {
            o.Add("layer", SvJsonNode.String(m.Layer));
        }

        if (m.Folder.Length > 0)
        {
            o.Add("folder", SvJsonNode.String(m.Folder));
        }

        if (m.Hidden)
        {
            o.Add("hidden", SvJsonNode.True);
        }

        if (m.Locked)
        {
            o.Add("locked", SvJsonNode.True);
        }

        if (m.PrefabInstance is not null)
        {
            o.Add("prefab", SvJsonNode.String(m.PrefabInstance));
        }

        if (m.PrefabPart is not null)
        {
            o.Add("prefabPart", SvJsonNode.String(m.PrefabPart));
        }

        if (m.Note is not null)
        {
            o.Add("note", SvJsonNode.String(m.Note));
        }

        if (m.Author is not null)
        {
            o.Add("author", SvJsonNode.String(m.Author));
        }

        if (m.Op is not null)
        {
            o.Add("op", SvJsonNode.String(m.Op));
        }

        if (m.MoverKey is int key)
        {
            o.Add("moverKey", SvJsonNode.Integer(key));
        }

        return o;
    }

    // ---- readers ---------------------------------------------------------------------------------------------

    public static Vec3 ReadVec(SvJsonNode? node, Vec3 fallback = default)
    {
        if (node is SvJsonArray { Count: 3 } a)
        {
            return new Vec3(Num(a[0]), Num(a[1]), Num(a[2]));
        }

        return PropValues.TryVector(node, out Vec3 v) ? v : fallback;
    }

    public static Rot ReadRot(SvJsonNode? node)
    {
        if (node is SvJsonArray { Count: 3 } a)
        {
            return new Rot((int)Num(a[0]), (int)Num(a[1]), (int)Num(a[2]));
        }

        return PropValues.TryRotator(node, out Rot r) ? r : Rot.Zero;
    }

    public static Actor ActorFromJson(SvJsonObject o)
    {
        var a = new Actor(Str(o, "id") ?? "", Str(o, "class") ?? "Engine.Actor")
        {
            Location = ReadVec(o["loc"]),
            Rotation = ReadRot(o["rot"]),
            DrawScale = o["scale"] is null ? 1 : Num(o["scale"]),
            DrawScale3D = ReadVec(o["scale3d"], Vec3.One),
            PrePivot = ReadVec(o["prePivot"]),
            Props = PropSet.FromJson(o["props"]),
            Shape = o["shape"] is SvJsonObject s ? ShapeFromJson(s) : null,
            Match = MatchJson.FromJson(o["match"]),
            Legs = MoverLegsJson.FromJson(o["legs"]),
            Editor = MetaFromJson(o["editor"]),
            Source = Str(o, "src"),
        };
        return a;
    }

    public static Brush BrushFromJson(SvJsonObject o) => new(Str(o, "id") ?? "")
    {
        Oper = string.Equals(Str(o, "csg"), "add", StringComparison.OrdinalIgnoreCase) ? CsgOper.Add : CsgOper.Subtract,
        PolyFlags = (int)Num(o["flags"]),
        Location = ReadVec(o["loc"]),
        Rotation = ReadRot(o["rot"]),
        PrePivot = ReadVec(o["prePivot"]),
        MainScale = ReadVec(o["mainScale"], Vec3.One),
        PostScale = ReadVec(o["postScale"], Vec3.One),
        Shape = o["shape"] is SvJsonObject s ? ShapeFromJson(s) : new BrushShape(),
        Props = PropSet.FromJson(o["props"]),
        Editor = MetaFromJson(o["editor"]),
        Source = Str(o, "src"),
    };

    public static BrushShape ShapeFromJson(SvJsonObject o)
    {
        var s = new BrushShape { Builder = o["builder"] is SvJsonObject b ? BuilderFromJson(b) : null };
        if (o["faces"] is SvJsonArray faces)
        {
            foreach (SvJsonNode f in faces)
            {
                if (f is SvJsonObject fo)
                {
                    s.Faces.Add(FaceFromJson(fo));
                }
            }
        }

        return s;
    }

    public static BuilderSpec BuilderFromJson(SvJsonObject o)
    {
        var b = new BuilderSpec(Str(o, "kind") ?? "cube");
        foreach (KeyValuePair<string, SvJsonNode> kv in o)
        {
            if (kv.Key == "kind")
            {
                continue;
            }

            if (kv.Value is SvJsonNumber)
            {
                b.Numbers[kv.Key] = Num(kv.Value);
            }
            else if (kv.Value is SvJsonString str)
            {
                b.Strings[kv.Key] = str.Value;
            }
        }

        return b;
    }

    public static BrushFace FaceFromJson(SvJsonObject o)
    {
        var f = new BrushFace
        {
            Material = Str(o, "mat"),
            Flags = (int)Num(o["flags"]),
            TextureBase = ReadVec(o["texBase"]),
            TextureU = ReadVec(o["texU"]),
            TextureV = ReadVec(o["texV"]),
            LightMapScale = o["lmScale"] is null ? 32 : Num(o["lmScale"]),
            LightMapScalePsx2 = o["lmScalePsx2"] is null ? null : Num(o["lmScalePsx2"]),
            ItemName = Str(o, "item"),
            Link = o["link"] is null ? -1 : (int)Num(o["link"]),
        };
        if (o["verts"] is SvJsonArray verts)
        {
            f.Vertices.AddRange(verts.Select(v => ReadVec(v)));
        }

        if (o["pan"] is SvJsonArray { Count: 2 } pan)
        {
            f.PanU = (int)Num(pan[0]);
            f.PanV = (int)Num(pan[1]);
        }

        return f;
    }

    public static Ge GeFromJson(SvJsonObject o)
    {
        var g = new Ge(Str(o, "id") ?? "", Str(o, "kind") ?? GeKinds.LedgeGrab)
        {
            Mask = (int)Num(o["mask"]),
            Platform = (int)Num(o["platform"]),
            Flags = (int)Num(o["flags"]),
            Editor = MetaFromJson(o["editor"]),
            Source = Str(o, "src"),
        };
        if (o["points"] is SvJsonArray pts)
        {
            g.Points.AddRange(pts.Select(p => ReadVec(p)));
        }

        return g;
    }

    public static EditorMeta MetaFromJson(SvJsonNode? node)
    {
        var m = new EditorMeta();
        if (node is SvJsonObject o)
        {
            m.Layer = Str(o, "layer") ?? "";
            m.Folder = Str(o, "folder") ?? "";
            m.Hidden = o["hidden"]?.Kind == SvJsonKind.True;
            m.Locked = o["locked"]?.Kind == SvJsonKind.True;
            m.PrefabInstance = Str(o, "prefab");
            m.PrefabPart = Str(o, "prefabPart");
            m.Note = Str(o, "note");
            m.Author = Str(o, "author");
            m.Op = Str(o, "op");
            m.MoverKey = o["moverKey"] is SvJsonNumber key ? (int)Num(key) : null;
        }

        return m;
    }

    public static string? Str(SvJsonObject o, string key) => o[key] is SvJsonString s ? s.Value : null;

    public static double Num(SvJsonNode? node) => PropValues.Num(node);

    public static string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
