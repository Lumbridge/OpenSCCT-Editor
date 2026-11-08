using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Scripting;

namespace OpenScct.Editor.Core.Doors;

public static class DoorCommands
{
    public static Actor Create(MapProject p, DoorDesign design, Vec3 centre, Rot rotation = default)
    {
        var a = new Actor(new FreshIds(p).Next("Door"), "Engine.Mover") { Door = design.Clone(), Rotation = rotation };
        Configure(a, centre, rotation); a.Props.Set("Tag", SvJsonNode.String(a.Id)); return a;
    }
    public static void Configure(Actor a, Vec3 centre, Rot rotation)
    {
        var d = a.Door ?? throw new ArgumentException("Select an editable door"); d.Validate();
        a.PrePivot = d.Pivot; a.Location = centre + rotation.Rotate(d.Pivot); a.Rotation = rotation;
        a.DrawScale = 1; a.DrawScale3D = Vec3.One; a.Editor.MoverKey = null;
        a.Props.Set("DrawType", SvJsonNode.Integer((int)OpenScct.Core.Engine.EDrawType.DT_StaticMesh));
        a.Props.Set("BasePos", PropValues.Vector(a.Location)); a.Props.Set("BaseRot", PropValues.Rotator(rotation));
        a.Props.Set("NumKeys", SvJsonNode.Integer(2)); a.Props.Set("KeyNum", SvJsonNode.Integer(0));
        Vec3 offset = d.Motion switch
        {
            "slide left" => new(0, -(d.Travel > 0 ? d.Travel : d.Width + d.FrameWidth), 0),
            "slide right" => new(0, d.Travel > 0 ? d.Travel : d.Width + d.FrameWidth, 0),
            "raise" => new(0, 0, d.Travel > 0 ? d.Travel : d.Height + d.FrameWidth),
            _ => Vec3.Zero,
        };
        double sign = d.Reverse ? -1 : 1;
        var localTurn = new Rot(0, (int)Math.Round(d.OpenAngle / 360 * Rot.UnitsPerTurn * sign * (d.Motion == "swing right" ? -1 : 1)), 0);
        var openRotation = RotatorMath.Compose(localTurn, rotation);
        var turn = new Rot(openRotation.Pitch - rotation.Pitch, openRotation.Yaw - rotation.Yaw, openRotation.Roll - rotation.Roll);
        SvJsonObject Keys(SvJsonNode value) { var elements = new SvJsonObject(); elements.Add("1", value); var keys = new SvJsonObject(); keys.Add("$elements", elements); return keys; }
        a.Props["KeyPos"] = offset == Vec3.Zero ? null : Keys(PropValues.Vector(rotation.Rotate(offset * sign)));
        a.Props["KeyRot"] = d.Motion.StartsWith("swing", StringComparison.Ordinal) ? Keys(PropValues.Rotator(turn)) : null;
        a.Props.Set("InitialState", SvJsonNode.String(d.Behaviour)); a.Props.Set("MoveTime", SvJsonNode.Float((float)d.MoveTime)); a.Props.Set("StayOpenTime", SvJsonNode.Float((float)d.StayOpen));
        a.Props.Set("bCollideActors", SvJsonNode.Bool(true)); a.Props.Set("bBlockActors", SvJsonNode.Bool(true)); a.Props.Set("bBlockPlayers", SvJsonNode.Bool(true));
        DoorAssets.Prepare(a);
    }
    public static ReplaceObjectsCommand Edit(MapProject p, string id, Action<DoorDesign> edit, string? merge = null)
    {
        var before = p.FindActor(id) ?? throw new ArgumentException("Select a door first");
        if (before.Door is null) throw new ArgumentException("This mover has no door design; use the Movers panel to edit its keys");
        var after = before.Clone(); var closed = DoorGeometry.Closed(before);
        edit(after.Door!); Configure(after, closed.LocalToWorld(Vec3.Zero) + Vec3.UnitZ * ((after.Door!.Height - before.Door.Height) / 2), closed.Rotation);
        return new ReplaceObjectsCommand("Edit door", merge).Actor(before.Clone(), after);
    }
    public static void FollowScale(Actor before, Actor after, bool prepare = true)
    {
        if (after.Door is not { } d) return;
        Vec3 old = before.DrawScale3D * before.DrawScale, next = after.DrawScale3D * after.DrawScale;
        if (old == next) return;
        if (next.X <= 0 || next.Y <= 0 || next.Z <= 0) throw new ArgumentException("Door scale must stay positive; use movement and Reverse opening to change its direction");
        double x = next.X / old.X, y = next.Y / old.Y, z = next.Z / old.Z;
        Vec3 centre = after.LocalToWorld(Vec3.Zero);
        d.Width *= y; d.Height *= z; d.Thickness *= x; d.FrameWidth *= Math.Min(y, z); d.FrameDepth *= x; d.CutDepth *= x; d.Clearance *= Math.Min(y, z);
        if (d.Travel > 0) d.Travel *= d.Motion == "raise" ? z : y;
        Configure(after, centre, after.Rotation);
    }
}
