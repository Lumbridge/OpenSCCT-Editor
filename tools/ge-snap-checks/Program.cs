using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Geometry.Snap;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScctEditor.Tools;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
SnapNeighbours Neighbours(IEnumerable<SnapFace> faces, ISnapSweeper? baked = null) => SnapNeighbours.Build(faces, [], _ => [], baked);
Brush ceiling = Box.Make("Ceiling", new(1024, 1024, 32), CsgOper.Add); ceiling.Location = new(0, 0, 316);
var neighbours = Neighbours(SnapFaces.OfBrush(ceiling));
Ge ladder = GeDrawing.Create(GeKinds.Ladder, Vec3.Zero, new(0, 0, 256)); ladder.Id = "Ladder";
var p = new MapProject { Ges = [ladder] };
var snapshot = new TransformSnapshot(p, [ObjectRef.Ge(ladder.Id)]);
MovingShape shape = NeighbourDrag.ShapeOf(snapshot, ObjectRef.Ge(ladder.Id))!;
Check(shape is not null && shape.Faces.Count == 6, "A GE-only selection has a complete snap shape despite its zero Y size");
MoveSnap move = NeighbourSnap.Move(shape!, new(0, 0, 40), [2], neighbours, SnapView.World(8), null);
Check(move.StuckZ && move.Offset.Z == 44, "Ladder top moves exactly onto the ceiling");
var commands = new CommandStack(p);
commands.Execute(snapshot.Translate(p, move.Offset, "move"));
Check(p.Ges[0].Points[1].Z == 300, "Move applies the snapped offset");
commands.Undo(); Check(p.Ges[0].Points.SequenceEqual(ladder.Points), "Move undo restores all control points");
commands.Redo(); Check(p.Ges[0].Points[1].Z == 300, "Move redo restores the snap");
commands.Undo();

Vec3 pivot = shape!.Center;
ValueSnap scale = NeighbourSnap.Scale(shape, pivot, 2, 1.31, neighbours, SnapView.World(8), null)!;
Check(scale is not null, "A flat GE has a resize snap");
commands.Execute(snapshot.Scale(p, pivot, 2, scale!.Value, "scale"));
Check(Math.Abs(p.Ges[0].Points[1].Z - 300) < 1e-6, "Resize lands exactly on the ceiling");
commands.Undo();
MovingShape endpoint = MovingShape.Box(ladder.Points[1], ladder.Points[1]);
MoveSnap resized = NeighbourSnap.Move(endpoint, new(0, 0, 40), [2], neighbours, SnapView.World(8), null);
Check(resized.StuckZ && resized.Offset.Z == 44, "An individual zero-size dot reaches the same plane without padding");
Check(!NeighbourSnap.Move(endpoint, new(0, 0, 40), [0, 1], neighbours, SnapView.World(8), null).StuckZ,
    "A constrained dot never snaps along the view depth or a locked axis");
Check(!NeighbourSnap.Move(endpoint, new(0, 0, 16), [2], neighbours, SnapView.World(8), null).Any,
    "Outside the snap distance, a dot stays free");

foreach (Ge ge in new[] {
    new Ge("Ledge", GeKinds.LedgeGrab) { Points = [Vec3.Zero, new(256, 0, 0)] },
    new Ge("Zip", GeKinds.ZipLine) { Points = [new(0, 0, 256), new(256, 0, 0)] },
    new Ge("Bar", GeKinds.HandOverHand) { Points = [Vec3.Zero, new(256, 0, 0)] },
    new Ge("Pole", GeKinds.Pole) { Points = [Vec3.Zero, new(0, 0, 256)] },
    new Ge("Pipe", GeKinds.Pipe) { Points = ladder.Points.ToList() },
    new Ge("Fence", GeKinds.Fence) { Points = [Vec3.Zero, new(256, 0, 0)] } })
{
    var s = SnapFaces.OfGe(ge)!;
    Check(s.Faces.Count == 6 && s.Directions.Count > 0, ge.Kind + " supports flat bounds and actual rotation directions");
}

Brush wall = Box.Make("AngledWall", new(32, 512, 512), CsgOper.Add); wall.Rotation = new(0, 5461, 0);
var turns = Neighbours(SnapFaces.OfBrush(wall));
TurnSnap turn = NeighbourSnap.Turn(shape.Directions, null, 2, 1, Vec3.Zero, 256, 5300, 400, turns, null)!;
Check(turn is not null && Math.Abs(turn.Units - 5461) <= 1, "Ladder facing lines up with an oblique wall");
Ge slanted = new("Slanted", GeKinds.LedgeGrab) { Points = [Vec3.Zero, new(256, 256, 0)] };
Check(SnapFaces.OfGe(slanted)!.Directions.All(d => Math.Abs(d.X) > .5 && Math.Abs(d.Y) > .5),
    "A turned GE uses its line direction instead of world box axes");
var turned = snapshot.Turned(p, Vec3.Zero, 8192);
Check(Math.Abs(NeighbourDrag.ShapeOf(turned, ObjectRef.Ge(ladder.Id))!.Directions[1].Y) > .7,
    "Turning during a move updates the snap baseline");
var mixed = new TransformSnapshot(new MapProject { Ges = [ladder], Brushes = [wall] }, [ObjectRef.Brush(wall.Id), ObjectRef.Ge(ladder.Id)]);
Check(NeighbourDrag.ShapeOf(mixed, ObjectRef.Ge(ladder.Id))!.Max.Z == 256, "Mixed selection snap bounds include the GE");

var baked = Neighbours([], new Ceiling());
MoveSnap bakedMove = NeighbourSnap.Move(endpoint, new(0, 0, 40), [2], baked, SnapView.World(8), null);
Check(bakedMove.StuckZ && bakedMove.Offset.Z == 44, "Point snapping against baked collision handles degenerate faces");
ValueSnap bakedScale = NeighbourSnap.Scale(shape, pivot, 2, 1.31, baked, SnapView.World(8), null)!;
Check(bakedScale is not null && Math.Abs(pivot.Z + (256 - pivot.Z) * bakedScale.Value - 300) < 1e-6,
    "Flat GE resize against baked collision keeps all six face indices valid");
Check(MovingShape.Box(new(-1, -2, -3), new(1, 2, 3)).Faces.All(f => f.Area > 0), "Ordinary solid snap boxes retain real faces");

if (args.FirstOrDefault() is { } folder)
{
    var sample = MapProject.CreateDefault("GeSnap", "GE handle snapping");
    sample.Brushes.Add(ceiling);
    Brush angled = wall.Clone(); angled.Location = new(100, 150, 128); sample.Brushes.Add(angled);
    sample.Ges.Add(ladder);
    sample.Workspace.CameraLocation = new(-400, -600, 250);
    sample.Workspace.CameraRotation = Orientation.FromAxes(new Vec3(0, 0, 128) - sample.Workspace.CameraLocation, Vec3.UnitZ);
    ProjectStore.Save(sample, Path.GetFullPath(folder));
}
Console.WriteLine($"PASS {checks} GE snapping checks");

sealed class Ceiling : ISnapSweeper
{
    public SweepHit? Sweep(Vec3 from, Vec3 to, Vec3 extent)
    {
        double distance = to.Z - from.Z, gap = 300 - from.Z - extent.Z;
        return distance > 0 && gap >= 0 && gap <= distance
            ? new SweepHit(gap / distance, -Vec3.UnitZ, from + (to - from) * (gap / distance), Exact: true) : null;
    }
    public bool Overlaps(Vec3 at, Vec3 extent) => at.Z + extent.Z > 300;
}
