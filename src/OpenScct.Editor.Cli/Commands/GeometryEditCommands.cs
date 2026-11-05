using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using static OpenScct.Editor.Cli.Commands.GeometryCliSupport;

namespace OpenScct.Editor.Cli.Commands;

/// <summary><c>ose edit</c>: the editor's face, vertex, clip and placement tools, headless (the same Core ops the
/// app's tools call). Every changing op is one undo step and saves the project (<c>--dry-run</c>: report only).</summary>
internal static class GeometryEditCommands
{
    private const string Help =
        "Runs a geometry editing tool on a project. Faces are Brush:index; points are x,y,z; lists of points use ';'.\n" +
        "Surfaces:\n" +
        "  faces <brushes...>                          list faces: material, flags, normal, scale, rotation, pan\n" +
        "  material <faces...> --mat Pkg.Group.Name    set the material (--mat none: the default)\n" +
        "  flags <faces...> [--set a,b] [--clear c]    poly flags: " + "invisible, masked, translucent, twoSided, unlit, portal...\n" +
        "  lightmap <faces...> --scale 16              lightmap scale\n" +
        "  surface <faces...> [--pan u,v] [--rotate deg] [--scale u,v] [--fit u,v]   set the mapping absolutely\n" +
        "  select-faces <material|coplanar|walls|floors|ceilings> [--mat m] [--face B:i] [--brushes ids]   list matching faces\n" +
        "  extrude <Brush:face> --by 64 [--new]        push the face out (negative: in), or grow a new brush from it\n" +
        "Vertices and edges:\n" +
        "  vertices <brush>                            list world vertices and convexity\n" +
        "  move-vertex <brush> --at p;p --by dx,dy,dz  move vertices (faces that bend are split into triangles)\n" +
        "  weld <brush> --at p;p [--to x,y,z]         weld vertices into one point (their centre, or --to)\n" +
        "  delete-vertex <brush> --at p;p              remove vertices (the brush becomes the hull of the rest)\n" +
        "  snap-vertex <brush> [--at p;p] [--grid 16]  snap vertices (all when no --at) to the world grid\n" +
        "  bevel <brush> --edge p;p --size 16          cut an edge off\n" +
        "  clip <brushes...> --points p;p[;p] [--axis z] [--split] [--flip] [--cap mat]   clip or split by a plane\n" +
        "Placement:\n" +
        "  drop <actors...> [--ceiling] [--align] [--half h]   drop to the floor (or ceiling) by brushes and baked collision\n" +
        "  snap <ids...> [--grid 16]                   snap pivots to the grid\n" +
        "  mirror <ids...> --axis x|y|z [--about x,y,z]   mirror across the selection's centre (or a point)\n" +
        "  array <ids...> --count 3 --step x,y,z      copies along a vector (custom event Tags get fresh ones per copy)\n" +
        "  replace-class <actors...> --class Pkg.Class    change the class, keeping properties\n" +
        "  measure <x,y,z> <x,y,z>                     distance, player heights, spy/merc run time";

    [CliProvider]
    private static IEnumerable<CliCommand> Commands() =>
    [
        new("edit", "edit <project> <op> [args...] [options] [--dry-run] [--json]", Help, RunAsync),
    ];

