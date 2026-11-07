using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;

var project = MapProject.CreateDefault("LedgeProbe", "Short ledge trace regression");
Brush room = Box.Make("Room", new Vec3(2000, 2000, 1600), CsgOper.Subtract);
room.Location = new Vec3(350, 1990, 0);
project.Brushes.Add(room);
Brush stone = Box.Make("Stone", new Vec3(216, 122, 221), CsgOper.Add);
stone.Location = new Vec3(362, 1993, -81.5);
project.Brushes.Add(stone);
TraversalWorld world = TraversalWorld.Build(project);
int checks = 0;
foreach (double x in new[] { 265d, 340, 375 })
{
    foreach (double y in new[] { 1971d, 1980, 2050 })
    {
        TraceHit? longHit = world.Trace(new Vec3(x, y, 400), new Vec3(x, y, -250));
        TraceHit? shortHit = world.Trace(new Vec3(x, y, 61), new Vec3(x, y, -11));
        Require(longHit is { } a && shortHit is { } b && Math.Abs(a.Point.Z - 29) < .001 && Math.Abs(b.Point.Z - 29) < .001,
            $"long and short rays both hit the stone at ({x}, {y})");
    }
}

Require(world.Trace(new Vec3(265, 2058, 61), new Vec3(265, 2058, -11)) is null, "a short ray outside the lip misses");
var options = new LedgeOptions();
LipFit? fit = LedgeProbe.Fit(world, new Vec3(280, 2054, 29), new Vec3(440, 2054, 29), options);
Require(fit is not null && Math.Abs(fit.Across) < .01 && Math.Abs(fit.TopZ - 29) < .001, "the authored ledge fits its real edge");
Require(LedgeProbe.Evaluate(world, new Vec3(360, 2054, 29), fit!.HangSide, options).Problem == LipProblem.None,
    "the correctly placed ledge remains grabbable");
Console.WriteLine($"PASS: {checks} short-trace and ledge checks");

void Require(bool result, string description)
{
    if (!result) throw new InvalidOperationException(description);
    checks++;
}
