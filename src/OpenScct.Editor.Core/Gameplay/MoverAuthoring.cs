using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

public enum MoverTemplate { Slide, Raise, Swing, Platform, Turntable }

/// <summary>Creation, conversion and useful starting motion shared by the UI and scripts.</summary>
public static class MoverAuthoring
{
    public static string Title(MoverTemplate template) => template switch
    {
        MoverTemplate.Slide => "Sliding door", MoverTemplate.Raise => "Rising door",
        MoverTemplate.Swing => "Swinging door", MoverTemplate.Platform => "Moving platform", _ => "Turntable",
    };

    public static Actor Create(MapProject p, MoverTemplate template, Vec3 at)
    {
        bool platform = template is MoverTemplate.Platform or MoverTemplate.Turntable;
        var a = new Actor(p.NewId(platform ? "Platform" : "Door"), "Engine.Mover")
        {
            Location = at + new Vec3(0, 0, platform ? 16 : 112),
            Shape = Box.Shape(platform ? new Vec3(192, 192, 32) : new Vec3(32, 128, 224), Starter.DefaultMaterial),
        };
        Configure(a, template);
        return a;
    }

    public static bool CanConvert(MapProject p, ObjectRef r) => r.Kind switch
    {
        ObjectKind.Brush => p.FindBrush(r.Id) is { Oper: CsgOper.Add, Stair: null, Editor.Locked: false } b && !Compile.BakedContent.IsBaked(b),
        ObjectKind.Actor => p.FindActor(r.Id) is { Editor.Locked: false } a && !MoverBase.IsMover(a)
            && ClassDefaults.IsA(a, "Engine.StaticMeshActor"),
        _ => false,
    };

    /// <summary>Preserves identity, materials, mesh, transform and incoming references, as one undo step.</summary>
    public static IEditCommand Convert(MapProject p, ObjectRef r, MoverTemplate template)
    {
        if (!CanConvert(p, r)) throw new ArgumentException("Select an unlocked static mesh or an additive brush. Rebuild imported geometry before converting a baked brush.");
        if (r.Kind == ObjectKind.Actor)
        {
            Actor source = p.FindActor(r.Id)!;
            Actor a = source.Clone(); a.Class = "Engine.Mover"; a.Editor.UnlinkPrefab();
            Configure(a, template);
            return new ReplaceObjectsCommand("Make " + source.Id + " move").Actor(source, a);
        }
        Brush b = p.FindBrush(r.Id)!;
        // Bake the original brush's affine transform into the local faces, retaining world texture placement.
        Actor mover = FromBrush(b, template);
        return new CompositeCommand("Make " + b.Id + " move", [new DeleteObjectsCommand([r]), new AddObjectsCommand("Add mover", actors: [mover])]);
    }

    public static Actor FromBrush(Brush b, MoverTemplate template)
    {
        var faces = b.WorldFaces().ToList();
        foreach (var face in faces)
        {
            face.Vertices = face.Vertices.Select(v => v - b.Location).ToList();
            face.TextureBase -= b.Location;
        }
        var mover = new Actor(b.Id, "Engine.Mover") { Location = b.Location, Shape = new BrushShape { Faces = faces },
            Props = b.Props.Clone(), Editor = b.Editor.Clone() };
        mover.Editor.UnlinkPrefab();
        Configure(mover, template);
        return mover;
    }

    public static ReplaceObjectsCommand ApplyTemplate(MapProject p, string id, MoverTemplate template) =>
        ReplaceObjectsCommand.Edit(p, [ObjectRef.Actor(id)], "Set up " + Title(template).ToLowerInvariant(), actor: a =>
        {
            var m = MoverSetup.Read(a);
            a.Location = m.BasePos; a.Rotation = m.BaseRot;
            Configure(a, template);
        }, followBase: false);

