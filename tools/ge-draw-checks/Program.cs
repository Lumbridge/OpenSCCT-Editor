using OpenScct.Content.Json;
using OpenScct.Editor.Cli;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Interop.T3d;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

internal static class Program
{
    private static int _checks;

    private static async Task Main(string[] args)
    {
        if (args is ["--sample", string sample])
        {
            SaveSample(sample);
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "ose-ge-draw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = MapProject.CreateDefault("GeDrawChecks", "GE drawing checks");
            Brush room = Box.Make("Room0", new Vec3(2048, 2048, 1024), CsgOper.Subtract);
            room.Location = new Vec3(0, 0, 384);
            project.Brushes.Add(room);
            var session = new EditorSession(project);
            foreach (string kind in GeKinds.All.Where(GeGeometry.Supports))
            {
                bool vertical = kind is GeKinds.Ladder or GeKinds.Pipe or GeKinds.Pole;
                Vec3 start = vertical ? new Vec3(64, 96, 384) : new Vec3(0, 0, 256);
                Vec3 end = vertical ? new Vec3(128, 192, 0) : new Vec3(512, 128, kind == GeKinds.ZipLine ? 128 : 256);
                Ge ge = GeDrawing.Create(kind, start, end, Vec3.UnitY, GeGeometry.Defaults(kind));
                session.Execute(new AddObjectsCommand("Draw " + kind, ges: [ge]));
                IReadOnlyList<Brush> parts = GeGeometry.Build(ge);
                Check(parts.Count > 0, kind + " makes visible geometry");
                Check(parts.All(b => b.Shape.Faces.Count >= 4 && b.WorldFaces().All(f => f.Vertices.All(Finite))), kind + " makes finite closed solids");
                WorldModel shape = WorldModel.Build(parts, [], worldSolid: false, numbering: null);
                Check(shape.Visible.Count > 0 && (kind == GeKinds.Fence ? shape.Solid.Count > 0 : shape.Solid.Count == 0),
                    kind + " remains visible with collision appropriate for traversal");
                if (vertical)
                {
                    Check(ge.Points[0] == new Vec3(64, 96, 0) && ge.Points[1] == start, kind + " locks XY and sorts downward draw endpoints");
                }

                if (GeKinds.HasThirdPoint(kind))
                {
                    Check(ge.Points[2] == ge.Points[0] + Vec3.UnitY * 10, kind + " preserves climber facing");
                }

                Ge copy = ge.Clone(ge.Id + "Copy");
                copy.Appearance!.Radius += 1;
                Check(copy.Appearance.Radius != ge.Appearance!.Radius, kind + " clone owns its appearance");
                Vec3 shift = new(900, -300, 75);
                copy = ge.Clone();
                copy.Points = copy.Points.Select(p => p + shift).ToList();
                Vec3[] before = parts.SelectMany(b => b.WorldFaces()).SelectMany(f => f.Vertices).ToArray();
                Vec3[] after = GeGeometry.Build(copy).SelectMany(b => b.WorldFaces()).SelectMany(f => f.Vertices).ToArray();
                Check(before.Length == after.Length && before.All(p => after.Any(q => (q - p - shift).Length < 0.001)),
                    kind + " geometry follows translated GE endpoints");
            }

            int count = project.Ges.Count;
            Check(session.Commands.Undo() && project.Ges.Count == count - 1 && session.Commands.Redo() && project.Ges.Count == count,
                "GE and appearance undo and redo as one object");
            Ge ladder = project.Ges.Single(g => g.Kind == GeKinds.Ladder);
            double oldWidth = ladder.Appearance!.Width;
            ChangeResult update = Apply(session, """
                {"op":"ge","id":"Ladder0","appearance":{"width":72,"material":"Test.Metal"}}
                """);
            Check(update.Ok && project.FindGe("Ladder0")!.Appearance!.Width == 72 && project.FindGe("Ladder0")!.Appearance!.Material == "Test.Metal",
                "script changes generated geometry settings");
            Check(session.Commands.Undo() && project.FindGe("Ladder0")!.Appearance!.Width == oldWidth, "appearance edit undo restores prior settings");
            SvJsonObject queried = (SvJsonObject)MapQuery.Run(session, ApiJson.Parse("""{"q":"get","ids":["Ladder0"]}"""));
            Check(queried["items"] is SvJsonArray { Count: 1 } items && items[0] is SvJsonObject first && first["appearance"] is SvJsonObject,
                "get includes appearance in default full object fields");
            SvJsonObject found = (SvJsonObject)MapQuery.Run(session, ApiJson.Parse("""{"q":"find","class":"GE.LADDER","fields":["id","appearance"],"rows":true}"""));
            Check(found["rows"] is SvJsonArray { Count: 1 } rows && rows[0] is SvJsonArray row && row[1] is SvJsonObject look &&
                look["width"] is SvJsonNumber width && ApiJson.RequireNumber(width, "width") == oldWidth,
                "find rows exposes appearance settings");
            Check(!Apply(session, """{"op":"ge","id":"Ladder0","appearance":{"spacing":0}}""").Ok,
                "invalid appearance is rejected atomically");
            Check(project.FindGe("Ladder0")!.Appearance!.Spacing > 0, "failed edit preserves the previous appearance");
            Check(!Apply(session, """{"op":"ge","id":"Ladder0","appearance":{"typo":42}}""").Ok, "unknown appearance fields are errors");
            Check(Apply(session, """{"op":"ge","id":"Ladder0","appearance":false}""").Ok && project.FindGe("Ladder0")!.Appearance is null,
                "script can remove generated geometry without removing the GE");
            Check(session.Commands.Undo() && project.FindGe("Ladder0")!.Appearance is not null, "removed appearance can be restored");

            Throws(() => GeDrawing.Create(GeKinds.Ladder, Vec3.Zero, new Vec3(500, 0, 0)), "flat ladder draw rejected");
            Throws(() => GeDrawing.Create(GeKinds.ZipLine, Vec3.Zero, Vec3.Zero), "zero-length segment rejected");
            Throws(() => GeDrawing.Create(GeKinds.Pole, new Vec3(double.NaN, 0, 0), Vec3.UnitZ * 256), "nonfinite endpoint rejected");
            Throws(() => GeDrawing.Create(GeKinds.LedgeGrab, Vec3.Zero, Vec3.UnitX * 256, appearance: new GeAppearance()), "ledge appearance rejected");
            Ge invalid = project.Ges[0].Clone();
            invalid.Appearance!.Radius = double.PositiveInfinity;
            Throws(() => GeGeometry.Build(invalid), "nonfinite radius rejected");

            string folder = Path.Combine(root, "fixture.osmap");
            session.Save(folder);
            MapProject loaded = ProjectStore.Load(folder);
            Check(loaded.Ges.Count == count && loaded.Ges.All(g => CanonicalJson.ToCanonicalString(ModelJson.GeToJson(g)) ==
                CanonicalJson.ToCanonicalString(ModelJson.GeToJson(project.FindGe(g.Id)!))), "project save/load retains every appearance setting");
            int existingBrushes = project.Brushes.Count;
            MapProject expanded = GeGeometry.Expand(project);
            Check(expanded.Brushes.Count > existingBrushes && project.Brushes.Count == existingBrushes, "lowering adds geometry without changing source project");
            Check(expanded.AllRefs().Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == expanded.AllRefs().Count(), "lowered brush IDs are unique");
            Check(GeGeometry.Expand(expanded).Brushes.Count == expanded.Brushes.Count, "lowering is idempotent");
            MapProject withCollision = ProjectSnapshot.Take(project);
            withCollision.Actors.Add(new Actor("Ladder0_Geometry0", "Engine.Light"));
            withCollision.Prefabs.Add(new PrefabInstance("ExistingPrefab0", "ladder"));
            withCollision.FindGe("Ladder0")!.Source = "imported-ladder";
            MapProject loweredCollision = GeGeometry.Expand(withCollision);
            Check(loweredCollision.FindActor("Ladder0_Geometry0") is not null && loweredCollision.FindBrush("Ladder0_Geometry0") is null &&
                loweredCollision.AllRefs().Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == loweredCollision.AllRefs().Count(),
                "lowering avoids existing actor ID collision");
            Check(loweredCollision.Prefabs.Single().Id == "ExistingPrefab0" && loweredCollision.FindGe("Ladder0")!.Source == "imported-ladder" &&
                withCollision.FindGe("Ladder0")!.Appearance is not null, "lowering preserves prefab metadata and import source");

            T3dDocument export = T3dExport.FromProject(project, null, [ObjectRef.Ge("Ladder0")]);
            Check(export.GeLines.Count == 1 && export.Actors.Count > 2 && export.Actors.All(a => a.Brush is not null),
                "selected GE T3D export includes its generated brushes only");

            int dry = await CliApp.RunAsync(["ge", "draw", folder, "--kind", "LADDER", "--from", "0,0,0", "--to", "0,0,512", "--name", "CliLadder", "--dry-run", "--json"]);
            Check(dry == 0 && ProjectStore.Load(folder).FindGe("CliLadder") is null, "CLI dry-run creates no saved objects");
            int added = await CliApp.RunAsync(["ge", "draw", folder, "--kind", "LADDER", "--from", "0,0,0", "--to", "0,0,512", "--name", "CliLadder", "--width", "64", "--json"]);
            Check(added == 0 && ProjectStore.Load(folder).FindGe("CliLadder")?.Appearance?.Width == 64, "CLI saves GE and generated dimensions");
            int invalidDraw = await CliApp.RunAsync(["ge", "draw", folder, "--kind", "PIPE", "--from", "0,0,0", "--to", "0,0,256", "--name", "InvalidPipe", "--radius", "-1"]);
            Check(invalidDraw != 0 && ProjectStore.Load(folder).FindGe("InvalidPipe") is null, "CLI rejects invalid dimensions without saving a GE");
            int typo = await CliApp.RunAsync(["ge", "draw", folder, "--kind", "PIPE", "--from", "0,0,0", "--to", "0,0,256", "--name", "TypoPipe", "--raduis", "3"]);
            Check(typo != 0 && ProjectStore.Load(folder).FindGe("TypoPipe") is null, "CLI rejects unknown options without silently using defaults");
            int zoneOnly = await CliApp.RunAsync(["ge", "draw", folder, "--kind", "PIPE", "--from", "0,0,0", "--to", "0,0,256", "--name", "ZonePipe", "--zone-only"]);
            Check(zoneOnly == 0 && ProjectStore.Load(folder).FindGe("ZonePipe") is { Appearance: null }, "CLI can create a climbable pipe GE without new geometry");

            CompileResult build = new MapCompiler().Compile(project, Path.Combine(root, project.Id), new CompileOptions
            {
                FastLighting = true,
                WriteClassTable = false,
                Validate = true,
            });
            Check(build.Ok, "compiled GE fixture passes strict SVMap validation: " + string.Join("; ", build.Findings.Where(f => f.Severity == Severity.Error).Select(f => f.Message)));
            Check(build.Stats.GetValueOrDefault("brushes") == expanded.Brushes.Count && build.Stats.GetValueOrDefault("csg.visible") > 6,
                "compiled fixture includes the generated brush surfaces alongside the room");
            Console.WriteLine($"PASS: {_checks} GE drawing checks. Fixture: {root}");
        }
        catch
        {
            Console.Error.WriteLine("GE checks fixture retained: " + root);
            throw;
        }
    }

