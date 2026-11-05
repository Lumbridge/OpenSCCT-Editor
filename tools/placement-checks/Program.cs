using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Tools;

static void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
static bool Near(Vec3 a, Vec3 b) => (a - b).Length < 1e-6;

// Off-origin bounds must remain supported and centred through all eight drag rotations.
Vec3 min = new(100, -80, -20), max = new(140, -40, 100), point = new(400, 200, 0);
foreach (Vec3 normal in new[] { Vec3.UnitZ, Vec3.UnitX, new Vec3(1, 1, 0).Normalized() })
{
    for (int step = 0; step < 8; step++)
    {
        var (location, rotation) = MeshDrop.OnSurface(point, normal, min, max, yaw: step * 8192);
        Vec3 centre = location + rotation.Rotate((min + max) / 2);
        double contact = double.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            Vec3 corner = new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            contact = Math.Min(contact, Vec3.Dot(location + rotation.Rotate(corner) - point, normal));
        }
        Check(Math.Abs(contact) < 1e-6, $"surface contact {normal}, turn {step}");
        Vec3 tangent = centre - point - normal * Vec3.Dot(centre - point, normal);
        Check(tangent.Length < 1e-6, $"offset mesh footprint centred, turn {step}");
    }
}

var p = new MapProject();
p.Actors.Add(new Actor("a", "Engine.Actor") { Location = new Vec3(-100, 0, 10) });
p.Actors.Add(new Actor("b", "Engine.Actor") { Location = new Vec3(100, 0, 10) });
var snapshot = new TransformSnapshot(p, [ObjectRef.Actor("a"), ObjectRef.Actor("b")]);
Vec3 pivot = new(0, 0, 10), offset = new(32, 48, 0);
var first = snapshot.Translate(p, offset, "drag");
first.Do(p);
var turned = snapshot.Turned(p, pivot, 8192);
var second = turned.Translate(p, offset, "drag");
second.Do(p);
Check(Math.Abs((p.FindActor("a")!.Location - p.FindActor("b")!.Location).Length - 200) < 1e-6, "group spacing survives R");
Check(Near((p.FindActor("a")!.Location + p.FindActor("b")!.Location) / 2, pivot + offset), "group rotates around shared centre");
Check(p.FindActor("a")!.Rotation.Yaw == 8192, "R rotates 45 degrees");
Check(first.TryMerge(second), "move and R merge into one undo step");
first.Undo(p);
Check(Near(p.FindActor("a")!.Location, new Vec3(-100, 0, 10)) && p.FindActor("a")!.Rotation == Rot.Zero, "undo/cancel restores position and orientation");
first.Do(p);
Check(p.FindActor("b")!.Rotation.Yaw == 8192, "redo restores group turn");
var full = snapshot.Turned(p, pivot, 65536).Translate(p, offset, "drag");
full.Do(p);
Check(Near(p.FindActor("a")!.Location, new Vec3(-68, 48, 10)) && p.FindActor("a")!.Rotation.Normalized() == Rot.Zero, "eight presses return to original orientation without drift");
Check(Placeables.Stock.Single(x => x.ShortClass == "SBombTargetObjectiveTrigger") is { Mount: PlaceableMount.Surface, OnFloors: true }, "bomb target permits walls and floors");
Check(Placeables.Stock.Single(x => x.ShortClass == "SExtincteur").OnFloors, "extinguisher permits floors");
Check(Math.Abs(Vec3.Dot(new Rot(0, Placeables.Stock.Single(x => x.ShortClass == "SVitre").WallYaw, 0).Rotate(Vec3.UnitY), Vec3.UnitX)) > .999, "window plane aligns with wall");
foreach (Vec3 normal in new[] { Vec3.UnitX, Vec3.UnitZ, -Vec3.UnitZ })
{
    Placeable window = Placeables.Stock.Single(x => x.ShortClass == "SVitre");
    Check(Math.Abs(Vec3.Dot(window.RotationOn(normal).Rotate(Vec3.UnitY), normal)) > .999,
        $"window plane aligns on {normal}");
    Placeable panel = Placeables.Stock.Single(x => x.ShortClass == "SInterrupteur");
    Check(Vec3.Dot(panel.RotationOn(normal).Rotate(Vec3.UnitX), normal) > .999,
        $"switch mounting plane aligns on {normal}");
}
foreach (string cls in new[] { "SMission", "SObjective", "SGPOControl" })
    Check(Placeables.Stock.Single(x => x.ShortClass == cls).Mount == PlaceableMount.Free, $"{cls} is a free marker");
foreach (Placeable entry in Placeables.Stock)
    Check(entry.MountWords.Contains("Shift") && !entry.MountWords.Contains("only"), $"{entry.Title} advertises free placement");
Console.WriteLine("All placement checks passed.");