    public static IEditCommand AddSwitch(MapProject p, string id, Vec3 at)
    {
        if (p.FindActor(id) is not { } mover || !MoverBase.IsMover(mover)) throw new ArgumentException("Select a mover first");
        var sender = new Actor(p.NewId("MoverSwitch"), "SBase.SInterrupteur") { Location = at };
        sender.Props["DrawType"] = SvJsonNode.Integer((int)OpenScct.Core.Engine.EDrawType.DT_StaticMesh);
        sender.Props["StaticMesh"] = PropValues.AssetRef("SGameplayObjects.SGPO_SM.trigger_urgence");
        sender.Props["UsableBy"] = SvJsonNode.Integer(3);
        sender.Props["sDescriptionText"] = SvJsonNode.String("Operate " + id);
        sender.Props["fInteractionRadius"] = SvJsonNode.Float(50);
        sender.Props["UseRelativeLocation"] = PropValues.Vector(new Vec3(39.5, 0, 0));
        var scratch = Session.ProjectSnapshot.Take(p); scratch.Actors.Add(sender);
        var wire = Wiring.Link(scratch, sender.Id, id)!;
        return new CompositeCommand("Add a switch for " + id, [new AddObjectsCommand("Place mover switch", actors: [sender]), wire]);
    }

    private static void Configure(Actor a, MoverTemplate template)
    {
        a.Legs = null; a.Editor.MoverKey = null;
        a.Props["bStatic"] = SvJsonNode.False;
        a.Props["bUseCylinderCollision"] = SvJsonNode.False;
        a.Props["bCollideActors"] = a.Props["bBlockActors"] = a.Props["bBlockPlayers"] = SvJsonNode.True;
        a.Props["Physics"] = SvJsonNode.Integer((int)OpenScct.Core.Engine.EPhysics.PHYS_MovingBrush);
        a.Props["Tag"] ??= SvJsonNode.String(a.Id);
        a.Props["BasePos"] = PropValues.Vector(a.Location);
        a.Props["BaseRot"] = PropValues.Rotator(a.Rotation);
        a.Props["NumKeys"] = SvJsonNode.Integer(2); a.Props["KeyNum"] = null;
        a.Props["MoveTime"] = SvJsonNode.Float(template == MoverTemplate.Platform ? 3 : 1.5f);
        a.Props["StayOpenTime"] = SvJsonNode.Float(4);
        a.Props["InitialState"] = SvJsonNode.String(template switch
        {
            MoverTemplate.Platform => "StandOpenTimed", MoverTemplate.Turntable => "TriggerToggle", _ => "BumpOpenTimed",
        });
        a.Props["MoverGlideType"] = SvJsonNode.Integer(1);
        var size = a.Shape is { } shape ? shape.Bounds().Max - shape.Bounds().Min : new Vec3(32, 128, 224);
        Vec3 offset = template switch
        {
            MoverTemplate.Slide => a.Rotation.Rotate(new Vec3(0, Math.Max(128, size.Y), 0)),
            MoverTemplate.Raise => new Vec3(0, 0, Math.Max(128, size.Z)),
            MoverTemplate.Platform => new Vec3(0, 0, 256), _ => Vec3.Zero,
        };
        if (template == MoverTemplate.Swing && a.Shape is { } hinged)
        {
            Vec3 pivot = new(0, hinged.Bounds().Min.Y, 0);
            a.Location += a.Rotation.Rotate((pivot - a.PrePivot).Scale(a.DrawScale3D) * a.DrawScale);
            a.PrePivot = pivot; a.Props["BasePos"] = PropValues.Vector(a.Location);
        }
        a.Props["KeyPos"] = new SvJsonArray([PropValues.Vector(Vec3.Zero), PropValues.Vector(offset)]);
        a.Props["KeyRot"] = new SvJsonArray([PropValues.Rotator(Rot.Zero), PropValues.Rotator(
            new Rot(0, template == MoverTemplate.Swing ? 16384 : template == MoverTemplate.Turntable ? 32768 : 0, 0))]);
        if (a.Shape is not null) a.Props["DrawType"] = SvJsonNode.Integer((int)OpenScct.Core.Engine.EDrawType.DT_Brush);
        else if (PropValues.AssetPath(a.Props["StaticMesh"]) is not null)
            a.Props["DrawType"] = SvJsonNode.Integer((int)OpenScct.Core.Engine.EDrawType.DT_StaticMesh);
    }
}
