using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using OpenScct.Content.Materials;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Interop.Original;

namespace OpenScct.Editor.Core.Assets.Import;

public enum TextureConflict { Rename, Replace, Skip }
public sealed class TextureSetDraft
{
    public bool Enabled { get; set; } = true;
    public TextureConflict Conflict { get; set; } = TextureConflict.Rename;
    public string Name { get; set; } = "Material";
    public AuthoredMaterial Material { get; set; } = new();
    public Dictionary<string, string> Sources { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, byte[]> Generated { get; } = new(StringComparer.Ordinal);
    public List<string> Errors { get; } = [];
}
public sealed record TextureImportResult(string Path, int Width, int Height, IReadOnlyList<string> Files, bool Skipped = false);

/// <summary>Project-owned image import. Review is read-only; files are staged under content-derived names before
/// the manifest is atomically swapped. Existing images are never overwritten, including on replacement.</summary>
public static partial class TextureImporter
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> s_imports = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> s_suffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["albedo"] = "baseColor", ["basecolor"] = "baseColor", ["base_color"] = "baseColor", ["diffuse"] = "baseColor", ["color"] = "baseColor",
        ["normal"] = "normal", ["normalgl"] = "normal", ["normaldx"] = "normal", ["nrm"] = "normal", ["roughness"] = "roughness", ["rough"] = "roughness",
        ["gloss"] = "roughness", ["glossiness"] = "roughness", ["smoothness"] = "roughness", ["metallic"] = "metallic", ["metalness"] = "metallic",
        ["specular"] = "specular", ["spec"] = "specular", ["emissive"] = "emission", ["emission"] = "emission", ["height"] = "height", ["displacement"] = "height",
        ["bump"] = "height", ["ao"] = "ao", ["ambientocclusion"] = "ao", ["opacity"] = "opacity", ["alpha"] = "opacity", ["orm"] = "orm",
    };

    public static IReadOnlyList<TextureSetDraft> Preview(IEnumerable<string> files)
    {
        var sets = new Dictionary<string, TextureSetDraft>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(file), slot = "baseColor", suffix = "";
            foreach (string key in s_suffixes.Keys.OrderByDescending(k => k.Length))
                if (name.EndsWith("_" + key, StringComparison.OrdinalIgnoreCase) || name.EndsWith("-" + key, StringComparison.OrdinalIgnoreCase))
                { suffix = key; slot = s_suffixes[key]; name = name[..^(key.Length + 1)]; break; }
            string safe = AssetPaths.Safe(name);
            if (!sets.TryGetValue(safe, out TextureSetDraft? set)) sets.Add(safe, set = new() { Name = safe });
            if (slot == "orm")
            {
                Bind(set, "ao", file, "r"); Bind(set, "roughness", file, "g"); Bind(set, "metallic", file, "b");
                set.Material.Metallic = 1; set.Material.Roughness = 1;
            }
            else
            {
                Bind(set, slot, file, "r");
                set.Material.Textures[slot].Invert = suffix is "gloss" or "glossiness" or "smoothness";
                if (slot == "normal") set.Material.NormalDirectX = suffix == "normaldx";
                if (slot == "roughness") set.Material.Roughness = 1;
                if (slot == "metallic") set.Material.Metallic = 1;
                if (slot == "emission") set.Material.EmissionEnergy = 1;
                if (suffix == "bump") set.Material.BumpFromHeight = true;
            }
        }
        foreach (TextureSetDraft draft in sets.Values)
        {
            if (!draft.Sources.TryGetValue("baseColor", out string? source)) continue;
            try { draft.Material.Surface = DetectTransparency(File.ReadAllBytes(source)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { draft.Errors.Add(ex.Message); }
        }
        return sets.Values.ToList();
    }

    private static void Bind(TextureSetDraft set, string slot, string file, string channel)
    {
        if (!set.Sources.TryAdd(slot, file)) { set.Errors.Add($"More than one {slot} image was assigned; split or remove the duplicate."); return; }
        set.Material.Textures[slot] = new() { File = "pending/" + slot + ".png", Channel = channel, Srgb = slot is "baseColor" or "emission" or "detailColor" or "environment" };
    }

    public static TextureSetDraft PreviewSequence(IReadOnlyList<string> files, float fps = 12)
    {
        if (files.Count == 0) throw new ArgumentException("Choose frame images first.");
        List<string> ordered = files.OrderBy(f => SequenceNumber().Replace(Path.GetFileName(f), m => m.Value.PadLeft(12, '0')), StringComparer.OrdinalIgnoreCase).ToList();
        RgbaImage first = RgbaImage.Decode(File.ReadAllBytes(ordered[0]));
        int columns = (int)Math.Ceiling(Math.Sqrt(files.Count)), rows = (files.Count + columns - 1) / columns;
        long pixels = (long)columns * rows * first.Width * first.Height;
        if (pixels > RgbaImage.MaxPixels) throw new InvalidDataException("The frame atlas exceeds the image pixel budget; use smaller frames or fewer frames.");
        var atlas = new RgbaImage(columns * first.Width, rows * first.Height);
        for (int frame = 0; frame < ordered.Count; frame++)
        {
            RgbaImage image = frame == 0 ? first : RgbaImage.Decode(File.ReadAllBytes(ordered[frame]));
            if (image.Width != first.Width || image.Height != first.Height) throw new InvalidDataException("Every animation frame must have the same dimensions.");
            for (int y = 0; y < first.Height; y++)
                Buffer.BlockCopy(image.Pixels, y * first.Width * 4, atlas.Pixels, (((frame / columns) * first.Height + y) * atlas.Width + frame % columns * first.Width) * 4, first.Width * 4);
        }
        var draft = new TextureSetDraft { Name = AssetPaths.Safe(Regex.Replace(Path.GetFileNameWithoutExtension(ordered[0]), @"[_-]?\d+$", "")) };
        draft.Generated["baseColor"] = atlas.EncodePng();
        draft.Material.Surface = DetectTransparency(draft.Generated["baseColor"]);
        draft.Material.Textures["baseColor"] = new() { File = "pending.png", Srgb = true };
        draft.Material.Animation = new() { Columns = columns, Rows = rows, Frames = files.Count, Fps = fps };
        return draft;
    }

    public static TextureImportResult Import(string projectFolder, TextureSetDraft draft, string group = "Textures", TextureConflict conflict = TextureConflict.Rename,
        CancellationToken cancellation = default)
    {
        if (!Project.ProjectStore.IsProject(projectFolder)) throw new InvalidOperationException("Save an .osmap project before importing textures.");
        var gate = s_imports.GetOrAdd(Path.GetFullPath(projectFolder), _ => new SemaphoreSlim(1));
        gate.Wait(cancellation);
        try { return ImportLocked(projectFolder, draft, group, conflict, cancellation); }
        finally { gate.Release(); }
    }

    private static TextureImportResult ImportLocked(string projectFolder, TextureSetDraft draft, string group, TextureConflict conflict, CancellationToken cancellation)
    {
        if (draft.Errors.Count > 0) throw new InvalidDataException(string.Join("\n", draft.Errors));
        AuthoredMaterial material = draft.Material.Copy();
        var problems = material.Problems();
        if (problems.Count > 0) throw new InvalidDataException(string.Join("\n", problems));
        JsonObject manifest = ProjectAssets.For(projectFolder).Edit();
        JsonObject materials = (manifest["materials"] ??= new JsonObject()).AsObject();
        JsonObject textures = (manifest["textures"] ??= new JsonObject()).AsObject();
        string safeGroup = string.Join('.', group.Split('.').Select(AssetPaths.Safe)), stem = AssetPaths.Safe(draft.Name);
        string path = $"MyLevel.{safeGroup}.{stem}";
        bool Exists(string p) => materials.Any(k => k.Key.Equals(p, StringComparison.OrdinalIgnoreCase)) || textures.Any(k => k.Key.Equals(p, StringComparison.OrdinalIgnoreCase));
        if (Exists(path))
        {
            if (conflict == TextureConflict.Skip) return new(path, 0, 0, [], true);
            if (conflict == TextureConflict.Rename)
            { int suffix = 2; while (Exists(path)) path = $"MyLevel.{safeGroup}.{stem}_{suffix++}"; }
        }
        foreach (string slot in AuthoredMaterial.Slots) textures.Remove(path + "." + slot);
        string root = Path.Combine(Path.GetFullPath(projectFolder), "assets");
        var encoded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int width = 1, height = 1;
        foreach ((string slot, MaterialTexture binding) in material.Textures)
        {
            cancellation.ThrowIfCancellationRequested();
            byte[] bytes = draft.Generated.TryGetValue(slot, out byte[]? generated) ? generated
                : draft.Sources.TryGetValue(slot, out string? source) ? File.ReadAllBytes(source)
                : File.ReadAllBytes(SafeFile(root, binding.File));
            RgbaImage decoded = RgbaImage.Decode(bytes);
            if (slot != "environment" && (decoded.Width % material.Animation.Columns != 0 || decoded.Height % material.Animation.Rows != 0))
                throw new InvalidDataException($"{slot}: the image dimensions must divide evenly into the animation grid.");
            // Retain original PNG precision, especially 16-bit displacement maps.
            byte[] png = RgbaImage.FormatOf(bytes) == "PNG" ? bytes : decoded.EncodePng();
            string hash = Convert.ToHexStringLower(SHA256.HashData(png))[..16];
            string relative = $"textures/{hash}.png";
            binding.File = relative;
            encoded[relative] = png;
            if (slot == "baseColor") { width = decoded.Width; height = decoded.Height; }
            textures[path + "." + slot] = new JsonObject { ["file"] = relative };
        }
        if (!material.Textures.ContainsKey("baseColor"))
        {
            width = material.Animation.Columns; height = material.Animation.Rows;
            byte[] white = new RgbaImage(width, height, Enumerable.Repeat((byte)255, width * height * 4).ToArray()).EncodePng();
            string whiteFile = $"textures/material-white-{width}-{height}.png";
            encoded[whiteFile] = white;
            material.Textures["baseColor"] = new() { File = whiteFile, Srgb = true };
        }
        string baseFile = material.Textures["baseColor"].File;
        JsonObject? old = materials.FirstOrDefault(k => k.Key.Equals(path, StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
        JsonObject entry = old?.DeepClone().AsObject() ?? new();
        entry["uSize"] = width / material.Animation.Columns; entry["vSize"] = height / material.Animation.Rows;
        entry["flags"] = Flags(material);
        string actualKey = materials.Select(k => k.Key).FirstOrDefault(k => k.Equals(path, StringComparison.OrdinalIgnoreCase)) ?? path;
        materials[actualKey] = entry;
        textures[path] = new JsonObject { ["file"] = baseFile };
        var written = new List<string>();
        try
        {
            foreach ((string relative, byte[] bytes) in encoded)
            {
                cancellation.ThrowIfCancellationRequested();
                string target = SafeFile(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) { File.WriteAllBytes(target, bytes); written.Add(target); }
            }
            byte[] fallback = LegacyMaterialBake.Base(new LibraryMaterial(root, "MyLevel", path, entry["flags"]!.AsObject(), null, width / material.Animation.Columns, height / material.Animation.Rows)).EncodePng();
            string fallbackFile = "textures/" + Convert.ToHexStringLower(SHA256.HashData(fallback))[..16] + ".png";
            string fallbackTarget = SafeFile(root, fallbackFile);
            if (!File.Exists(fallbackTarget)) { File.WriteAllBytes(fallbackTarget, fallback); written.Add(fallbackTarget); }
            entry["flags"]!["texture"] = fallbackFile; encoded[fallbackFile] = fallback;
            cancellation.ThrowIfCancellationRequested();
            ProjectAssets.Save(projectFolder, manifest);
        }
        catch { foreach (string file in written) File.Delete(file); throw; }
        return new(actualKey, width, height, encoded.Keys.ToList());
    }

    public static string DetectTransparency(byte[] data) => DdsFile.AlphaOf(RgbaImage.Decode(data)) switch { AlphaKind.Mask => "masked", AlphaKind.Blend => "translucent", _ => "opaque" };

    public static JsonObject Flags(AuthoredMaterial material) => new()
    {
        ["texture"] = material.Textures.GetValueOrDefault("baseColor")?.File ?? "textures/material-white.png",
        ["masked"] = material.Surface == "masked", ["translucent"] = material.Surface == "translucent", ["two_sided"] = material.TwoSided,
        ["unlit"] = material.Unlit, ["authored"] = material.ToJson(),
    };

    public static string SafeFile(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string file = Path.GetFullPath(Path.Combine(root, relative));
        if (!file.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A texture path escapes its asset folder.");
        return file;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex SequenceNumber();
}
