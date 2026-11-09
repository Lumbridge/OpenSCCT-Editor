using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Interop.Original;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>Assets absent from the destination install, written as one native, map-specific package.
/// No project or installed package is edited. The content hash gives each revision its own file, so publishing
/// a new dependency cannot invalidate an existing map if the subsequent map save fails.</summary>
public sealed class SdcAssetBundle
{
    private sealed record Asset(string Relative, string ClassName, int Ref);
    private readonly Dictionary<string, Asset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly UPackageBuilder _builder;
    private readonly SdcAssetImports _stock;
    private readonly AssetResolver _resolver;
    private readonly AssetCatalog _catalog;
    private readonly MapProject _project;
    private readonly UeSchema? _schema;
    private readonly List<Finding> _findings;
    private readonly HashSet<string> _embedded;
    private readonly UPackage? _source;
    private readonly HashSet<string> _redirectPackages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _attempted = new(StringComparer.OrdinalIgnoreCase);

    private SdcAssetBundle(MapProject project, string name, string? gameRoot, ExportOptions options, UeSchema? schema, List<Finding> findings)
    {
        _project = project; Name = name; _schema = schema; _findings = findings;
        _builder = new UPackageBuilder(name);
        _stock = new SdcAssetImports(gameRoot);
        _resolver = LocalAssets.Resolver(project, options.ContentRoot ?? EditorServices.ContentRoot, options.ProjectFolder);
        _catalog = AssetCatalog.For(options.ContentRoot ?? EditorServices.ContentRoot, options.ProjectFolder);
        _source = Source(project);
        _embedded = _source?.Exports.Select(e => _source.PathOf(e.Index + 1)[(project.Id.Length + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new(StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; private set; }
    public byte[] Package { get; private set; } = [];
    public int Count => _assets.Values.Select(a => a.Ref).Distinct().Count();
    public string? FilePath { get; private set; }

    public static SdcAssetBundle Prepare(MapProject project, string path, string? gameRoot, ExportOptions options, UeSchema? schema, List<Finding> findings)
    {
        string map = Path.GetFileNameWithoutExtension(path);
        string stem = new(map.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(40).ToArray());
        if (stem.Length == 0) stem = "Map";
        var bundle = new SdcAssetBundle(project, stem + "_Assets", gameRoot, options, schema, findings);
        foreach (var reference in RoundTripLint.AssetRefs(project, options.ProjectFolder).Concat(bundle.SourceAssets())
            .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(reference.Path) || reference.Path.Equals(Starter.DefaultMaterial, StringComparison.OrdinalIgnoreCase)) continue;
            bundle.Require(reference.Path, reference.Key, reference.Owner);
        }
        if (bundle.Count == 0 || findings.Any(f => f.Severity == Severity.Error)) return bundle;
        if (bundle._source is { } source)
        {
            foreach (string package in bundle._assets.Keys.Select(p => p.Split('.')[0]).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!bundle._stock.HasPackage(package) && !source.Imports.Select((import, index) => (Import: import, Path: source.PathOf(-(index + 1))))
                    .Any(i => i.Import.ClassName != "Package" && i.Path.Split('.')[0].Equals(package, StringComparison.OrdinalIgnoreCase) && !bundle._assets.ContainsKey(i.Path)))
                    bundle._redirectPackages.Add(package);
            // Even unused source group imports must resolve after the old missing package is redirected.
            foreach ((UImport import, int index) in source.Imports.Select((i, index) => (i, index)))
            {
                string imported = source.PathOf(-(index + 1));
                if (import.ClassName == "Package" && import.Outer != 0 && bundle._redirectPackages.Contains(imported.Split('.')[0]))
                    bundle.Group(imported);
            }
        }
        bundle.Package = bundle._builder.Build();
        bundle.Name += "_" + Convert.ToHexStringLower(SHA256.HashData(bundle.Package))[..12];
        // Read all tables back before making the map depend on this file.
        UPackage check = UPackage.Parse(bundle.Name, bundle.Package);
        foreach (Asset asset in bundle._assets.Values)
            if (!check.Exports.Any(e => check.PathOf(e.Index + 1).Equals(bundle.Name + "." + asset.Relative, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"The generated asset package is missing {asset.Relative}.");
        string? root = DestinationRoot(path);
        if (root is null)
        {
            findings.Add(Finding.Error("sdc.assetDestination", "This export needs an asset package. Save the SDC in the target installation's Packages\\Maps or Packages\\MapsEd folder so its assets can be installed alongside it."));
            return bundle;
        }
        bundle.FilePath = Path.Combine(root, "Packages", "StaticMeshes", bundle.Name + ".usx");
        findings.Add(Finding.Info("sdc.assetPackage", $"{bundle.Count} assets and their required materials are included in {bundle.FilePath}. Keep this package with the exported map when sharing it. Assets already supplied by the target installation remain external dependencies."));
        return bundle;
    }

    /// <summary>Only recognised Maps/MapsEd folders identify the installation to write alongside.</summary>
    public static string? DestinationRoot(string path)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return (dir.Name.Equals("Maps", StringComparison.OrdinalIgnoreCase) || dir.Name.Equals("MapsEd", StringComparison.OrdinalIgnoreCase))
            && dir.Parent?.Name.Equals("Packages", StringComparison.OrdinalIgnoreCase) == true ? dir.Parent.Parent?.FullName : null;
    }

    public bool Contains(string path) => _assets.ContainsKey(path);

    public int RemappedImport(UPackageBuilder destination, string path, UImport original)
    {
        if (Contains(path)) return Import(destination, path);
        if (original.ClassName != "Package" || !_redirectPackages.Contains(path.Split('.')[0])) return 0;
        return original.Outer == 0 ? destination.ImportPackage(Name) : ImportGroup(destination, path.Split('.'));
    }

    private int ImportGroup(UPackageBuilder destination, IEnumerable<string> groups)
    {
        int outer = destination.ImportPackage(Name);
        foreach (string group in groups) outer = destination.ImportPackage(group, outer);
        return outer;
    }

    public int Import(UPackageBuilder destination, string path)
    {
        if (!_assets.TryGetValue(path, out Asset? asset)) return 0;
        int outer = destination.ImportPackage(Name);
        string[] parts = asset.Relative.Split('.');
        foreach (string group in parts[..^1]) outer = destination.ImportPackage(group, outer);
        return destination.Import("Engine", asset.ClassName, outer, parts[^1]);
    }

    private int Require(string path, string key, ObjectRef owner, string? image = null, bool masked = false, bool alpha = false)
    {
        if (_assets.TryGetValue(path, out Asset? existing)) return existing.Ref;
        // Project assets override the source map. Otherwise retain embedded source assets without re-encoding.
        bool projectAsset = key.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)
            ? _resolver.Project?.FindMesh(path) is not null : _resolver.Project?.FindMaterial(path) is not null;
        bool local = RoundTripLint.IsLocalPath(_project, path);
        string relative = path.Contains('.') ? path[(path.IndexOf('.') + 1)..] : path;
        if (!projectAsset && local && _embedded.Contains(relative)) return 0;
        if (!projectAsset && _stock.Find(path) is not null) return _stock.Import(_builder, path);
        if (!_attempted.Add(path)) return 0;
        try
        {
            LibraryMesh? mesh = key.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) ? _resolver.FindMesh(path) : null;
            if (mesh is not null) return AddMesh(path, mesh, owner);
            LibraryMaterial? material = _resolver.FindMaterial(path);
            AssetEntry? entry = RoundTripLint.FindAsset(_project, _catalog, path, key);
            if (entry?.Kind == AssetKind.Sound)
            {
                if (entry.File is null || !File.Exists(entry.File)) throw new InvalidDataException("The source sound file is missing. Restore it or choose an installed sound.");
                return Add(path, "Sound", USoundWriter.Prepare(entry.File, AssetPaths.Split(path).Name));
            }
            image ??= material?.Texture is { } texture ? Path.Combine(material.RenderDir, texture) : entry?.File ?? entry?.Texture;
            if (image is null || !File.Exists(image))
                throw new InvalidDataException("No source image or static mesh is available. Restore the source asset or choose an asset installed in the target game.");
            bool Flag(string flag) => material?.Flags[flag] is JsonValue v && v.TryGetValue(out bool value) && value;
            Action<UWriter> write = UTextureWriter.Prepare(image, masked || Flag("masked"), alpha || Flag("translucent"), _schema);
            int result = Add(path, "Texture", write);
            if (Flag("two_sided") || Flag("unlit") || material?.Flags["shader"] is not null)
                _findings.Add(Finding.Warning("sdc.assetMaterial", $"{path} is packaged as its base texture. Its shader/two-sided/unlit settings need checking in RE+.", owner));
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            _findings.Add(Finding.Error("sdc.assetMissing", $"Cannot package {path}: {ex.Message}", owner));
            return 0;
        }
    }

    private int Add(string path, string cls, Action<UWriter> write)
    {
        // Keep the original package as a group: same-named assets from different packs never collide.
        string[] parts = path.Split('.');
        if (parts.Any(p => p.Length == 0 || p.Length > 63 || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')))
            throw new ArgumentException("The asset path must use letters, digits, underscores or hyphens in each group/name.");
        int outer = Group(string.Join('.', parts[..^1]));
        int classRef = _builder.ImportClass("Engine", cls);
        _builder.Exports.Add(new UExportSlot(parts[^1], classRef, 0x000F0004) { Outer = outer, Write = write });
        int objref = _builder.Exports.Count;
        _assets[path] = new Asset(path, cls, objref);
        return objref;
    }

    private int Group(string path)
    {
        int outer = 0; string groupPath = "";
        foreach (string group in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            groupPath = groupPath.Length == 0 ? group : groupPath + "." + group;
            if (!_groups.TryGetValue(groupPath, out int groupRef))
            {
                _builder.Exports.Add(new UExportSlot(group, _builder.ImportClass("Core", "Package"), 0x000F0004) { Outer = outer, Write = w => w.Name("None") });
                _groups[groupPath] = groupRef = _builder.Exports.Count;
            }
            outer = groupRef;
        }
        return outer;
    }

    private int AddMesh(string path, LibraryMesh mesh, ObjectRef owner)
    {
        List<MeshSection> sections = MeshSections.Read(mesh);
        int[] materials = sections.Select((s, index) => s.Material is { Length: > 0 } material
            ? Require(material, "Material", owner, s.Texture, s.AlphaMode == "MASK", s.AlphaMode == "BLEND")
            : s.Texture is { } image ? Require(path + "_Material" + index, "Material", owner, image, s.AlphaMode == "MASK", s.AlphaMode == "BLEND") : 0).ToArray();
        List<MeshSection>? collision = mesh.CollisionMesh is { } ci ? MeshSections.Read(mesh.GlbPath, mesh.RenderDir, ci) : null;
        return Add(path, "StaticMesh", UStaticMeshWriter.Prepare(sections, collision, materials));
    }

    private IEnumerable<(string Path, string Key, ObjectRef Owner)> SourceAssets()
    {
        if (_source is null) yield break;
        var owner = ObjectRef.Actor(Interop.T3d.T3dExport.LevelInfoName(_project));
        for (int i = 0; i < _source.Imports.Count; i++)
        {
            UImport import = _source.Imports[i];
            string cls = import.ClassPackage + "." + import.ClassName;
            bool mesh = import.ClassName == "StaticMesh", sound = import.ClassName == "Sound";
            if (mesh || sound || import.ClassName is "Texture" or "Shader" or "FinalBlend" or "TexModifier" or "Cubemap"
                || _schema?.IsA(cls, "Engine.Material") == true)
                yield return (_source.PathOf(-(i + 1)), mesh ? "StaticMesh" : sound ? "AmbientSound" : "Material", owner);
        }
    }

    private static UPackage? Source(MapProject project)
    {
        if (project.Source.Kind != "sdc" || project.Source.File is not { } file || !File.Exists(file)) return null;
        byte[] bytes = File.ReadAllBytes(file);
        if (project.Source.Sha256 is { Length: > 0 } sha && !sha.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
            return null;
        return UPackage.Load(file, project.Id);
    }
}