    private static Task<int> RunAsync(CliArgs a)
    {
        EnsureServices();
        string folder = a.Positional(0) ?? throw new ArgumentException("give the project folder");
        string op = a.Positional(1)?.ToLowerInvariant() ?? throw new ArgumentException("give an op (ose edit --help)");
        EditorSession session = EditorSession.Open(folder);
        MapProject p = session.Project;
        List<string> args = a.PositionalArgs.Skip(2).ToList();
        var report = new SvJsonObject();
        report.Add("op", SvJsonNode.String(op));
        IEditCommand? command = op switch
        {
            "faces" => ListFaces(p, args, report),
            "material" => FaceEdit.Apply(p, Faces(args), "Set material", FaceEdit.SetMaterial(Material(a))),
            "flags" => FaceEdit.Apply(p, Faces(args), "Set surface flags", FaceEdit.SetFlags(SurfaceFlags.Parse(a.Get("set")), SurfaceFlags.Parse(a.Get("clear")))),
            "lightmap" => FaceEdit.Apply(p, Faces(args), "Set lightmap scale", FaceEdit.SetLightMapScale(Opt(a, "scale", 32))),
            "surface" => Surface(p, a, args),
            "select-faces" => SelectFaces(p, a, args, report),
            "extrude" => ExtrudeFace(p, a, args, report),
            "wall-add" => WallAdd(session, a, args, report),
            "vertices" => ListVertices(p, args, report),
            "move-vertex" => Vertex(p, args, report, b => VertexEdit.Move(b, Points(a, "at"), Vec(a, "by")), "Move vertices"),
            "weld" => Vertex(p, args, report, b => VertexEdit.Weld(b, Points(a, "at"), a.Has("to") ? Vec(a, "to") : null), "Weld vertices"),
            "delete-vertex" => Vertex(p, args, report, b => VertexEdit.Delete(b, Points(a, "at")), "Delete vertices"),
            "snap-vertex" => Vertex(p, args, report,
                b => VertexEdit.Snap(b, a.Has("at") ? Points(a, "at") : null, Opt(a, "grid", p.Workspace.GridSize)), "Snap vertices"),
            "bevel" => Bevel(p, a, args),
            "clip" => Clip(p, a, args, report),
            "drop" => Drop(session, a, args, report),
            "snap" => SelectionEdit.SnapToGrid(p, Refs(p, args), Opt(a, "grid", p.Workspace.GridSize)),
            "mirror" => Mirror(p, a, args),
            "array" => ArrayCopies(p, a, args, report),
            "replace-class" => SelectionEdit.ReplaceClass(p, Refs(p, args), a.Get("class") ?? throw new ArgumentException("give --class")),
            "measure" => Measure(p, args, report),
            _ => throw new ArgumentException($"unknown edit op '{op}' (ose edit --help)"),
        };

        if (command is ReplaceObjectsCommand { IsEmpty: true })
        {
            command = null;
        }

        if (command is not null)
        {
            report.Add("step", SvJsonNode.String(command.Name));
            if (!a.Has("dry-run"))
            {
                session.Execute(command);
                session.Save();
            }
        }

        report.Add("saved", SvJsonNode.Bool(command is not null && !a.Has("dry-run")));
        if (a.Has("json"))
        {
            PrintJson(report, a);
        }
        else
        {
            Console.WriteLine(CanonicalJson.ToPrettyString(report));
        }

        return Task.FromResult(0);
    }

    // ---- surfaces ----------------------------------------------------------------------------------------------

    private static IEditCommand? ListFaces(MapProject p, List<string> args, SvJsonObject report)
    {
        var list = new SvJsonArray();
        foreach (Brush b in (args.Count > 0 ? args : throw new ArgumentException("give brush ids")).Select(id => FindBrush(p, id)))
        {
            ShapeTransform t = ShapeTransform.For(b);
            for (int i = 0; i < b.Shape.Faces.Count; i++)
            {
                BrushFace f = b.Shape.Faces[i];
                SurfaceInfo info = SurfaceInfo.Of(f, t);
                var o = new SvJsonObject();
                o.Add("face", SvJsonNode.String(new FaceRef(b.Id, i).ToString()));
                o.Add("material", f.Material is null ? SvJsonNode.Null : SvJsonNode.String(f.Material));
                o.Add("flags", Array(SurfaceFlags.Names(f.Flags)));
                o.Add("normal", ModelJson.Vec(Round(info.Normal)));
                o.Add("scale", new SvJsonArray([Num(info.ScaleU), Num(info.ScaleV)]));
                o.Add("rotation", Num(Math.Round(info.Rotation, 3)));
                o.Add("pan", new SvJsonArray([Num(info.PanU), Num(info.PanV)]));
                o.Add("lightMapScale", Num(f.LightMapScale));
                o.Add("area", Num(Math.Round(info.Area, 1)));
                list.Add(o);
            }
        }

        report.Add("faces", list);
        return null;
    }

