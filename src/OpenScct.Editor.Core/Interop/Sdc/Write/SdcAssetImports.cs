namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// Import records for objects in other packages (materials, meshes, sounds, classes). The engine finds an import by
/// its outer chain, name and class, and refuses to load the map when the class is wrong, so the class of an asset is
/// read from the install's own package that holds it (its export table), never guessed. Packages are read once.
/// </summary>
public sealed class SdcAssetImports
{
    private static readonly string[] s_extensions = [".utx", ".usx", ".uax", ".ukx", ".u", ".utc", ".uas", ".unr"];

    private readonly string? _gameRoot;
    private readonly IReadOnlyDictionary<string, string>? _provided;
    private readonly Dictionary<string, Dictionary<string, (string ClassPackage, string ClassName, string[] Groups)>?> _packages =
        new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, string>? _files;

    public SdcAssetImports(string? gameRoot, IReadOnlyDictionary<string, string>? provided = null) { _gameRoot = gameRoot; _provided = provided; }

    public string? PackageFile(string name) => File(name);

    public bool HasPackage(string name) => File(name) is not null;

    /// <summary>The object an asset path names in the install: its class and the groups between package and name;
    /// null when the install has no such package or object.</summary>
    public (string ClassPackage, string ClassName, string[] Groups)? Find(string path)
    {
        int dot = path.IndexOf('.');
        if (dot <= 0)
        {
            return null;
        }

        Dictionary<string, (string, string, string[])>? exports = Package(path[..dot]);
        return exports is not null && exports.TryGetValue(path[(dot + 1)..], out (string, string, string[]) found) ? found : null;
    }

    /// <summary>The objref of an import of <paramref name="path"/> (adding the package, group and object records),
    /// or 0 when the install does not hold it.</summary>
    public int Import(UPackageBuilder b, string path)
    {
        if (Find(path) is not { } found)
        {
            return 0;
        }

        string[] parts = path.Split('.');
        int outer = b.ImportPackage(parts[0]);
        for (int i = 1; i < parts.Length - 1; i++)
        {
            outer = b.ImportPackage(parts[i], outer);
        }

        return b.Import(found.ClassPackage, found.ClassName, outer, parts[^1]);
    }

    private Dictionary<string, (string, string, string[])>? Package(string name)
    {
        if (_packages.TryGetValue(name, out Dictionary<string, (string, string, string[])>? cached))
        {
            return cached;
        }

        Dictionary<string, (string, string, string[])>? exports = null;
        if (File(name) is { } file)
        {
            try
            {
                UPackage pkg = UPackage.Load(file);
                exports = new Dictionary<string, (string, string, string[])>(StringComparer.OrdinalIgnoreCase);
                foreach (UExport e in pkg.Exports)
                {
                    string path = pkg.PathOf(e.Index + 1);
                    string relative = path[(path.IndexOf('.') + 1)..];
                    (string package, string cls) = pkg.ClassOf(e);
                    string[] groups = relative.Split('.')[..^1];
                    exports.TryAdd(relative, (package == pkg.Name ? name : package, cls, groups));
                }
            }
            catch (InvalidDataException)
            {
                exports = null;
            }
        }

        _packages[name] = exports;
        return exports;
    }

    /// <summary>The install's package file of a name (Packages\** or System\_PC_).</summary>
    private string? File(string name)
    {
        if (_provided?.TryGetValue(name, out string? provided) == true) return provided;
        if (_gameRoot is null)
        {
            return null;
        }

        if (_files is null)
        {
            _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (string dir in new[] { Path.Combine(_gameRoot, "Packages"), Path.Combine(_gameRoot, "System") })
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (string f in Directory.EnumerateFiles(dir, "*", walk))
                {
                    if (s_extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                        && !f.Contains(Path.DirectorySeparatorChar + "MapsEd" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        _files.TryAdd(Path.GetFileNameWithoutExtension(f), f);
                    }
                }
            }
        }

        return _files.GetValueOrDefault(name);
    }
}
