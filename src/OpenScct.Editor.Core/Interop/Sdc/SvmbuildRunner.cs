using System.Security.Cryptography;
using System.Text;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Services;
using OpenScct.Maps;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// OpenSCCT's content builder (content/svmbuild, Python), which turns an original map (.sdc) into an SVMap folder, run
/// through OpenSCCT's own launcher (<see cref="BuilderTool"/>). It comes from one of two places:
/// <list type="bullet">
/// <item>An OpenSCCT checkout. The builder writes generated files next to its own code
/// (tools/scctpkg/scctpkg/class_defaults.json, Python byte code), so it never runs inside the checkout: the four trees
/// it needs (content/svmbuild, tools/scctpkg, re/formats, spec) are copied once per builder version to
/// <c>%LOCALAPPDATA%\OpenSCCT-Editor\svmbuild\&lt;digest&gt;</c> and run there with the Python on PATH (or OSE_PYTHON).</item>
/// <item>An installed OpenSCCT game build (the launcher's <c>%LOCALAPPDATA%\OpenSCCT\versions\&lt;current&gt;</c>, or a
/// folder given by OSE_OPENSCCT): its <c>contenttools\</c> builder with its own Python, run in place as the game itself
/// runs it for downloaded maps (its embedded Python only imports the build's own copy, so it cannot be staged). The
/// first run writes what the game's own first build would: class_defaults.json and byte code inside contenttools.</item>
/// </list>
/// </summary>
public sealed class SvmbuildRunner
{
    /// <summary>The checkout folders the builder needs, relative to the checkout root (its REPO_ROOT).</summary>
    public static readonly IReadOnlyList<string> Trees = ["content/svmbuild", "tools/scctpkg", "re/formats", "spec"];

    /// <summary>Overrides the Python interpreter of a checkout's builder (default: <c>python</c> from PATH, 3.10+).</summary>
    public const string PythonVariable = "OSE_PYTHON";

    /// <summary>Overrides <see cref="DataRoot"/>.</summary>
    public const string DataVariable = "OSE_DATA";

    private readonly string? _python;
    private BuilderTool? _tool;

    private SvmbuildRunner(string sourceRoot, bool installed, string? python, BuilderTool? tool)
    {
        SourceRoot = sourceRoot;
        IsInstalledBuild = installed;
        _python = python;
        _tool = tool;
    }

    /// <summary>The OpenSCCT checkout or game build the builder comes from.</summary>
    public string SourceRoot { get; }

    /// <summary>True for a game build's <c>contenttools</c> builder, false for a (staged) checkout.</summary>
    public bool IsInstalledBuild { get; }

    public string Description => IsInstalledBuild ? $"the OpenSCCT build in {SourceRoot}" : $"the OpenSCCT checkout {SourceRoot}";

