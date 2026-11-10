using System.Security.Cryptography;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Interop.Original;

/// <summary>Map-specific native assets, retaining only the referenced object closure and preserving native offsets.</summary>
internal sealed class NativeDependencies
{
    private sealed class Package(string folder, string extension, UPackage source, string name)
    {
        public string Folder { get; } = folder;
        public string Extension { get; } = extension;
        public UPackage Source { get; } = source;
        public string Name { get; } = name;
        public HashSet<int> Exports { get; } = [];
        public HashSet<int> Imports { get; } = [];
        public Dictionary<string, int> Paths { get; } = source.Exports.GroupBy(e => source.PathOf(e.Index + 1), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Index + 1, StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int[]> Matches { get; } = source.Exports.GroupBy(e => source.PathOf(e.Index + 1), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Index + 1).ToArray(), StringComparer.OrdinalIgnoreCase);
        public UPackageBuilder Builder { get; } = UPackageBuilder.FromPackage(source, name);
    }
    private sealed record Variant(Package Package, UExport Source, string Path, PropSet Props, int Ref);
    private readonly Dictionary<string, Package?> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _bspPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Variant> _variants = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Written { get; } = [];
    public List<Finding> Findings { get; } = [];

    public MapProject Prepare(MapProject project, string stage, ExportOptions options, string? gameRoot)
    {
        MapProject copy = ProjectSnapshot.Take(project);
        var installed = new SdcAssetImports(gameRoot);
        var sourceInstall = new SdcAssetImports(project.Source.Kind == "sdc" && project.Source.File is { } original
            ? SdcAssetBundle.DestinationRoot(original) : null);
        UeSchema? schema = UeSchema.Load(options.ContentRoot ?? EditorServices.ContentRoot);
        var resolver = LocalAssets.Resolver(project, options.ContentRoot ?? EditorServices.ContentRoot, options.ProjectFolder);
        var pending = new Queue<(Package Package, int Ref)>();
        Package? Load(string name)
        {
            if (_packages.TryGetValue(name, out Package? known)) return known;
            _packages[name] = null;
            if ((sourceInstall.PackageFile(name) ?? installed.PackageFile(name)) is not { } file) return null;
            (string Folder, string Kind, string Extension)? destination = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".ukx" => ("Animations", "Anim", ".ukx"), ".usx" => ("StaticMeshes", "Mesh", ".usx"),
                ".utc" or ".utx" => ("Textures", "Tex", ".utc"), ".uax" => ("Sounds", "Sound", ".uax"), _ => null,
            };
            // .uas files are audio stream banks, not Unreal object packages.
            if (destination is not { } target) return null;
            UPackage native = UPackage.Load(file, name);
            string safe = new(name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(18).ToArray());
            string map = new(project.Id.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(24).ToArray());
            string identity = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant())))[..8];
            string renamed = name.StartsWith(map + "_" + target.Kind + "_", StringComparison.OrdinalIgnoreCase) && name.Length <= 63
                ? name : map + "_" + target.Kind + "_" + safe + "_" + identity;
            return _packages[name] = new(target.Folder, target.Extension, native, renamed);
        }
        void Require(string path)
        {
            int dot = path.IndexOf('.');
            if (dot > 0 && Load(path[..dot]) is { } package && package.Matches.TryGetValue(path, out int[]? references))
                foreach (int reference in references) pending.Enqueue((package, reference));
        }
        foreach (var reference in RoundTripLint.AssetRefs(project, options.ProjectFolder))
        {
            bool authored = reference.Key.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)
                ? resolver.Project?.FindMesh(reference.Path) is not null : resolver.Project?.FindMaterial(reference.Path) is not null;
            if (!authored) Require(reference.Path);
        }
        UPackage? source = null;
        Package? sourcePackage = null;
        if (project.Source.Kind == "sdc" && project.Source.File is { } sourceFile && File.Exists(sourceFile))
        {
            byte[] bytes = File.ReadAllBytes(sourceFile);
            if (project.Source.Sha256 is { Length: > 0 } expected && !expected.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The source SDC changed since import. Restore the source or import its new revision before exporting.");
            source = UPackage.Load(sourceFile, project.Id);
            sourcePackage = new("", ".sdc", source, source.Name);
            _packages[source.Name] = sourcePackage;
            // Opaque source-map objects and their original import slots are preserved by the SDC writer.
            for (int i = 0; i < source.Imports.Count; i++)
                if (source.Imports[i].ClassName != "Package") Require(source.PathOf(-(i + 1)));
        }
        while (pending.TryDequeue(out var item))
        {
            Package p = item.Package; int reference = item.Ref;
            if (reference == 0) continue;
            if (reference < 0)
            {
                if (!p.Imports.Add(reference)) continue;
                UImport import = p.Source.Import(reference);
                pending.Enqueue((p, import.Outer));
                if (import.ClassName != "Package") Require(p.Source.PathOf(reference));
                continue;
            }
            if (!p.Exports.Add(reference)) continue;
            foreach (int dependency in NativeAssetReferences.Read(p.Source, p.Source.Export(reference), schema, Findings))
                pending.Enqueue((p, dependency));
        }

        string BspMaterial(string path)
        {
            if (_bspPaths.TryGetValue(path, out string? known)) return known;
            _bspPaths[path] = path; // Break material cycles.
            int dot = path.IndexOf('.');
            if (dot <= 0 || Load(path[..dot]) is not { } p || !p.Paths.TryGetValue(path, out int reference)) return path;
            UExport e = p.Source.Export(reference);
            string cls = p.Source.ClassOf(e).Package + "." + p.Source.ClassName(e);
            if (!(schema?.IsA(cls, "Engine.Material") ?? p.Source.ClassName(e) is "Shader" or "FinalBlend")) return path;
            UReader reader = p.Source.Reader(e);
            var trace = new UPropertyTrace();
            SvJsonNode NativeValue(int i)
            {
                var value = (SvJsonObject)PropValues.AssetRef(p.Source.PathOf(i));
                value.Add("\u0001native", SvJsonNode.Integer(i));
                return value;
            }
            PropSet props = PropSet.FromJson(new UPropertyReader(schema, (i, _) => NativeValue(i)) { Trace = trace }
                .ReadObject(reader, cls, e.Name));
            if (trace.Opaque || reader.Remaining != 0) return path;
            bool changed = false;
            if (p.Source.ClassName(e) == "Shader" && props.GetInt("OutputBlending") != 0)
            { props.Set("OutputBlending", SvJsonNode.Integer(0)); changed = true; }
            if (p.Source.ClassName(e) == "FinalBlend" && props.GetInt("FrameBufferBlending") != 0)
            { props.Set("FrameBufferBlending", SvJsonNode.Integer(0)); changed = true; }
            SvJsonNode Safe(SvJsonNode value)
            {
                if (PropValues.AssetPath(value) is { } child)
                {
                    string safe = BspMaterial(child);
                    if (safe != child) { changed = true; return PropValues.AssetRef(safe); }
                    return value;
                }
                if (value is SvJsonArray array) return new SvJsonArray(array.Select(Safe));
                if (value is SvJsonObject obj)
                {
                    var result = new SvJsonObject(); foreach (var property in obj) result.Add(property.Key, Safe(property.Value)); return result;
                }
                return value;
            }
            foreach (var property in props.ToArray()) props.Set(property.Key, Safe(property.Value));
            if (!changed) return path;
            string identity = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..8];
            string name = new(e.Name.Take(46).ToArray()); name += "_Bsp_" + identity;
            string relative = p.Source.PathOf(e.Outer) is { Length: > 0 } outer ? outer + "." + name : p.Source.Name + "." + name;
            var slot = new UExportSlot(name, e.ClassRef, e.Flags) { Outer = e.Outer, SuperRef = e.SuperRef };
            p.Builder.Exports.Add(slot);
            var variant = new Variant(p, e, relative, props, p.Builder.Exports.Count);
            _variants[relative] = variant;
            slot.Write = w => new UPropertyWriter(schema, value => NativeRef(p, value))
                .WriteObject(w, cls, cls, props, name);
            _bspPaths[path] = relative;
            Findings.Add(Finding.Warning("original.bspMaterialFallback", $"{path}: BSP uses an opaque material variant to avoid a native Reloaded renderer crash. Transparency/blending is disabled for that BSP material; static-mesh materials keep their original appearance."));
            return relative;
        }
        string? Material(string? path) => path is null ? null : Rename(BspMaterial(path));
        // Only CSG surfaces use the compatibility variants. Static meshes retain their original shaders.
        foreach (Brush brush in copy.Brushes)
        {
            foreach (BrushFace face in brush.Shape.Faces) face.Material = Material(face.Material);
            if (brush.Stair is { } stairs)
            {
                stairs.TreadMaterial = Material(stairs.TreadMaterial)!;
                stairs.RiserMaterial = Material(stairs.RiserMaterial)!;
                stairs.SideMaterial = Material(stairs.SideMaterial)!;
            }
        }
        // Source worlds may be retained instead of compiled from the current brushes. Change only their BSP
        // material references; actor skins and mesh material imports still point at the original shader.
        if (source is not null && sourcePackage is not null)
        {
            SdcMap map = SdcMapReader.Read(source, schema);
            int SafeRef(int reference)
            {
                if (reference == 0) return 0;
                string path = source.PathOf(reference), safe = BspMaterial(path);
                return safe == path ? reference : NativeRef(sourcePackage, PropValues.AssetRef(safe));
            }
            foreach (int modelRef in new[] { map.LevelRecord.ModelXbox, map.LevelRecord.ModelPsx2 }.Where(r => r > 0).Distinct())
            {
                UModelData model = UModelData.Read(source, source.Export(modelRef), map.Layout);
                bool changed = false;
                for (int i = 0; i < model.Surfs.Count; i++)
                {
                    FBspSurf surface = model.Surfs[i]; int safe = SafeRef(surface.Material);
                    if (safe != surface.Material)
                    {
                        model.Surfs[i] = surface with { Material = safe, Poly = surface.Poly is { } poly ? poly with { Material = safe } : null };
                        changed = true;
                    }
                }
                foreach (UBspSection section in model.Sections)
                {
                    int safe = SafeRef(section.Material);
                    changed |= section.Material != safe; section.Material = safe;
                }
                if (changed) sourcePackage.Builder.Exports[modelRef - 1].Write = w => model.Write(w, map.Layout, i => i);
            }
        }
        foreach (Package p in _packages.Values.OfType<Package>().Where(p => p.Folder.Length > 0 && p.Exports.Count > 0).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            options.Progress?.Invoke("packing " + p.Name);
            int packageClass = p.Builder.ImportClass("Core", "Package");
            for (int i = 0; i < p.Source.Exports.Count; i++)
                if (!p.Exports.Contains(i + 1)) p.Builder.Exports[i] = new UExportSlot("UnusedNative" + i, packageClass, 0) { Write = w => w.Name("None") };
            for (int i = 0; i < p.Source.Imports.Count; i++)
                if (!p.Imports.Contains(-(i + 1))) p.Builder.ReplaceImport(-(i + 1), new("Core", "Package", 0, "Core"));
            Redirect(p.Builder, p.Source);
            string file = Path.Combine(stage, "Packages", p.Folder, p.Name + p.Extension);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, UPackageBuilder.FileBytes(file, p.Builder.BuildPreservingSource(p.Source, p.Exports)));
            Files.Add(p.Name, file); Written.Add(file);
            Findings.Add(Finding.Info("original.assetSubset", $"{p.Name}: kept {p.Exports.Count} of {p.Source.Exports.Count} source objects plus {_variants.Values.Count(v => v.Package == p)} BSP compatibility variants. Unused asset payloads are omitted; native object offsets are preserved."));
        }
        if (source is not null && sourcePackage is not null && (Files.Count > 0 || _variants.Count > 0))
        {
            var builder = sourcePackage.Builder;
            Redirect(builder, source);
            string file = Path.Combine(stage, ".source", project.Id + ".sdc");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            byte[] bytes = UPackageBuilder.Pack(builder.BuildPreservingSource(source));
            File.WriteAllBytes(file, bytes);
            copy.Source.File = file;
            copy.Source.Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        void Props(PropSet props) { foreach (var property in props.ToArray()) props.Set(property.Key, Rewrite(property.Value)); }
        Props(copy.LevelInfo); Props(copy.MapInfo);
        foreach (PropSet mode in copy.MapSettings.Values) Props(mode);
        foreach (Actor actor in copy.Actors)
        {
            Props(actor.Props);
            foreach (BrushFace face in actor.Shape?.Faces ?? []) face.Material = Rename(face.Material);
        }
        foreach (Brush brush in copy.Brushes) Props(brush.Props);
        return copy;
    }

    private int NativeRef(Package p, SvJsonNode value)
    {
        if (value is SvJsonObject stored && stored["\u0001native"] is SvJsonNumber number && number.TryGetInt32(out int original)) return original;
        if (PropValues.AssetPath(value) is not { } path) return 0;
        if (!_variants.TryGetValue(path, out Variant? variant))
        {
            if (p.Paths.TryGetValue(path, out int local)) return local;
            for (int i = 0; i < p.Source.Imports.Count; i++)
                if (p.Source.PathOf(-(i + 1)).Equals(path, StringComparison.OrdinalIgnoreCase)) return -(i + 1);
            return 0;
        }
        if (variant.Package == p) return variant.Ref;
        string[] parts = path.Split('.'); int outer = p.Builder.ImportPackage(variant.Package.Name);
        foreach (string group in parts[1..^1]) outer = p.Builder.ImportPackage(group, outer);
        var cls = variant.Package.Source.ClassOf(variant.Source);
        return p.Builder.Import(cls.Package, cls.Class, outer, parts[^1]);
    }

    private void Redirect(UPackageBuilder builder, UPackage source)
    {
        for (int i = 0; i < source.Imports.Count; i++)
            if (source.Imports[i] is { Outer: 0, ClassName: "Package" } import && _packages.TryGetValue(import.Name, out Package? target)
                && target is { Exports.Count: > 0 } && builder.ImportAt(-(i + 1)).Name == import.Name)
                builder.ReplaceImport(-(i + 1), builder.ImportAt(-(i + 1)) with { Name = target.Name });
    }

    private string? Rename(string? path)
    {
        if (path is null) return null;
        int dot = path.IndexOf('.');
        if (dot <= 0) return path;
        return _packages.TryGetValue(path[..dot], out Package? package) && package is not null && package.Exports.Count > 0 ? package.Name + path[dot..] : path;
    }

    private SvJsonNode Rewrite(SvJsonNode value)
    {
        if (value is SvJsonArray array) return new SvJsonArray(array.Select(Rewrite));
        if (value is not SvJsonObject obj) return value;
        var rewritten = new SvJsonObject();
        foreach (var property in obj)
            rewritten.Add(property.Key, property.Key == "$asset" && property.Value is SvJsonString asset
                ? SvJsonNode.String(Rename(asset.Value)!) : Rewrite(property.Value));
        return rewritten;
    }
}