    private static IEditCommand Surface(MapProject p, CliArgs a, List<string> args)
    {
        List<FaceRef> faces = Faces(args);
        var steps = new List<(string Name, Func<BrushFace, ShapeTransform, BrushFace> Op)>();
        if (a.Get("scale") is { } s)
        {
            double[] v = Parsing.Numbers(s, 2, "--scale (u,v)");
            steps.Add(("scale", FaceEdit.SetScale(v[0], v[1])));
        }

        if (a.Get("rotate") is { } r)
        {
            steps.Add(("rotate", FaceEdit.SetRotation(Parsing.Number(r, "--rotate"))));
        }

        if (a.Get("fit") is { } fit)
        {
            double[] v = fit == "true" ? [1, 1] : Parsing.Numbers(fit, 2, "--fit (u,v)");
            steps.Add(("fit", FaceEdit.Fit(v[0], v[1])));
        }

        if (a.Get("pan") is { } pan)
        {
            double[] v = Parsing.Numbers(pan, 2, "--pan (u,v)");
            steps.Add(("pan", FaceEdit.SetPan((int)Math.Round(v[0]), (int)Math.Round(v[1]))));
        }

        if (steps.Count == 0)
        {
            throw new ArgumentException("surface: give --pan, --rotate, --scale or --fit");
        }

        // Later settings read the faces as the earlier ones left them, so compose them per face.
        return FaceEdit.Apply(p, faces, "Texture " + string.Join(", ", steps.Select(x => x.Name)),
            (f, t) => steps.Aggregate(f, (face, step) => step.Op(face, t)));
    }

    private static IEditCommand? SelectFaces(MapProject p, CliArgs a, List<string> args, SvJsonObject report)
    {
        string kind = args.FirstOrDefault()?.ToLowerInvariant() ?? throw new ArgumentException("select-faces: give a kind");
        IEnumerable<string> brushes = a.Get("brushes")?.Split(',', StringSplitOptions.TrimEntries) ?? p.Brushes.Select(b => b.Id);
        IEnumerable<FaceRef> faces = kind switch
        {
            "material" => FaceEdit.WithMaterial(p, Material(a)),
            "coplanar" => FaceEdit.Coplanar(p, FaceRef.Parse(a.Get("face") ?? throw new ArgumentException("give --face Brush:index"))),
            _ => FaceEdit.BySlope(p, brushes, kind),
        };
        report.Add("faces", Array(faces.Select(f => f.ToString())));
        return null;
    }

    private static IEditCommand ExtrudeFace(MapProject p, CliArgs a, List<string> args, SvJsonObject report)
    {
        FaceRef f = FaceRef.Parse(args.FirstOrDefault() ?? throw new ArgumentException("extrude: give Brush:face"));
        Brush b = FindBrush(p, f.Brush);
        double by = Opt(a, "by", p.Workspace.GridSize);
        if (a.Has("new"))
        {
            Brush nb = Extrude.NewBrush(b, f.Face, by, new FreshIds(p).Next(b.Id));
            report.Add("added", SvJsonNode.String(nb.Id));
            return new AddObjectsCommand($"Extrude {nb.Id}", [nb]);
        }

        VertexEditResult r = Extrude.Face(b, f.Face, by);
        AddWarning(report, r);
        return new ReplaceObjectsCommand($"Extrude {f}").Brush(b.Clone(), r.Brush);
    }

    private static IEditCommand WallAdd(EditorSession session, CliArgs a, List<string> args, SvJsonObject report)
    {
        MapProject p = session.Project;
        FaceRef f = FaceRef.Parse(args.FirstOrDefault() ?? throw new ArgumentException("wall-add: give Brush:face"));
        string piece = a.Get("piece") ?? "doorway";
        Vec3? at = a.Has("at") ? Vec(a, "at") : null;
        WallAnchor anchor = WallPieces.Anchor(p, f, at)
            ?? throw new ArgumentException($"wall-add: {f} is not a wall of a carved (subtract) brush");
        List<Brush> added = WallPieces.Build(p, anchor, piece, new WorldGeometry(p, session.Folder));
        report.Add("placed", SvJsonNode.String(WallPieces.Describe(anchor, piece)));
        report.Add("added", Array(added.Select(b => b.Id)));
        return new AddObjectsCommand($"Add {piece} on wall", added);
    }

