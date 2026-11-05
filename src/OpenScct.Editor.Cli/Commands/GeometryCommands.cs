using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Session;
using static OpenScct.Editor.Cli.Commands.GeometryCliSupport;

namespace OpenScct.Editor.Cli.Commands;

/// <summary><c>ose shapes</c>, <c>ose shape</c> and <c>ose brush</c>: builders and brush operations.</summary>
internal static class GeometryCommands
{
    private static readonly HashSet<string> s_shapeOptions = new(StringComparer.OrdinalIgnoreCase) { "at", "rot", "csg", "pretty", "json", "help" };

    private static readonly HashSet<string> s_brushOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "at", "rot", "csg", "pretty", "json", "help", "dry-run",
    };

    [CliProvider]
    private static IEnumerable<CliCommand> Commands() =>
    [
        new("shapes", "shapes [kind] [--json]",
            "Lists the brush builders and blockout pieces with their parameters, defaults and ranges.", ShapesAsync),
        new("shape", "shape <kind> [--<param> value ...] [--at x,y,z] [--rot pitch,yaw,roll] [--csg subtract|add] [--pretty]",
            "Builds a shape and prints its brushes as project JSON (builder spec on each shape).\n" +
            "Plain builders carve (subtract) unless --csg add (sheets add); blockout pieces choose per brush from their mode.", ShapeAsync),
        new("brush", "brush <project> <op> [ids...] [options] [--dry-run] [--json]", BrushHelp, BrushAsync),
        new("probe", "probe <project> <x,y,z> [x,y,z ...] [--drop 600] [--json]",
            "Tells whether world points are in solid or open space by the project's brushes (CSG replay), and how far\n" +
            "down the floor is (within --drop). A baked world's geometry is not seen.", ProbeAsync),
    ];

    private const string BrushHelp =
        "Edits brushes of a project in one undo step and saves it (--dry-run: report only). Ops:\n" +
        "  add <kind> [--<param> v] [--at x,y,z] [--rot p,y,r] [--csg add|subtract]   place a builder shape or blockout piece\n" +
        "  rebuild <id> [--<param> v]       regenerate a parametric piece with changed parameters\n" +
        "  info <ids>                       convexity, planarity, closure, volume, bounds\n" +
        "  split|clip <id> --plane nx,ny,nz,d [--cap material]   cut by a world plane (clip keeps the back)\n" +
        "  flip | mirror --axis x|y|z | merge-faces | triangulate | hull | bake | recenter <ids>\n" +
        "  snap <ids> [--grid 16]           snap vertices to the world grid\n" +
        "  uv <mode> <ids> [--faces 0,2] [--scale s]   modes: default, floor, wall, face, cylinder,\n" +
        "     fit --tex 256,256 [--repeat 1,1], pan --u 16 --v 0, rotate --deg 90, scale --u 2 --v 2 [--absolute], flip-u, flip-v\n" +
        "  paste-surface <ids> --from Brush:face [--faces ...] [--mapping]";

    private static Task<int> ShapesAsync(CliArgs a)
    {
        IEnumerable<BuilderKind> kinds = a.Positional(0) is { } k ? [BuilderCatalog.Get(k)] : BuilderCatalog.All;
        if (a.Has("json"))
        {
            PrintJson(new SvJsonArray(kinds.Select(KindToJson)), a);
            return Task.FromResult(0);
        }

        foreach (BuilderKind kind in kinds)
        {
            Console.WriteLine($"{kind.Kind,-12} {(kind.IsBlockout ? "blockout" : "builder "),-8}  {kind.Summary}");
            if (a.Positional(0) is not null)
            {
                Console.WriteLine($"  origin: {kind.Origin}" + (kind.IsBlockout ? "" : $"; csg {(kind.DefaultOper == CsgOper.Add ? "add" : "subtract")} unless --csg"));
                foreach (BuilderParam p in kind.Params)
                {
                    string range = p.Choices.Count > 0 ? string.Join("|", p.Choices)
                        : p.Type is ParamType.Number or ParamType.Integer ? $"{Inv(p.Min)}..{Inv(p.Max)}" : p.Type.ToString().ToLowerInvariant();
                    Console.WriteLine($"  --{p.Name,-18} {p.DefaultString,-14} {range,-18} {p.Summary}");
                }
            }
        }

        return Task.FromResult(0);
    }

    private static SvJsonObject KindToJson(BuilderKind kind)
    {
        var o = new SvJsonObject();
        o.Add("kind", SvJsonNode.String(kind.Kind));
        o.Add("blockout", SvJsonNode.Bool(kind.IsBlockout));
        o.Add("summary", SvJsonNode.String(kind.Summary));
        o.Add("origin", SvJsonNode.String(kind.Origin));
        if (!kind.IsBlockout)
        {
            o.Add("csg", SvJsonNode.String(kind.DefaultOper == CsgOper.Add ? "add" : "subtract"));
        }

        o.Add("params", new SvJsonArray(kind.Params.Select(ParamToJson)));
        return o;
    }

    private static Task<int> ShapeAsync(CliArgs a)
    {
        BuilderKind kind = BuilderCatalog.Get(a.Positional(0) ?? throw new ArgumentException("give a shape kind (ose shapes lists them)"));
        BuilderSpec spec = SpecFromOptions(kind, a, s_shapeOptions);
        Vec3 at = a.Get("at") is { } t ? Parsing.Vector(t, "--at") : Vec3.Zero;
        Rot rot = a.Get("rot") is { } r ? Parsing.Rotator(r) : Rot.Zero;
        var scratch = new MapProject();
        IReadOnlyList<Brush> brushes = PiecePlacer.Create(scratch, spec, at, rot, ParseCsg(a.Get("csg")));
        var o = new SvJsonObject();
        o.Add("kind", SvJsonNode.String(kind.Kind));
        o.Add("brushes", new SvJsonArray(brushes.Select(b => ModelJson.BrushToJson(b))));
        PrintJson(o, a);
        return Task.FromResult(0);
    }

    // ---- ose probe ---------------------------------------------------------------------------------------------

    private static Task<int> ProbeAsync(CliArgs a)
    {
        string folder = a.Positional(0) ?? throw new ArgumentException("give the project folder");
        List<Vec3> points = a.PositionalArgs.Skip(1).Select(t => Parsing.Vector(t, "point (x,y,z)")).ToList();
        if (points.Count == 0)
        {
            throw new ArgumentException("give one or more points as x,y,z");
        }

        double drop = a.Get("drop") is { } d ? Parsing.Number(d, "--drop") : Core.Validation.Limits.SpyUnhurtDrop;
        MapProject p = ProjectStore.Load(folder);
        SolidQuery solid = SolidQuery.Of(p);
        var results = new SvJsonArray();
        foreach (Vec3 point in points)
        {
            bool inSolid = solid.IsSolid(point);
            double? down = inSolid ? 0 : solid.DropToFloor(point, drop);
            var o = new SvJsonObject();
            o.Add("at", ModelJson.Vec(point));
            o.Add("solid", SvJsonNode.Bool(inSolid));
            if (!inSolid)
            {
                o.Add("floor", down is { } dz ? Num(Poly.Tidy(point.Z - dz)) : SvJsonNode.Null);
                o.Add("drop", down is { } dd ? Num(Poly.Tidy(dd)) : SvJsonNode.Null);
            }

            results.Add(o);
            if (!a.Has("json"))
            {
                Console.WriteLine($"{point}: " + (inSolid ? "solid" : down is { } h ? $"open, floor {Inv(h)} below (z {Inv(point.Z - h)})" : $"open, no floor within {Inv(drop)}"));
            }
        }

        if (a.Has("json"))
        {
            var o = new SvJsonObject();
            o.Add("baked", SvJsonNode.Bool(p.Baked is not null));
            o.Add("points", results);
            PrintJson(o, a);
        }

        return Task.FromResult(0);
    }

    // ---- ose brush ---------------------------------------------------------------------------------------------

    private static Task<int> BrushAsync(CliArgs a)
    {
        EnsureServices();
        string folder = a.Positional(0) ?? throw new ArgumentException("give the project folder");
        string op = a.Positional(1)?.ToLowerInvariant() ?? throw new ArgumentException("give an op (ose brush --help)");
        EditorSession session = EditorSession.Open(folder);
        MapProject p = session.Project;
        List<string> args = a.PositionalArgs.Skip(2).ToList();
        var result = new BrushResult(op);
        IEditCommand? command = op switch
        {
            "add" => Add(p, a, args, result),
            "rebuild" => Rebuild(p, a, args, result),
            "info" => Info(p, args, result),
            "split" or "clip" => Split(p, a, op, args, result),
            "uv" => Uv(p, a, args, result),
            "paste-surface" => PasteSurface(p, a, args, result),
            _ => Shape(p, a, op, args, result),
        };

        if (command is not null && !a.Has("dry-run"))
        {
            session.Execute(command);
            session.Save();
            result.Saved = true;
        }

        if (a.Has("json") || op == "info")
        {
            PrintJson(result.ToJson(), a);
        }
        else
        {
            Console.WriteLine(result.Summary());
        }

        return Task.FromResult(0);
    }

    private static IEditCommand Add(MapProject p, CliArgs a, List<string> args, BrushResult result)
    {
        BuilderKind kind = BuilderCatalog.Get(args.FirstOrDefault() ?? throw new ArgumentException("add: give a shape kind"));
        BuilderSpec spec = SpecFromOptions(kind, a, s_brushOptions);
        Vec3 at = a.Get("at") is { } t ? Parsing.Vector(t, "--at") : Vec3.Zero;
        Rot rot = a.Get("rot") is { } r ? Parsing.Rotator(r) : Rot.Zero;
        IReadOnlyList<Brush> brushes = PiecePlacer.Create(p, spec, at, rot, ParseCsg(a.Get("csg")));
        result.Added.AddRange(brushes.Select(b => b.Id));
        return new AddObjectsCommand($"Add {kind.Kind}", brushes);
    }

    private static IEditCommand Rebuild(MapProject p, CliArgs a, List<string> args, BrushResult result)
    {
        Brush b = Find(p, args.FirstOrDefault());
        BuilderSpec stored = b.Shape.Builder ?? throw new ArgumentException($"{b.Id} is not parametric");
        BuilderSpec spec = SpecFromOptions(BuilderCatalog.Get(stored.Kind), a, s_brushOptions, stored);
        PieceEdit edit = PiecePlacer.Regenerate(p, b.Id, spec);
        result.Removed.AddRange(edit.Removed.Select(x => x.Id).Except(edit.Added.Select(x => x.Id), StringComparer.OrdinalIgnoreCase));
        result.Changed.AddRange(edit.Added.Select(x => x.Id).Intersect(edit.Removed.Select(x => x.Id), StringComparer.OrdinalIgnoreCase));
        result.Added.AddRange(edit.Added.Select(x => x.Id).Except(edit.Removed.Select(x => x.Id), StringComparer.OrdinalIgnoreCase));
        return edit;
    }

    private static IEditCommand? Info(MapProject p, List<string> args, BrushResult result)
    {
        IEnumerable<Brush> brushes = args.Count == 0 ? p.Brushes : args.Select(id => Find(p, id));
        foreach (Brush b in brushes)
        {
            ShapeReport r = ShapeAnalysis.Analyze(b.Shape);
            (Vec3 min, Vec3 max) = BrushOps.WorldBounds(b);
            var o = new SvJsonObject();
            o.Add("id", SvJsonNode.String(b.Id));
            o.Add("csg", SvJsonNode.String(b.Oper == CsgOper.Add ? "add" : "subtract"));
            o.Add("faces", SvJsonNode.Integer(r.Faces));
            o.Add("volume", SvJsonNode.Float((float)r.Volume));
            o.Add("sheet", SvJsonNode.Bool(r.IsSheet));
            o.Add("closed", SvJsonNode.Bool(r.Closed));
            o.Add("convex", SvJsonNode.Bool(r.Convex));
            o.Add("outward", SvJsonNode.Bool(r.Outward));
            o.Add("clean", SvJsonNode.Bool(r.IsCleanSolid));
            o.Add("maxPlanarError", SvJsonNode.Float((float)r.MaxPlanarError));
            AddIndexes(o, "zeroArea", r.ZeroAreaFaces);
            AddIndexes(o, "nonPlanar", r.NonPlanarFaces);
            AddIndexes(o, "nonConvexFaces", r.NonConvexFaces);
            o.Add("min", ModelJson.Vec(min));
            o.Add("max", ModelJson.Vec(max));
            if (b.Shape.Builder is { } spec)
            {
                o.Add("builder", SvJsonNode.String(spec.Kind));
            }

            result.Reports.Add(o);
        }

        return null;

        static void AddIndexes(SvJsonObject o, string key, IReadOnlyList<int> list)
        {
            if (list.Count > 0)
            {
                o.Add(key, new SvJsonArray(list.Select(i => SvJsonNode.Integer(i))));
            }
        }
    }

    private static IEditCommand Split(MapProject p, CliArgs a, string op, List<string> args, BrushResult result)
    {
        Plane3 plane = Plane3.Parse(a.Get("plane") ?? throw new ArgumentException($"{op}: give --plane nx,ny,nz,d"));
        var cmd = new CompositeBuilder($"{op} brushes");
        var taken = new HashSet<string>(p.AllRefs().Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        foreach (string id in Ids(args))
        {
            Brush b = Find(p, id);
            (Brush? front, Brush? back) = BrushOps.Split(b, plane, a.Get("cap"));
            if (op == "clip")
            {
                if (back is null)
                {
                    cmd.Delete(b);
                    result.Removed.Add(b.Id);
                }
                else if (front is not null)
                {
                    cmd.Replace(b, back);
                    result.Changed.Add(b.Id);
                }

                continue;
            }

            if (front is null || back is null)
            {
                continue; // wholly on one side: nothing to split
            }

            cmd.Replace(b, back);
            front.Id = UniqueId(b.Id, taken);
            cmd.InsertAfter(b, front);
            result.Changed.Add(b.Id);
            result.Added.Add(front.Id);
        }

        return cmd.Build(p);
    }

    private static IEditCommand Uv(MapProject p, CliArgs a, List<string> args, BrushResult result)
    {
        string mode = args.FirstOrDefault()?.ToLowerInvariant() ?? throw new ArgumentException("uv: give a mode");
        IReadOnlyList<int>? faces = Parsing.Indexes(a.Get("faces"));
        double scale = a.Get("scale") is { } s ? Parsing.Number(s, "--scale") : 1;
        double U(string key, double fallback) => a.Get(key) is { } v ? Parsing.Number(v, "--" + key) : fallback;
        Func<Brush, Brush> edit = mode switch
        {
            "default" => b => TextureOps.Apply(b, faces, (f, t) => TextureOps.AlignDefault(f, t, scale)),
            "floor" => b => TextureOps.Apply(b, faces, (f, t) => TextureOps.AlignFloor(f, t, scale)),
            "wall" => b => TextureOps.Apply(b, faces, (f, t) => TextureOps.AlignWall(f, t, scale)),
            "face" => b => TextureOps.Apply(b, faces, (f, t) => TextureOps.AlignFace(f, t, scale)),
            "cylinder" => b => TextureOps.AlignCylinder(b, faces, scale),
            "fit" => FitEdit(a, faces),
            "pan" => b => TextureOps.Apply(b, faces, (f, _) => TextureOps.Pan(f, (int)Math.Round(U("u", 0)), (int)Math.Round(U("v", 0)))),
            "rotate" => b => TextureOps.Apply(b, faces, (f, t) => TextureOps.Rotate(f, t, U("deg", 90))),
            "scale" when a.Has("absolute") => b => TextureOps.Apply(b, faces, (f, t) => TextureOps.ScaleTo(f, t, U("u", 1), U("v", U("u", 1)))),
            "scale" => b => TextureOps.Apply(b, faces, (f, _) => TextureOps.Scale(f, U("u", 1), U("v", U("u", 1)))),
            "flip-u" => b => TextureOps.Apply(b, faces, (f, _) => TextureOps.FlipU(f)),
            "flip-v" => b => TextureOps.Apply(b, faces, (f, _) => TextureOps.FlipV(f)),
            _ => throw new ArgumentException($"uv: unknown mode '{mode}'"),
        };

        return Replace(p, Ids(args.Skip(1).ToList()), $"Texture {mode}", edit, result);
    }

    private static Func<Brush, Brush> FitEdit(CliArgs a, IReadOnlyList<int>? faces)
    {
        double[] tex = Parsing.Numbers(a.Get("tex") ?? "256,256", 2, "--tex (width,height)");
        double[] rep = Parsing.Numbers(a.Get("repeat") ?? "1,1", 2, "--repeat (u,v)");
        return b => TextureOps.Apply(b, faces, (f, t) => TextureOps.Fit(f, t, tex[0], tex[1], rep[0], rep[1]));
    }

    private static IEditCommand PasteSurface(MapProject p, CliArgs a, List<string> args, BrushResult result)
    {
        string from = a.Get("from") ?? throw new ArgumentException("paste-surface: give --from Brush:face");
        int colon = from.LastIndexOf(':');
        Brush source = Find(p, colon > 0 ? from[..colon] : from);
        int face = colon > 0 ? (int)Parsing.Number(from[(colon + 1)..], "--from face") : 0;
        if (face < 0 || face >= source.Shape.Faces.Count)
        {
            throw new ArgumentException($"{source.Id} has no face {face}");
        }

        SurfaceStyle style = SurfaceStyle.Of(source.Shape.Faces[face]);
        ShapeTransform sourceT = ShapeTransform.For(source);
        IReadOnlyList<int>? faces = Parsing.Indexes(a.Get("faces"));
        bool mapping = a.Has("mapping");
        return Replace(p, Ids(args), "Paste surface",
            b => TextureOps.Apply(b, faces, (f, t) => TextureOps.Paste(f, t, style, sourceT, mapping)), result);
    }

    private static IEditCommand Shape(MapProject p, CliArgs a, string op, List<string> args, BrushResult result)
    {
        Func<Brush, Brush> edit = op switch
        {
            "flip" => b => With(b, BrushOps.Flip(b.Shape)),
            "mirror" => b => With(b, BrushOps.Mirror(b.Shape, Axis(a.Get("axis") ?? "x"))),
            "merge-faces" => b => With(b, BrushOps.MergeCoplanarFaces(b.Shape)),
            "triangulate" => b => With(b, BrushOps.Triangulate(b.Shape)),
            "hull" => b => With(b, BrushOps.ConvexHull(b.Shape)),
            "snap" => b => BrushOps.SnapVertices(b, a.Get("grid") is { } g ? Parsing.Number(g, "--grid") : p.Workspace.GridSize),
            "bake" => BrushOps.Bake,
            "recenter" => BrushOps.Recenter,
            _ => throw new ArgumentException($"unknown brush op '{op}' (ose brush --help)"),
        };

        return Replace(p, Ids(args), op, edit, result);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static IEditCommand Replace(MapProject p, IEnumerable<string> ids, string name, Func<Brush, Brush> edit, BrushResult result)
    {
        var cmd = new ReplaceObjectsCommand(name);
        foreach (string id in ids)
        {
            Brush b = Find(p, id);
            Brush after = edit(b);
            after.Id = b.Id;
            cmd.Brush(b.Clone(), after);
            result.Changed.Add(b.Id);
        }

        return cmd;
    }

    private static List<string> Ids(List<string> args) =>
        args.Count > 0 ? args : throw new ArgumentException("give one or more brush ids");

    /// <summary>A new id from the stem of <paramref name="id"/> that nothing uses yet (Room0 gives Room1, Room2...).</summary>
    private static string UniqueId(string id, HashSet<string> taken)
    {
        string stem = id.TrimEnd("0123456789".ToCharArray());
        for (int i = 0; ; i++)
        {
            string next = (stem.Length > 0 ? stem : "Brush") + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (taken.Add(next))
            {
                return next;
            }
        }
    }

    private static Brush Find(MapProject p, string? id) =>
        id is null ? throw new ArgumentException("give a brush id") : p.FindBrush(id) ?? throw new ArgumentException($"no brush '{id}'");

    private static Brush With(Brush b, BrushShape shape)
    {
        Brush o = b.Clone();
        o.Shape = shape;
        return o;
    }

    private static int Axis(string s) => s.ToLowerInvariant() switch
    {
        "x" => 0,
        "y" => 1,
        "z" => 2,
        _ => throw new ArgumentException($"--axis must be x, y or z, not '{s}'"),
    };

    /// <summary>What a brush command did, for plain and JSON output.</summary>
    private sealed class BrushResult(string op)
    {
        public List<string> Added { get; } = new();

        public List<string> Changed { get; } = new();

        public List<string> Removed { get; } = new();

        public List<SvJsonObject> Reports { get; } = new();

        public bool Saved { get; set; }

        public SvJsonObject ToJson()
        {
            var o = new SvJsonObject();
            o.Add("op", SvJsonNode.String(op));
            o.Add("saved", SvJsonNode.Bool(Saved));
            AddList(o, "added", Added);
            AddList(o, "changed", Changed);
            AddList(o, "removed", Removed);
            if (Reports.Count > 0)
            {
                o.Add("brushes", new SvJsonArray(Reports));
            }

            return o;
        }

        public string Summary() =>
            $"{op}: added {Added.Count}, changed {Changed.Count}, removed {Removed.Count}" + (Saved ? " (saved)" : " (not saved)") +
            (Added.Count > 0 ? $"\n  added {string.Join(", ", Added)}" : "");

        private static void AddList(SvJsonObject o, string key, List<string> ids)
        {
            if (ids.Count > 0)
            {
                o.Add(key, Array(ids));
            }
        }
    }

    /// <summary>Collects replace/insert/delete steps of one op into a single undoable command keeping CSG order.</summary>
    private sealed class CompositeBuilder(string name)
    {
        private readonly List<(Brush Before, Brush After)> _replace = new();
        private readonly List<(Brush After, Brush Added)> _insert = new();
        private readonly List<Brush> _delete = new();

        public void Replace(Brush before, Brush after) => _replace.Add((before, after));

        public void InsertAfter(Brush anchor, Brush added) => _insert.Add((anchor, added));

        public void Delete(Brush b) => _delete.Add(b);

        public IEditCommand Build(MapProject p)
        {
            var parts = new List<IEditCommand>();
            if (_replace.Count > 0)
            {
                var replace = new ReplaceObjectsCommand(name);
                foreach ((Brush before, Brush after) in _replace)
                {
                    replace.Brush(before.Clone(), after);
                }

                parts.Add(replace);
            }

            foreach ((Brush anchor, Brush added) in _insert)
            {
                string anchorId = anchor.Id;
                parts.Add(new DelegateCommand($"Insert {added.Id}",
                    proj => proj.Brushes.Insert(proj.Brushes.FindIndex(b => b.Id == anchorId) + 1, added),
                    proj => proj.Brushes.Remove(added)));
            }

            if (_delete.Count > 0)
            {
                parts.Add(new DeleteObjectsCommand(_delete.Select(b => ObjectRef.Brush(b.Id))));
                parts.Add(new RekeyPiecesCommand(_delete.Select(b => b.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)));
            }

            return new CompositeCommand(name, parts);
        }
    }
}
