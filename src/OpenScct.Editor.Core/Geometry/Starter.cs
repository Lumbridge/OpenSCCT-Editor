using OpenScct.Content.Json;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry;

/// <summary>Content for new maps.</summary>
public static class Starter
{
    /// <summary>The default material of new brush faces.</summary>
    public const string DefaultMaterial = "Editor.Grid";

    /// <summary>Adds a carved 1024 x 1024 x 512 room with a spy start, a merc start and a light.</summary>
    public static void AddStarterRoom(MapProject p)
    {
        Brush room = Box.Make(p.NewId("Room"), new Vec3(1024, 1024, 512), CsgOper.Subtract);
        room.Location = new Vec3(0, 0, 256);
        p.Brushes.Add(room);

        Actor spy = new(p.NewId("PlayerStart"), "Engine.PlayerStart");
        spy.Location = new Vec3(-384, 0, ActorPlacement.HalfHeight(spy, p.Workspace.Movement.StandHeight / 2));
        spy.Props.Set("TeamNumber", SvJsonNode.Integer(0));
        p.Actors.Add(spy);

        Actor merc = new(p.NewId("PlayerStart"), "Engine.PlayerStart") { Rotation = new Rot(0, 32768, 0) };
        merc.Location = new Vec3(384, 0, ActorPlacement.HalfHeight(merc, p.Workspace.Movement.StandHeight / 2));
        merc.Props.Set("TeamNumber", SvJsonNode.Integer(1));
        p.Actors.Add(merc);

        Actor light = new(p.NewId("Light"), "Engine.Light") { Location = new Vec3(0, 0, 448) };
        p.Actors.Add(light);
    }
}

/// <summary>Axis-aligned box brushes (the UE2 cube builder), the base shape most tools start from.</summary>
public static class Box
{
    /// <summary>A box brush of <paramref name="size"/> centred on its origin, faces wound outwards.</summary>
    public static Brush Make(string id, Vec3 size, CsgOper oper, string? material = null)
    {
        var b = new Brush(id) { Oper = oper };
        b.Shape = Shape(size, material);
        return b;
    }

    /// <summary>The cube builder's shape for <paramref name="size"/> (breadth X, width Y, height Z).</summary>
    public static BrushShape Shape(Vec3 size, string? material = null)
    {
        var spec = new BuilderSpec("cube");
        spec.Numbers["breadth"] = size.X;
        spec.Numbers["width"] = size.Y;
        spec.Numbers["height"] = size.Z;
        if (material is not null)
        {
            spec.Strings["material"] = material;
        }

        return BuilderCatalog.Get("cube").Build(spec)[0].Shape;
    }

    /// <summary>A face with world-aligned texture axes (UE2 default planar mapping, 1 texel per unit).</summary>
    public static BrushFace Face(string? material, params Vec3[] vertices) => FaceKit.Make(material, vertices);
}

/// <summary>Texture alignment helpers (UE2 Alignment: Default/Planar).</summary>
public static class TextureAlign
{
    /// <summary>Planar mapping along the face's dominant axis, as the stock editor's default alignment (in the face's
    /// own space; <see cref="TextureOps.AlignDefault"/> does it through a brush transform).</summary>
    public static void Planar(BrushFace f)
    {
        BrushFace aligned = TextureOps.AlignDefault(f, ShapeTransform.Identity);
        f.TextureU = aligned.TextureU;
        f.TextureV = aligned.TextureV;
        f.TextureBase = aligned.TextureBase;
    }
}