    // ---- vertices ------------------------------------------------------------------------------------------------

    private static IEditCommand? ListVertices(MapProject p, List<string> args, SvJsonObject report)
    {
        Brush b = FindBrush(p, args.FirstOrDefault());
        report.Add("brush", SvJsonNode.String(b.Id));
        report.Add("vertices", new SvJsonArray(VertexEdit.Vertices(b).Select(v => ModelJson.Vec(Round(v)))));
        report.Add("edges", SvJsonNode.Integer(VertexEdit.Edges(b).Count));
        report.Add("convex", SvJsonNode.Bool(ShapeAnalysis.IsConvexSolid(b.Shape)));
        return null;
    }

    private static IEditCommand Vertex(MapProject p, List<string> args, SvJsonObject report, Func<Brush, VertexEditResult> edit, string name)
    {
        Brush b = FindBrush(p, args.FirstOrDefault());
        VertexEditResult r = edit(b);
        AddWarning(report, r);
        report.Add("faces", SvJsonNode.Integer(r.Brush.Shape.Faces.Count));
        return new ReplaceObjectsCommand($"{name} of {b.Id}").Brush(b.Clone(), r.Brush);
    }

    private static IEditCommand Bevel(MapProject p, CliArgs a, List<string> args)
    {
        Brush b = FindBrush(p, args.FirstOrDefault());
        List<Vec3> edge = Points(a, "edge");
        if (edge.Count != 2)
        {
            throw new ArgumentException("bevel: --edge takes two points (x,y,z;x,y,z)");
        }

        return new ReplaceObjectsCommand($"Bevel {b.Id}").Brush(b.Clone(), VertexEdit.Bevel(b, edge[0], edge[1], Opt(a, "size", 16)));
    }

    private static IEditCommand? Clip(MapProject p, CliArgs a, List<string> args, SvJsonObject report)
    {
        int axis = Axis(a.Get("axis") ?? "z");
        Plane3 plane = ClipEdit.PlaneFrom(Points(a, "points"), axis) ?? throw new ArgumentException("clip: the points do not define a plane");
        if (a.Has("flip"))
        {
            plane = plane.Flipped;
        }

        BrushListEdit edit = ClipEdit.Run(p, args.Count > 0 ? args : throw new ArgumentException("clip: give brush ids"), plane, a.Has("split"), a.Get("cap"));
        report.Add("changed", Array(edit.Changed));
        report.Add("added", Array(edit.Added));
        report.Add("removed", Array(edit.Removed));
        return edit.IsEmpty ? null : edit.Build();
    }

    // ---- placement -----------------------------------------------------------------------------------------------

    private static IEditCommand Drop(EditorSession session, CliArgs a, List<string> args, SvJsonObject report)
    {
        MapProject p = session.Project;
        bool ceiling = a.Has("ceiling"), align = a.Has("align");
        var cmd = new ReplaceObjectsCommand(ceiling ? "Drop to ceiling" : "Drop to floor");
        var missed = new List<string>();
        var world = new WorldGeometry(p, session.Folder);
        foreach (string id in args.Count > 0 ? args : throw new ArgumentException("drop: give actor ids"))
        {
            Actor actor = p.FindActor(id) ?? throw new ArgumentException($"no actor '{id}'");
            double half = a.Get("half") is { } h ? Parsing.Number(h, "--half") : ActorPlacement.HalfHeight(actor);
            if (ActorPlacement.Drop(world, actor.Location, half, ceiling) is not { } hit)
            {
                missed.Add(actor.Id);
                continue;
            }

            Actor after = actor.Clone();
            after.Location = hit.Location;
            if (align)
            {
                after.Rotation = Orientation.AlignUp(actor.Rotation, ceiling ? -hit.Normal : hit.Normal);
            }

            MoverBase.Follow(actor, after);
            cmd.Actor(actor.Clone(), after);
        }

        report.Add("missed", Array(missed));
        return cmd;
    }

