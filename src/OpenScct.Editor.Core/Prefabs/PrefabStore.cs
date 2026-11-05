using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Prefabs;

/// <summary>
/// Finds and writes prefabs. Lookup by name goes: the map's own <c>&lt;map&gt;.osmap/prefabs/&lt;name&gt;.json</c>, then the
/// user library (<c>OSE_PREFABS</c>, else <c>prefabs</c> in <see cref="UserFolders.Settings"/>), then the starter library built
/// into the editor.
/// </summary>
public static class PrefabStore
{
    private const string ResourcePrefix = "OpenScct.Editor.Core.Prefabs.Library.";

    private static readonly Lazy<IReadOnlyList<PrefabDefinition>> s_builtin = new(LoadBuiltin);

    /// <summary>The user library folder (it may not exist yet).</summary>
    public static string LibraryFolder =>
        Environment.GetEnvironmentVariable("OSE_PREFABS") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(UserFolders.Settings, "prefabs");

    /// <summary>The map's prefab folder, or null for a map not saved yet.</summary>
    public static string? ProjectFolder(string? mapFolder) => mapFolder is null ? null : Path.Combine(mapFolder, ProjectStore.PrefabsFolder);

    public static IReadOnlyList<PrefabDefinition> Builtin => s_builtin.Value;

    /// <summary>The prefab of that name (ignoring case), or null. A broken file of that name is an error.</summary>
    public static PrefabDefinition? Find(string name, string? mapFolder)
    {
        if (!PrefabDefinition.IsValidName(name))
        {
            return null; // also keeps names like "../x" from reaching the file system
        }

        foreach ((string? folder, PrefabSource source) in Folders(mapFolder))
        {
            if (folder is not null && FileIn(folder, name) is { } path)
            {
                return Read(path, source);
            }
        }

        return Builtin.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every prefab by name (a map prefab hides a library one of the same name, which hides a built-in one),
    /// plus the files that could not be read.</summary>
    public static (List<PrefabDefinition> Prefabs, List<string> Problems) All(string? mapFolder)
    {
        var byName = new Dictionary<string, PrefabDefinition>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach ((string? folder, PrefabSource source) in Folders(mapFolder))
        {
            if (folder is null || !Directory.Exists(folder))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    PrefabDefinition def = Read(path, source);
                    byName.TryAdd(def.Name, def);
                }
                catch (ApiException ex)
                {
                    problems.Add(ex.Message);
                }
            }
        }

        foreach (PrefabDefinition def in Builtin)
        {
            byName.TryAdd(def.Name, def);
        }

        return (byName.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(), problems);
    }

    public static PrefabDefinition Read(string path, PrefabSource source)
    {
        SvJsonNode node;
        try
        {
            node = SvJson.Parse(File.ReadAllBytes(path), Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenScct.Content.SvMapFormatException)
        {
            throw new ApiException($"{path}: {ex.Message}", "name");
        }

        return PrefabDefinition.FromJson(node, source, path);
    }

    /// <summary>Writes a prefab as <c>&lt;folder&gt;/&lt;name&gt;.json</c>; returns the path.</summary>
    public static string Write(PrefabDefinition def, string folder)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, def.Name + ".json");
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, CanonicalJson.ToFileBytes(def.ToJson()));
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>The existing file for a name in a folder, ignoring case, or null.</summary>
    public static string? FileIn(string folder, string name)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        string exact = Path.Combine(folder, name + ".json");
        return File.Exists(exact)
            ? exact
            : Directory.EnumerateFiles(folder, "*.json").FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<(string? Folder, PrefabSource Source)> Folders(string? mapFolder)
    {
        yield return (ProjectFolder(mapFolder), PrefabSource.Project);
        yield return (LibraryFolder, PrefabSource.Library);
    }

    private static IReadOnlyList<PrefabDefinition> LoadBuiltin()
    {
        var asm = typeof(PrefabStore).Assembly;
        var list = new List<PrefabDefinition>();
        foreach (string res in asm.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using Stream stream = asm.GetManifestResourceStream(res)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            list.Add(PrefabDefinition.FromJson(SvJson.Parse(ms.ToArray(), res), PrefabSource.Builtin, null));
        }

        return list;
    }
}
