using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Assets.Import;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using static OpenScct.Editor.Cli.Commands.GeometryCliSupport;

namespace OpenScct.Editor.Cli.Commands;

/// <summary><c>ose assets</c>, <c>ose asset</c> and the asset edits: the catalogue of materials, meshes, textures and
/// sounds an authored map can use, mesh import, placing meshes and applying materials.</summary>
internal static class AssetCommands
{
    [CliProvider]
    private static IEnumerable<CliCommand> Commands() =>
    [
        new("assets", "assets [--kind material|mesh|texture|sound] [--filter words] [--origin Map|shared|MyLevel] [--project p.osmap] [--limit 200] [--json]",
            "Lists the assets an authored map can use: the project's own (with --project), the converted maps' materials and meshes,\n" +
            "and the shared textures and sounds. --filter matches every word (any case) in the name, package, group, origin or English gloss of French names, or the kind.", ListAsync),
        new("asset", "asset <path> [--kind k] [--project p.osmap] [--json]",
            "Details of one asset: file, size or bounds, materials, the converted maps that use it, and its uses in --project.", ShowAsync),
        new("asset-import", "asset-import <project> <mesh.obj|.gltf|.glb> [--name N] [--group Meshes] [--scale 1] [--zup]\n" +
            "      [--collision parts|hull|box|mesh] [--overwrite] [--place x,y,z] [--json]",
            "Imports a mesh into the project's assets as MyLevel.<group>.<name>, with a generated collision hull. OBJ is read in\n" +
            "Unreal units (Y up unless --zup), glTF in metres. PNG, JPEG, BMP and WebP base-colour textures are imported.\n" +
            "--place also adds a StaticMeshActor standing on that point.", ImportAsync),
        new("asset-place", "asset-place <project> <mesh path> --at x,y,z [--rot p,y,r] [--json]",
            "Adds a StaticMeshActor showing the mesh, its bottom on the point (one undo step), and saves the project.", PlaceAsync),
        new("asset-apply", "asset-apply <project> <material path> (--brushes id,id | --all) [--json]",
            "Sets every face of the brushes to the material (one undo step), and saves the project.", ApplyAsync),
    ];

    private static AssetCatalog Catalog(CliArgs a)
    {
        EnsureServices();
        return AssetCatalog.For(a.Get("content") ?? EditorServices.ContentRoot, a.Get("project"));
    }

    private static Task<int> ListAsync(CliArgs a)
    {
        AssetCatalog cat = Catalog(a);
        int limit = a.GetInt("limit", 200);
        List<AssetEntry> found = cat.Search(a.Get("filter"), AssetKinds.Parse(a.Get("kind")), a.Get("origin")).ToList();
        if (a.Has("json"))
        {
            var o = new SvJsonObject();
            o.Add("source", SvJsonNode.String(cat.Description));
            o.Add("total", SvJsonNode.Integer(found.Count));
            o.Add("assets", new SvJsonArray(found.Take(limit).Select(e => (SvJsonNode)Brief(e))));
            PrintJson(o, a);
            return Task.FromResult(0);
        }

        Console.WriteLine($"{found.Count} assets ({cat.Description}){(found.Count > limit ? $", first {limit}" : "")}");
        foreach (AssetEntry e in found.Take(limit))
        {
            Console.WriteLine($"  {AssetKinds.Name(e.Kind),-8} {e.Path,-56} {e.Origin,-8} {AssetQueries.Facts(e)}{(e.Gloss is { } g ? $"  \"{g}\"" : "")}");
        }