    private static IEditCommand Mirror(MapProject p, CliArgs a, List<string> args)
    {
        List<ObjectRef> refs = Refs(p, args);
        Vec3 about = a.Get("about") is { } s ? Parsing.Vector(s, "--about") : SelectionEdit.PivotCentre(p, refs);
        return SelectionEdit.Mirror(p, refs, Axis(a.Get("axis") ?? "x"), about);
    }

    private static IEditCommand ArrayCopies(MapProject p, CliArgs a, List<string> args, SvJsonObject report)
    {
        AddObjectsCommand command = SelectionEdit.Array(p, Refs(p, args), a.GetInt("count", 1), Vec(a, "step"), out Finding? note);
        if (note is not null)
        {
            report.Add("note", SvJsonNode.String(note.Message));
        }

        return command;
    }

    private static IEditCommand? Measure(MapProject p, List<string> args, SvJsonObject report)
    {
        if (args.Count != 2)
        {
            throw new ArgumentException("measure: give two points");
        }

        Measurement m = Measurement.Between(Parsing.Vector(args[0]), Parsing.Vector(args[1]), p.Workspace.Movement);
        report.Add("distance", Num(Math.Round(m.Distance, 2)));
        report.Add("horizontal", Num(Math.Round(m.Horizontal, 2)));
        report.Add("delta", ModelJson.Vec(m.Delta));
        report.Add("playerHeights", Num(Math.Round(m.PlayerHeights, 3)));
        report.Add("spySeconds", Num(Math.Round(m.SpySeconds, 3)));
        report.Add("mercSeconds", Num(Math.Round(m.MercSeconds, 3)));
        report.Add("summary", SvJsonNode.String(m.Summary()));
        return null;
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static List<FaceRef> Faces(List<string> args) =>
        args.Count > 0 ? args.Select(FaceRef.Parse).ToList() : throw new ArgumentException("give one or more faces (Brush:index)");

    private static List<ObjectRef> Refs(MapProject p, List<string> ids)
    {
        if (ids.Count == 0)
        {
            throw new ArgumentException("give one or more object ids");
        }

        return ids.Select(id => p.FindBrush(id) is { } b ? ObjectRef.Brush(b.Id)
            : p.FindActor(id) is { } ac ? ObjectRef.Actor(ac.Id)
            : p.FindGe(id) is { } g ? ObjectRef.Ge(g.Id)
            : throw new ArgumentException($"no object '{id}'")).ToList();
    }

    private static Brush FindBrush(MapProject p, string? id) =>
        id is null ? throw new ArgumentException("give a brush id") : p.FindBrush(id) ?? throw new ArgumentException($"no brush '{id}'");

    private static string? Material(CliArgs a)
    {
        string m = a.Get("mat") ?? throw new ArgumentException("give --mat (a material path, or none)");
        return m.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : m;
    }

    private static List<Vec3> Points(CliArgs a, string key) =>
        (a.Get(key) ?? throw new ArgumentException($"give --{key} x,y,z[;x,y,z...]"))
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(t => Parsing.Vector(t, "--" + key)).ToList();

    private static Vec3 Vec(CliArgs a, string key) =>
        Parsing.Vector(a.Get(key) ?? throw new ArgumentException($"give --{key} x,y,z"), "--" + key);

    private static double Opt(CliArgs a, string key, double fallback) =>
        a.Get(key) is { } s ? Parsing.Number(s, "--" + key) : fallback;

    private static Vec3 Round(Vec3 v) => new(Math.Round(v.X, 3), Math.Round(v.Y, 3), Math.Round(v.Z, 3));

    private static void AddWarning(SvJsonObject report, VertexEditResult r)
    {
        report.Add("convex", SvJsonNode.Bool(r.Convex));
        report.Add("splitFaces", SvJsonNode.Integer(r.SplitFaces));
        if (r.Warning is { } w)
        {
            report.Add("warning", SvJsonNode.String(w));
        }
    }

    private static int Axis(string s) => s.ToLowerInvariant() switch
    {
        "x" => 0,
        "y" => 1,
        "z" => 2,
        _ => throw new ArgumentException($"axis must be x, y or z, not '{s}'"),
    };
}
