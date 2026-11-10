using System.Security.Cryptography;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Interop.Original;

/// <summary>Copies referenced native asset packages under map-specific names without re-encoding their objects.
/// All asset-to-asset imports follow the renamed packages; script classes remain supplied by Reloaded.</summary>
internal sealed class NativeDependencies
{
    private sealed record Package(string File, string Folder, string Extension, UPackage Source, string Name);
    private readonly Dictionary<string, Package> _packages = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Written { get; } = [];

    public MapProject Prepare(MapProject project, string stage, ExportOptions options, string? gameRoot)
    {
        MapProject copy = ProjectSnapshot.Take(project);
        var installed = new SdcAssetImports(gameRoot);
        var sourceInstall = new SdcAssetImports(project.Source.Kind == "sdc" && project.Source.File is { } original
            ? SdcAssetBundle.DestinationRoot(original) : null);
        var resolver = LocalAssets.Resolver(project, options.ContentRoot ?? EditorServices.ContentRoot, options.ProjectFolder);
        var pending = new Queue<string>();
        foreach (var reference in RoundTripLint.AssetRefs(project, options.ProjectFolder))
        {
            bool authored = reference.Key.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)
                ? resolver.Project?.FindMesh(reference.Path) is not null : resolver.Project?.FindMaterial(reference.Path) is not null;
            if (!authored && reference.Path.Contains('.')) pending.Enqueue(reference.Path.Split('.')[0]);
        }
        UPackage? source = null;
        if (project.Source.Kind == "sdc" && project.Source.File is { } sourceFile && File.Exists(sourceFile))
        {
            byte[] bytes = File.ReadAllBytes(sourceFile);
            if (project.Source.Sha256 is { Length: > 0 } expected && !expected.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The source SDC changed since import. Restore the source or import its new revision before exporting.");
            source = UPackage.Load(sourceFile, project.Id);
            foreach (UImport import in source.Imports.Where(i => i.Outer == 0 && i.ClassName == "Package")) pending.Enqueue(import.Name);
        }
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out string? name))
        {
            if (!visited.Add(name) || (sourceInstall.PackageFile(name) ?? installed.PackageFile(name)) is not { } file) continue;
            string extension = Path.GetExtension(file).ToLowerInvariant();
            (string Folder, string Kind, string Extension)? destination = extension switch
            {
                ".ukx" => ("Animations", "Anim", ".ukx"), ".usx" => ("StaticMeshes", "Mesh", ".usx"),
                ".utc" or ".utx" => ("Textures", "Tex", ".utc"), ".uax" => ("Sounds", "Sound", ".uax"),
                ".uas" => ("Assets", "Asset", ".uas"), _ => null,
            };
            if (destination is not { } target) continue;
            UPackage native = UPackage.Load(file, name);
            string safe = new(name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(18).ToArray());
            string map = new(project.Id.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(24).ToArray());
            string renamed = map + "_" + target.Kind + "_" + safe + "_" + Convert.ToHexStringLower(SHA256.HashData(native.Data))[..8];
            _packages[name] = new(file, target.Folder, target.Extension, native, renamed);
            foreach (UImport import in native.Imports.Where(i => i.Outer == 0 && i.ClassName == "Package")) pending.Enqueue(import.Name);
        }
        foreach (Package package in _packages.Values.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            options.Progress?.Invoke("packing " + package.Name);
            var builder = UPackageBuilder.FromPackage(package.Source, package.Name);
            Redirect(builder, package.Source);
            string file = Path.Combine(stage, "Packages", package.Folder, package.Name + package.Extension);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            // Asset loaders expect an Unreal package header; the zlib container is only for SDC maps.
            File.WriteAllBytes(file, builder.BuildPreservingSource(package.Source));
            Files.Add(package.Name, file); Written.Add(file);
        }
        if (source is not null && _packages.Count > 0)
        {
            var builder = UPackageBuilder.FromPackage(source, source.Name);
            Redirect(builder, source);
            string file = Path.Combine(stage, ".source", project.Id + ".sdc");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            byte[] bytes = UPackageBuilder.Pack(builder.BuildPreservingSource(source));
            File.WriteAllBytes(file, bytes);
            copy.Source.File = file;
            copy.Source.Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        void Props(PropSet props)
        {
            foreach (var property in props.ToArray()) props.Set(property.Key, Rewrite(property.Value));
        }
        Props(copy.LevelInfo); Props(copy.MapInfo);
        foreach (PropSet mode in copy.MapSettings.Values) Props(mode);
        foreach (Actor actor in copy.Actors)
        {
            Props(actor.Props);
            foreach (BrushFace face in actor.Shape?.Faces ?? []) face.Material = Rename(face.Material);
        }
        foreach (Brush brush in copy.Brushes)
        {
            foreach (BrushFace face in brush.Shape.Faces) face.Material = Rename(face.Material);
            if (brush.Stair is { } stairs)
            {
                stairs.TreadMaterial = Rename(stairs.TreadMaterial)!;
                stairs.RiserMaterial = Rename(stairs.RiserMaterial)!;
                stairs.SideMaterial = Rename(stairs.SideMaterial)!;
            }
        }
        return copy;
    }

    private void Redirect(UPackageBuilder builder, UPackage source)
    {
        for (int i = 0; i < source.Imports.Count; i++)
            if (source.Imports[i] is { Outer: 0, ClassName: "Package" } import && _packages.TryGetValue(import.Name, out Package? target))
                builder.ReplaceImport(-(i + 1), import with { Name = target.Name });
    }

    private string? Rename(string? path)
    {
        if (path is null) return null;
        int dot = path.IndexOf('.');
        if (dot <= 0) return path;
        return _packages.TryGetValue(path[..dot], out Package? package) ? package.Name + path[dot..] : path;
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
