using System.Text.Json;
using System.Text.Json.Serialization;
using OpenScct.Launcher.Install;
using OpenScct.Launcher.Versioning;

namespace OpenScct.Editor.Core.Updates;

/// <summary>
/// <c>version.json</c> at the top of a release folder, written by tools/Package-Release.ps1: which version the folder
/// holds, which top-level names the release owns (<see cref="Entries"/>) and every file under them with its size and
/// SHA-256 (<see cref="Files"/>). An update replaces exactly those names and leaves everything else in the folder alone
/// (Godot's self-contained-mode data, an override.cfg, the user's files).
/// </summary>
public sealed class InstallInfo
{
    public const string FileName = "version.json";
    public const int CurrentFormat = 1;

    /// <summary>Godot's .NET folder next to the exe (named after the app assembly and the platform).</summary>
    public const string DataFolderName = "data_OpenScctEditor_windows_x86_64";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [JsonPropertyName("format")]
    public int Format { get; set; } = CurrentFormat;

    [JsonPropertyName("product")]
    public string Product { get; set; } = EditorRelease.Product;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("commit")]
    public string? Commit { get; set; }

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("built")]
    public DateTimeOffset? Built { get; set; }

    /// <summary>The top-level names of the folder the release owns.</summary>
    [JsonPropertyName("entries")]
    public List<string> Entries { get; set; } = [];

    /// <summary>Every file of the release but version.json itself (paths with '/'), checked before a build is swapped in.</summary>
    [JsonPropertyName("files")]
    public List<InstallFile> Files { get; set; } = [];

    [JsonIgnore]
    public SemVersion? ParsedVersion => SemVersion.TryParse(Version, out SemVersion? v) ? v : null;

    public static string PathIn(string folder) => Path.Combine(folder, FileName);

    /// <summary>The folder's version.json; null when there is none or it cannot be read.</summary>
    public static InstallInfo? TryRead(string folder)
    {
        try
        {
            return File.Exists(PathIn(folder)) ? Read(folder) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The folder's version.json. Throws <see cref="InvalidDataException"/> when it is missing or bad.</summary>
    public static InstallInfo Read(string folder)
    {
        string path = PathIn(folder);
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"{path} is missing.");
        }

        InstallInfo? info;
        try
        {
            info = JsonSerializer.Deserialize<InstallInfo>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} cannot be read: {ex.Message}", ex);
        }

        if (info is null || info.ParsedVersion is null || info.Entries.Count == 0 || info.Entries.Any(e => !IsEntryName(e))
            || info.Entries.Distinct(StringComparer.OrdinalIgnoreCase).Count() != info.Entries.Count)
        {
            throw new InvalidDataException($"{path} has no valid version or entries.");
        }

        return info;
    }

    public void Write(string folder) => AtomicFile.WriteAllText(PathIn(folder), JsonSerializer.Serialize(this, Json));

    /// <summary>A name the release may own at the top of the folder: one plain name that passes
    /// <see cref="UpdateStager.IsSafeEntryPath"/> (so never the update work folder, ".." or a reserved device name).</summary>
    public static bool IsEntryName(string? name) =>
        name is not null && name.IndexOfAny(['/', '\\']) < 0 && UpdateStager.IsSafeEntryPath(name);

    /// <summary>The order a swap moves the previous build's entries out: the exe first, so from the first rename on
    /// there is no exe that could start against a half-moved folder, then Godot's data folder (the one most likely to
    /// be locked), the rest, version.json last.</summary>
    public static List<string> OutOrder(IEnumerable<string> entries) => Ordered(entries, e =>
        IsExe(e) ? 0 : IsData(e) ? 1 : IsVersionFile(e) ? 3 : 2);

    /// <summary>The order a swap moves the new build's entries in: the exe last, so it appears only once everything
    /// it needs is in place.</summary>
    public static List<string> InOrder(IEnumerable<string> entries) => Ordered(entries, e =>
        IsData(e) ? 0 : IsVersionFile(e) ? 2 : IsExe(e) ? 3 : 1);

    private static List<string> Ordered(IEnumerable<string> entries, Func<string, int> rank) =>
        entries.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(rank)
            .ThenBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsExe(string e) =>
        e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || e.EndsWith(".x86_64", StringComparison.OrdinalIgnoreCase);

    private static bool IsData(string e) => e.StartsWith("data_", StringComparison.OrdinalIgnoreCase);

    private static bool IsVersionFile(string e) => string.Equals(e, FileName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One file of a release in <see cref="InstallInfo.Files"/>.</summary>
public sealed class InstallFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}