        return Task.FromResult(0);
    }

    private static Task<int> ShowAsync(CliArgs a)
    {
        string path = a.Positional(0) ?? throw new ArgumentException("give an asset path (Package.Group.Name)");
        AssetCatalog cat = Catalog(a);
        AssetKind? kind = AssetKinds.Parse(a.Get("kind"));
        AssetEntry e = (kind is { } k ? cat.Find(k, path) : cat.Find(path)) ?? throw new ArgumentException($"no asset '{path}' ({cat.Description})");
        List<AssetUse> uses = a.Get("project") is { } project ? AssetEdits.Usages(Core.Project.ProjectStore.Load(project), e.Path) : [];
        if (a.Has("json"))
        {
            SvJsonObject o = Brief(e);
            if (e.Materials.Count > 0)
            {
                o.Add("materials", Array(e.Materials));
            }

            var used = new SvJsonObject();
            foreach ((string map, int n) in e.UsedIn.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            {
                used.Add(map, SvJsonNode.Integer(n));
            }

            o.Add("usedIn", used);
            o.Add("usages", new SvJsonArray(uses.Select(u => (SvJsonNode)new SvJsonArray([SvJsonNode.String(u.Object.ToString()), SvJsonNode.String(u.How)]))));
            PrintJson(o, a);
            return Task.FromResult(0);
        }

        Console.WriteLine($"{e.Kind} {e.Path}");
        Console.WriteLine($"  origin   {e.Origin}");
        Console.WriteLine($"  file     {e.File ?? "(none)"}");
        if (e.Gloss is { } gloss)
        {
            Console.WriteLine($"  english  {gloss}");
        }

        Console.WriteLine($"  facts    {AssetQueries.Facts(e)}");
        if (e.Materials.Count > 0)
        {
            Console.WriteLine($"  materials {string.Join(", ", e.Materials)}");
        }

        if (e.UsedIn.Count > 0)
        {
            Console.WriteLine($"  used in {e.UsedIn.Count} maps: " + string.Join(", ",
                e.UsedIn.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => e.Kind == AssetKind.Mesh ? $"{kv.Key} ({kv.Value})" : kv.Key)));
        }

        if (a.Has("project"))
        {
            Console.WriteLine(uses.Count == 0 ? "  not used in the project" : $"  {uses.Count} uses in the project:");
            foreach (AssetUse u in uses)
            {
                Console.WriteLine($"    {u.Object}  {u.How}");
            }
        }

        return Task.FromResult(0);
    }

    private static Task<int> ImportAsync(CliArgs a)
    {
        string project = a.Positional(0) ?? throw new ArgumentException("give the project (.osmap folder)");
        string file = a.Positional(1) ?? throw new ArgumentException("give the mesh file (.obj, .gltf or .glb)");
        var options = new MeshImportOptions
        {
            Name = a.Get("name"),
            Group = a.Get("group") ?? "Meshes",
            Scale = a.Get("scale") is { } s ? Parsing.Number(s, "--scale") : 1,
            ZUp = a.Has("zup"),
            Collision = a.Get("collision") is { } c
                ? Enum.TryParse(c, ignoreCase: true, out CollisionMode m) ? m : throw new ArgumentException($"--collision must be parts, hull, box or mesh, not '{c}'")
                : CollisionMode.Parts,
            Overwrite = a.Has("overwrite"),
        };
        MeshImportResult r = MeshImporter.Import(project, file, options);
        string? placed = null;
        if (a.Get("place") is { } at)
        {
            placed = Place(project, r.Path, Parsing.Vector(at, "--place"), Rot.Zero, (r.BoundsMin, r.BoundsMax));
        }

        if (a.Has("json"))
        {
            var o = new SvJsonObject();
            o.Add("path", SvJsonNode.String(r.Path));
            o.Add("file", SvJsonNode.String(r.File));
            o.Add("triangles", SvJsonNode.Integer(r.Triangles));
            o.Add("collisionTriangles", SvJsonNode.Integer(r.CollisionTriangles));
            o.Add("hulls", SvJsonNode.Integer(r.Hulls));
            o.Add("bounds", new SvJsonArray([Vec(r.BoundsMin), Vec(r.BoundsMax)]));
            o.Add("materials", Array(r.Materials));
            o.Add("notes", Array(r.Notes));
            if (placed is not null)
            {
                o.Add("placed", SvJsonNode.String(placed));
            }

            PrintJson(o, a);
            return Task.FromResult(0);
        }

        Console.WriteLine($"Imported {r.Path}: {r.Triangles} triangles, collision {r.CollisionTriangles} triangles in {r.Hulls} hull(s)");
        Console.WriteLine($"  file   {r.File}");
        Console.WriteLine($"  bounds {Inv(r.BoundsMin.X)},{Inv(r.BoundsMin.Y)},{Inv(r.BoundsMin.Z)} .. {Inv(r.BoundsMax.X)},{Inv(r.BoundsMax.Y)},{Inv(r.BoundsMax.Z)}");
        Console.WriteLine($"  materials {string.Join(", ", r.Materials)}");
        foreach (string n in r.Notes)
        {
            Console.WriteLine($"  note: {n}");
        }

        if (placed is not null)
        {
            Console.WriteLine($"  placed {placed}");
        }

        return Task.FromResult(0);
    }

    private static Task<int> PlaceAsync(CliArgs a)
    {
        string project = a.Positional(0) ?? throw new ArgumentException("give the project (.osmap folder)");
        string mesh = a.Positional(1) ?? throw new ArgumentException("give the mesh path");
        Vec3 at = Parsing.Vector(a.Get("at") ?? throw new ArgumentException("give --at x,y,z"), "--at");
        Rot rot = a.Get("rot") is { } r ? Parsing.Rotator(r) : Rot.Zero;
        AssetCatalog cat = AssetCatalog.For(EnsureContent(), project);
        AssetEntry entry = cat.Find(AssetKind.Mesh, mesh) ?? throw new ArgumentException($"no static mesh '{mesh}' ({cat.Description})");
        string id = Place(project, entry.Path, at, rot, (entry.BoundsMin, entry.BoundsMax));
        Console.WriteLine(a.Has("json") ? CanonicalJson.ToCanonicalString(Obj(("placed", id), ("mesh", entry.Path))) : $"Placed {id} ({entry.Path})");
        return Task.FromResult(0);
    }

    private static Task<int> ApplyAsync(CliArgs a)
    {
        string project = a.Positional(0) ?? throw new ArgumentException("give the project (.osmap folder)");
        string material = a.Positional(1) ?? throw new ArgumentException("give the material path");
        EditorSession s = EditorSession.Open(project);
        IEnumerable<ObjectRef> refs = a.Has("all")
            ? s.Project.Brushes.Select(b => ObjectRef.Brush(b.Id))
            : (a.Get("brushes") ?? throw new ArgumentException("give --brushes id,id or --all")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ObjectRef.Brush);
        AssetCatalog cat = AssetCatalog.For(EnsureContent(), project);
        if (cat.Find(AssetKind.Material, material) is null)
        {
            Console.Error.WriteLine($"ose asset-apply: warning: no material '{material}' in the catalogue; it will build as a grid");
        }

        ReplaceObjectsCommand cmd = AssetEdits.ApplyMaterial(s.Project, refs, material) ?? throw new ArgumentException("none of those brushes exist");
        s.Execute(cmd);
        s.Save();
        Console.WriteLine(a.Has("json") ? CanonicalJson.ToCanonicalString(Obj(("material", material), ("command", cmd.Name))) : $"{cmd.Name}: done");
        return Task.FromResult(0);
    }

    private static string Place(string project, string mesh, Vec3 at, Rot rot, (Vec3, Vec3) bounds)
    {
        EditorSession s = EditorSession.Open(project);
        Actor actor = AssetEdits.MeshActor(s.Project, mesh, at, bounds, rot);
        s.Execute(AssetEdits.PlaceMesh(actor));
        s.Save();
        return actor.Id;
    }

    private static string? EnsureContent()
    {
        EnsureServices();
        return EditorServices.ContentRoot;
    }

    private static SvJsonObject Brief(AssetEntry e)
    {
        var o = new SvJsonObject();
        o.Add("kind", SvJsonNode.String(AssetKinds.Name(e.Kind)));
        o.Add("path", SvJsonNode.String(e.Path));
        o.Add("origin", SvJsonNode.String(e.Origin));
        if (e.Gloss is { } g)
        {
            o.Add("english", SvJsonNode.String(g));
        }

        switch (e.Kind)
        {
            case AssetKind.Mesh:
                o.Add("triangles", SvJsonNode.Integer(e.Triangles));
                o.Add("size", Vec(e.Size));
                o.Add("bounds", new SvJsonArray([Vec(e.BoundsMin), Vec(e.BoundsMax)]));
                o.Add("uses", SvJsonNode.Integer(e.UsedIn.Values.Sum()));
                break;
            case AssetKind.Material or AssetKind.Texture:
                o.Add("size", new SvJsonArray([SvJsonNode.Integer(e.Width), SvJsonNode.Integer(e.Height)]));
                break;
            case AssetKind.Sound:
                o.Add("seconds", Num(Math.Round(e.Duration, 2)));
                if (e.SoundKind is { } sk)
                {
                    o.Add("soundKind", SvJsonNode.String(sk));
                }

                break;
        }

        if (e.File is { } f)
        {
            o.Add("file", SvJsonNode.String(f));
        }

        return o;
    }

    private static SvJsonArray Vec(Vec3 v) => new([Num(v.X), Num(v.Y), Num(v.Z)]);

    private static SvJsonObject Obj(params (string Key, string Value)[] items)
    {
        var o = new SvJsonObject();
        foreach ((string k, string v) in items)
        {
            o.Add(k, SvJsonNode.String(v));
        }

        return o;
    }
}