    /// <summary>
    /// Whether this builder converts editor copies (Packages\MapsEd, the stock editor's working copy with its brushes):
    /// its map reader knows the editor layout (<c>mapconv/source.py</c> has <c>editor_copy</c>). Null when its sources
    /// cannot be read.
    /// </summary>
    public bool? ReadsEditorCopies
    {
        get
        {
            string rel = Path.Combine("content", "svmbuild", "mapconv", "source.py");
            string file = IsInstalledBuild ? Path.Combine(SourceRoot, "contenttools", "repo", rel) : Path.Combine(SourceRoot, rel);
            try
            {
                return File.Exists(file) ? File.ReadAllText(file).Contains("editor_copy", StringComparison.Ordinal) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>The editor's own data folder (staged builder, converted imports): OSE_DATA, else
    /// <c>%LOCALAPPDATA%\OpenSCCT-Editor</c>.</summary>
    public static string DataRoot => Environment.GetEnvironmentVariable(DataVariable) is { Length: > 0 } d
        ? Path.GetFullPath(d)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSCCT-Editor");

    /// <summary>
    /// The builder of <paramref name="root"/> (a checkout or a game build), else of OSE_OPENSCCT (either kind), else of
    /// the located checkout (<see cref="EditorServices.OpenScctRoot"/>), else of the launcher's current game build. Null
    /// when none has one.
    /// </summary>
    public static SvmbuildRunner? Find(string? root = null)
    {
        string? env = Environment.GetEnvironmentVariable("OSE_OPENSCCT");
        foreach (string? candidate in new[] { root, env, EditorServices.OpenScctRoot, ContentLocator.FindOpenScctRoot(), InstalledBuild() })
        {
            if (string.IsNullOrEmpty(candidate) || !Directory.Exists(candidate))
            {
                continue;
            }

            string full = Path.GetFullPath(candidate);
            if (HasBuilder(full))
            {
                string python = Environment.GetEnvironmentVariable(PythonVariable) is { Length: > 0 } p ? p : "python";
                return new SvmbuildRunner(full, installed: false, python, tool: null);
            }

            if (BuilderTool.InBuild(full) is { } shipped && shipped.SupportsAddons(full))
            {
                return new SvmbuildRunner(full, installed: true, python: null, shipped);
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="root"/> is a checkout with the builder's sources.</summary>
    public static bool HasBuilder(string root) =>
        File.Exists(Path.Combine(root, "content", "svmbuild", "gamefiles.py"))
        && File.Exists(Path.Combine(root, "tools", "scctpkg", "scctpkg", "packageset.py"))
        && Directory.Exists(Path.Combine(root, "re", "formats"))
        && Directory.Exists(Path.Combine(root, "spec"));

    /// <summary>The launcher's current game build (<c>%LOCALAPPDATA%\OpenSCCT\install.json</c>), or null.</summary>
    public static string? InstalledBuild()
    {
        string launcher = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSCCT");
        string manifest = Path.Combine(launcher, "install.json");
        if (!File.Exists(manifest))
        {
            return null;
        }

        try
        {
            return SvJson.Parse(File.ReadAllBytes(manifest), manifest) is SvJsonObject o && o["Current"] is SvJsonString { Value.Length: > 0 } v
                && v.Value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                ? Path.Combine(launcher, "versions", v.Value)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenScct.Content.SvMapFormatException)
        {
            return null;
        }
    }

    /// <summary>Cancelled when the host is closing (the editor app sets its quit token): a run in progress is stopped
    /// and its processes killed, so none outlives the editor.</summary>
    public static CancellationToken Stopping { get; set; }

    /// <summary>
    /// Runs <c>svmbuild &lt;arguments&gt;</c> with no window (staging a checkout's builder first); every output line goes
    /// to <paramref name="onLine"/>. <paramref name="workDir"/> is the folder an installed builder runs in. Returns the
    /// exit code.
    /// </summary>
    public int Run(IEnumerable<string> arguments, string workDir, Action<string>? onLine)
    {
        _tool ??= new BuilderTool(_python!, Path.Combine(Stage(onLine), "content"));
        try
        {
            return _tool.RunAsync(arguments, workDir, onLine, Stopping).GetAwaiter().GetResult();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"could not start '{_tool.Python}' for the content builder (install Python 3.10+ or set {PythonVariable}): {ex.Message}", ex);
        }
    }

    /// <summary>Copies a checkout's builder to the editor's data folder (once per version of its files) and returns the
    /// copy's root.</summary>
    private string Stage(Action<string>? progress)
    {
        List<(string Rel, string Full)> files = SourceFiles();
        string digest = Digest(files);
        string target = Path.Combine(DataRoot, "svmbuild", digest[..12]);
        if (File.Exists(Path.Combine(target, ".staged")))
        {
            return target;
        }

        progress?.Invoke($"copying the content builder from {SourceRoot}");
        string temp = target + ".tmp-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, recursive: true);
        }

        foreach ((string rel, string full) in files)
        {
            string dest = Path.Combine(temp, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(full, dest);
        }

        File.WriteAllText(Path.Combine(temp, ".staged"), SourceRoot + "\n" + digest + "\n");
        try
        {
            Directory.Move(temp, target);
        }
        catch (IOException) when (File.Exists(Path.Combine(target, ".staged")))
        {
            // Another import staged the same version first.
            Directory.Delete(temp, recursive: true);
        }

        return target;
    }

    private List<(string Rel, string Full)> SourceFiles()
    {
        var files = new List<(string, string)>();
        foreach (string tree in Trees)
        {
            string dir = Path.Combine(SourceRoot, tree);
            foreach (string full in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(SourceRoot, full).Replace('\\', '/');
                // Byte code and caches, and the game-derived class defaults the classes step makes in the copy.
                if (rel.Contains("/__pycache__/", StringComparison.Ordinal) || rel.Contains("/.pytest_cache/", StringComparison.Ordinal)
                    || rel.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)
                    || rel.EndsWith("/scctpkg/class_defaults.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                files.Add((rel, full));
            }
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return files;
    }

    private static string Digest(List<(string Rel, string Full)> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach ((string rel, string full) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(rel + "\n"));
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(full)));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