    private static void SaveSample(string folder)
    {
        MapProject p = MapProject.CreateDefault("Traversal", "Drawn traversal elements");
        Starter.AddStarterRoom(p);
        p.Brushes[0].Shape = Box.Shape(new Vec3(2400, 1800, 900));
        p.Brushes[0].Location = new Vec3(0, 0, 450);
        Add(GeKinds.Ladder, new(-650, -200, 0), new(-650, -200, 384));
        Add(GeKinds.Pipe, new(-400, -200, 0), new(-400, -200, 384));
        Add(GeKinds.Pole, new(-150, -200, 0), new(-150, -200, 384));
        Add(GeKinds.HandOverHand, new(100, -200, 277), new(700, -200, 277));
        Add(GeKinds.ZipLine, new(-650, 200, 500), new(50, 200, 350));
        Add(GeKinds.Fence, new(250, 300, 256), new(800, 300, 256));
        p.Workspace.CameraLocation = new Vec3(-1000, -800, 650);
        p.Workspace.CameraRotation = new Rot(-4096, 8192, 0);
        ProjectStore.Save(p, folder);
        Console.WriteLine("Visual fixture: " + Path.GetFullPath(folder));

        void Add(string kind, Vec3 start, Vec3 end)
        {
            Ge ge = GeDrawing.Create(kind, start, end, -Vec3.UnitY, GeGeometry.Defaults(kind));
            ge.Editor.Note = GeShapes.Stem(kind);
            p.Ges.Add(ge);
        }
    }

    private static ChangeResult Apply(EditorSession session, string op) => ChangeApplier.Apply(session,
        ApiJson.Parse("{\"format\":\"osmap.changes/1\",\"ops\":[" + op + "]}"));

    private static bool Finite(Vec3 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

    private static void Throws(Action action, string name)
    {
        try { action(); }
        catch (ArgumentException) { Check(true, name); return; }
        throw new InvalidOperationException(name + " did not throw");
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) { throw new InvalidOperationException(name); }
        _checks++;
    }
}
