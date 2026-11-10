using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Interop.Original;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>Assets absent from the destination install, written as one native, map-specific package.
/// No project or installed package is edited. Standalone SDC exports use immutable content-hashed names;
/// a folder export uses stable names and publishes the complete folder atomically.</summary>
public sealed class SdcAssetBundle
{
    private sealed record Asset(string Relative, string ClassName, int Ref, Part Part);
    private sealed class Part(string name, string folder, string extension)
    {
        public UPackageBuilder Builder { get; } = new(name);
        public Dictionary<string, int> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Name { get; set; } = name;
        public string Folder { get; } = folder;
        public string Extension { get; } = extension;
    }
    public sealed record Output(string File, byte[] Data);
    private readonly Part _main;
    private readonly Part _textures;
    private readonly Part _sounds;
    public List<Output> Outputs { get; } = [];
    private readonly Dictionary<string, Asset> _assets = new(StringComparer.OrdinalIgnoreCase);
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
    private readonly bool _portable;
    private readonly IReadOnlyDictionary<string, string>? _provided;

    private SdcAssetBundle(MapProject project, string name, string? gameRoot, ExportOptions options, UeSchema? schema, List<Finding> findings)
    {
        _project = project; Name = name; _schema = schema; _findings = findings;
        _main = new Part(name, "StaticMeshes", ".usx");
        _builder = _main.Builder;
        _stock = new SdcAssetImports(gameRoot, options.NativePackages);
        _portable = options.PortableAssets;
        _textures = _portable ? new Part(name + "_Textures", "Textures", ".utc") : _main;
        _sounds = _portable ? new Part(name + "_Sounds", "Sounds", ".uax") : _main;
        _provided = options.NativePackages;
        _resolver = LocalAssets.Resolver(project, options.ContentRoot ?? EditorServices.ContentRoot, options.ProjectFolder);
        _catalog = AssetCatalog.For(options.ContentRoot ?? EditorServices.ContentRoot, options.ProjectFolder);
        _source = Source(project);
        _embedded = _source?.Exports.Select(e => _source.PathOf(e.Index + 1)[(project.Id.Length + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new(StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; private set; }
    public byte[] Package { get; private set; } = [];
    public int Count => _assets.Count;
    public string? FilePath { get; private set; }

    public static SdcAssetBundle Prepare(MapProject project, string path, string? gameRoot, ExportOptions options, UeSchema? schema, List<Finding> findings)
    {
        string map = Path.GetFileNameWithoutExtension(path);
        string stem = new(map.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(options.PortableAssets ? 28 : 40).ToArray());
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
        string? root = DestinationRoot(path);
        foreach (Part part in new[] { bundle._textures, bundle._sounds, bundle._main }.Distinct())
        {
            if (part.Builder.Exports.Count == 0) continue;
            byte[] data = part.Builder.Build();
            string previous = part.Name;
            if (!options.PortableAssets) part.Name += "_" + Convert.ToHexStringLower(SHA256.HashData(data))[..12];
            // Mesh material objrefs already target imports; rename their package roots after hashing textures.
            if (part != bundle._main)
                for (int i = 0; i < bundle._builder.Imports.Count; i++)
                    if (bundle._builder.Imports[i] is { Outer: 0, ClassName: "Package" } import && import.Name == previous)
                        bundle._builder.ReplaceImport(-(i + 1), import with { Name = part.Name });
            UPackage check = UPackage.Parse(part.Name, data);
            foreach (Asset asset in bundle._assets.Values.Where(a => a.Part == part))
                if (!check.Exports.Any(e => check.PathOf(e.Index + 1).Equals(part.Name + "." + asset.Relative, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"The generated asset package is missing {asset.Relative}.");
            string file = root is null ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, part.Name + part.Extension)
                : Path.Combine(root, "Packages", part.Folder, part.Name + part.Extension);
            bundle.Outputs.Add(new(file, UPackageBuilder.FileBytes(file, data)));
            if (part == bundle._main) { bundle.Name = part.Name; bundle.Package = data; bundle.FilePath = file; }
            if (root is null) findings.Add(Finding.Warning("sdc.assetDestination", $"The asset package is saved beside the map as {Path.GetFileName(file)}. Copy it into Packages\\{part.Folder} when installing the SDC in Reloaded."));
        }
        findings.Add(Finding.Info("sdc.assetPackage", $"{bundle.Count} assets and their required materials are included in {string.Join(", ", bundle.Outputs.Select(o => o.File))}. Keep these packages with the exported map when sharing it."));
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
        int outer = destination.ImportPackage(asset.Part.Name);
        string[] parts = asset.Relative.Split('.');
        foreach (string group in parts[..^1]) outer = destination.ImportPackage(group, outer);
        return destination.Import("Engine", asset.ClassName, outer, parts[^1]);
    }

    private int Require(string path, string key, ObjectRef owner, string? image = null, bool masked = false, bool alpha = false)
    {
        if (_assets.TryGetValue(path, out Asset? existing)) return existing.Part == _main ? existing.Ref : Import(_builder, path);
        // Project assets override the source map. Otherwise retain embedded source assets without re-encoding.
        bool projectAsset = key.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)
            ? _resolver.Project?.FindMesh(path) is not null : _resolver.Project?.FindMaterial(path) is not null;
        bool local = RoundTripLint.IsLocalPath(_project, path);
        string relative = path.Contains('.') ? path[(path.IndexOf('.') + 1)..] : path;
        if (!projectAsset && local && _embedded.Contains(relative)) return 0;
        if (!projectAsset && (!_portable || _provided?.ContainsKey(path.Split('.')[0]) == true) && _stock.Find(path) is not null)
            return _stock.Import(_builder, path);
        if (!_attempted.Add(path)) return 0;
        bool textureReference = key.Equals("Material", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Texture", StringComparison.OrdinalIgnoreCase) || key.Contains("Skin", StringComparison.OrdinalIgnoreCase);
        try
        {
            LibraryMesh? mesh = key.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) ? _resolver.FindMesh(path) : null;
            if (mesh is not null) return AddMesh(path, mesh, owner);
            LibraryMaterial? material = _resolver.FindMaterial(path);
            AssetEntry? entry = RoundTripLint.FindAsset(_project, _catalog, path, key);
            textureReference |= material is not null || entry?.Kind == AssetKind.Material;
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
            if (textureReference)
            {
                _findings.Add(Finding.Warning("sdc.textureMissing", $"{path}: {ex.Message} A checkerboard placeholder texture is packaged instead; replace it when the original texture is available.", owner));
                return Add(path, "Texture", UTextureWriter.Placeholder(_schema));
            }
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
        Part part = cls == "Texture" ? _textures : cls == "Sound" ? _sounds : _main;
        int outer = Group(string.Join('.', parts[..^1]), part);
        int classRef = part.Builder.ImportClass("Engine", cls);
        part.Builder.Exports.Add(new UExportSlot(parts[^1], classRef, 0x000F0004) { Outer = outer, Write = write });
        int objref = part.Builder.Exports.Count;
        _assets[path] = new Asset(path, cls, objref, part);
        return part == _main ? objref : Import(_builder, path);
    }

    private int Group(string path, Part? part = null)
    {
        part ??= _main;
        int outer = 0; string groupPath = "";
        foreach (string group in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            groupPath = groupPath.Length == 0 ? group : groupPath + "." + group;
            if (!part.Groups.TryGetValue(groupPath, out int groupRef))
            {
                part.Builder.Exports.Add(new UExportSlot(group, part.Builder.ImportClass("Core", "Package"), 0x000F0004) { Outer = outer, Write = w => w.Name("None") });
                part.Groups[groupPath] = groupRef = part.Builder.Exports.Count;
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
