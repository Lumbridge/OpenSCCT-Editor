using System.Reflection;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Services;

/// <summary>Severity of a finding, worst first.</summary>
public enum Severity
{
    Error,
    Warning,
    Info,
}

/// <summary>One finding of a build, import or check: what, where, and which objects (for click-to-select and for
/// scripts, which fix by id).</summary>
public sealed record Finding(Severity Severity, string Code, string Message, IReadOnlyList<ObjectRef> Objects, Vec3? Location = null)
{
    public static Finding Error(string code, string message, params ObjectRef[] objects) => new(Severity.Error, code, message, objects);

    public static Finding Warning(string code, string message, params ObjectRef[] objects) => new(Severity.Warning, code, message, objects);

    public static Finding Info(string code, string message, params ObjectRef[] objects) => new(Severity.Info, code, message, objects);

    /// <summary>A one-click fix, when the rule knows a safe one.</summary>
    public FindingFix? Fix { get; init; }

    /// <summary>What the views draw for the finding, when a place alone does not show it (a leak's way through).</summary>
    public IReadOnlyList<FindingShape> Shapes { get; init; } = [];

    public Finding At(Vec3 location) => this with { Location = location };

    public Finding WithFix(string label, SvJsonObject changes) => this with { Fix = new FindingFix(label, changes) };

    public Finding WithFix(FindingFix fix) => this with { Fix = fix };

    public override string ToString() =>
        $"{Severity.ToString().ToLowerInvariant()} {Code}: {Message}" + (Objects.Count > 0 ? $" [{string.Join(", ", Objects.Select(o => o.Id))}]" : "");
}

/// <summary>How a <see cref="FindingShape"/> is drawn.</summary>
public enum FindingShapeKind
{
    /// <summary>A line through the points in order (a way through the map).</summary>
    Path,

    /// <summary>A closed polygon (an opening, a portal sheet).</summary>
    Outline,
}

/// <summary>A line or polygon of a finding, in map units.</summary>
public sealed record FindingShape(FindingShapeKind Kind, IReadOnlyList<Vec3> Points);

/// <summary>A fix of a finding: what it does, and the change document (<c>osmap.changes/1</c>) that does it.</summary>
public sealed record FindingFix(string Label, SvJsonObject Changes)
{
    /// <summary>Where the actors the fix moves stood when the check worked it out.</summary>
    public IReadOnlyList<(string Id, Vec3 At)> Was { get; init; } = [];

    /// <summary>
    /// False once an actor the fix moves has moved since the check (or is gone): the fix was worked out for where it
    /// stood, so the check must run again.
    /// </summary>
    public bool IsCurrent(MapProject p) => Was.All(w => p.FindActor(w.Id) is { } a && (a.Location - w.At).Length < 0.01);
}

/// <summary>Which game the output is for. Original export enforces the stock engine's limits as errors.</summary>
public enum TargetGame
{
    OpenScct,
    Original,
}

// ---- compile -------------------------------------------------------------------------------------------------

public sealed record CompileOptions
{
    /// <summary>Load the output back with OpenSCCT's strict SVMap loader and report what it rejects.</summary>
    public bool Validate { get; init; } = true;

    /// <summary>Skip lighting work (fast iteration); the world is lit flat.</summary>
    public bool FastLighting { get; init; }

    /// <summary>Where shared OpenSCCT content lives (the .content folder), for class defaults and shared render data.</summary>
    public string? ContentRoot { get; init; }

    /// <summary>The project's .osmap folder, for its own assets (<c>assets/</c>); null when it has none yet.</summary>
    public string? ProjectFolder { get; init; }

    /// <summary>Progress lines ("csg 120/400 brushes"), or null.</summary>
    public Action<string>? Progress { get; init; }

    /// <summary>The game the map is for: Original reports the stock engine's limits (zones, BSP size) as errors.</summary>
    public TargetGame Target { get; init; } = TargetGame.OpenScct;

    /// <summary>
    /// Write sim/classes.json (the class defaults of every class the map's gameplay actors use), as OpenSCCT's converter
    /// does for every converted map. On by default: OpenSCCT's renderer reads an actor's DrawType and StaticMesh from
    /// it when the actor stores none, so without it the gameplay objects that draw their class's mesh (cameras, laser
    /// triggers, laptops, bomb targets, disks, window panes) are invisible in the game, though they still work. Its map
    /// loader also applies the table's class defaults where they differ from the generated ones, so the table sets the
    /// gameplay values an actor does not store, as on a converted map.
    /// </summary>
    public bool WriteClassTable { get; init; } = true;
}

public sealed record CompileResult(bool Ok, string OutputFolder, IReadOnlyList<Finding> Findings, TimeSpan Elapsed,
    IReadOnlyDictionary<string, long> Stats);

/// <summary>Compiles a project to an SVMap 1.2 folder (the game-ready form OpenSCCT plays).</summary>
public interface IMapCompiler
{
    CompileResult Compile(MapProject project, string outputFolder, CompileOptions options);
}

// ---- geometry preview ----------------------------------------------------------------------------------------

/// <summary>One convex polygon of the CSG result, in world space (Unreal units), with its material, the brush face
/// that made it and that face's lightmap scale.</summary>
public sealed record PreviewPolygon(IReadOnlyList<Vec3> Vertices, Vec3 Normal, string? Material, int Flags, string BrushId,
    Vec3 TextureBase, Vec3 TextureU, Vec3 TextureV, int PanU, int PanV, int FaceIndex = 0, double LightMapScale = 32);

/// <summary>Runs the CSG of a project and returns the visible world polygons, for the editor's viewports (fast, no
/// BSP, no lighting).</summary>
public interface IWorldPreview
{
    IReadOnlyList<PreviewPolygon> Build(MapProject project);
}

// ---- import / export ------------------------------------------------------------------------------------------

public sealed record ImportOptions
{
    /// <summary>The map id to give the project (default: from the file).</summary>
    public string? MapId { get; init; }

    /// <summary>Where shared OpenSCCT content lives (the .content folder).</summary>
    public string? ContentRoot { get; init; }

    /// <summary>The original game install (for .sdc import through the converter).</summary>
    public string? GameRoot { get; init; }

    /// <summary>How a cooked .sdc's stripped static lights are brought back (<see cref="Interop.Sdc.Lights.LostLightRecovery"/>).</summary>
    public Interop.Sdc.Lights.LightRecoveryMode LightRecovery { get; init; } = Interop.Sdc.Lights.LightRecoveryMode.Full;

    /// <summary>How the brushes of a cooked .sdc or a converted SVMap are recovered from its compiled BSP
    /// (<see cref="Interop.Sdc.Recovery.BrushRecovery"/>); Off keeps the baked world only. An editor copy keeps its own
    /// brushes either way.</summary>
    public Interop.Sdc.Recovery.BrushRecoveryMode BrushRecovery { get; init; } = Interop.Sdc.Recovery.BrushRecoveryMode.Merge;

    /// <summary>More folders of editor copies (or installs) the light recovery searches for the map's twin.</summary>
    public IReadOnlyList<string> TwinFolders { get; init; } = [];

    /// <summary>How long the light recovery may fit stand-in lights.</summary>
    public TimeSpan? LightFitTime { get; init; }

    public Action<string>? Progress { get; init; }
}

public sealed record ImportResult(MapProject Project, IReadOnlyList<Finding> Findings);

/// <summary>Turns a map in another format into a project.</summary>
public interface IMapImporter
{
    /// <summary>Short name: svmap, t3d, sdc.</summary>
    string Name { get; }

    /// <summary>True when the importer reads <paramref name="path"/> (file or folder).</summary>
    bool CanImport(string path);

    ImportResult Import(string path, ImportOptions options);
}

public sealed record ExportOptions
{
    /// <summary>Generated native dependencies supplied by a portable export bundle.</summary>
    public IReadOnlyDictionary<string, string>? NativePackages { get; init; }

    /// <summary>Include referenced assets instead of relying on packages installed on this machine.</summary>
    public bool PortableAssets { get; init; }
    public string? ContentRoot { get; init; }

    /// <summary>The original install, for exporters that read its packages; null finds it.</summary>
    public string? GameRoot { get; init; }

    /// <summary>The exporter's <see cref="IMapExporter.Choices"/> by name (a missing one takes its default).</summary>
    public IReadOnlyDictionary<string, string> Choices { get; init; } = new Dictionary<string, string>();

    /// <summary>The project's folder, for the files its own assets and outfits name (null for a project not saved).</summary>
    public string? ProjectFolder { get; init; }

    public Action<string>? Progress { get; init; }

    /// <summary>The value of one of <paramref name="exporter"/>'s choices.</summary>
    public string Choice(IMapExporter exporter, string name) =>
        Choices.TryGetValue(name, out string? v) ? v : exporter.Choices.First(c => c.Name == name).Default;
}

/// <summary>A choice an exporter offers (the export dialog shows it, <c>ose export --name value</c> sets it).
/// <paramref name="ValueLabels"/> are the values as the dialog words them (null: the values themselves).</summary>
public sealed record ExportChoice(string Name, string Label, IReadOnlyList<string> Values, string Default, IReadOnlyList<string>? ValueLabels = null);

public sealed record ExportResult(bool Ok, IReadOnlyList<string> Files, IReadOnlyList<Finding> Findings);

/// <summary>Writes a project in another format (T3D for the original editor, ...).</summary>
public interface IMapExporter
{
    /// <summary>Short name: t3d, ...</summary>
    string Name { get; }

    /// <summary>File extension written, e.g. ".t3d"; empty for an exporter that writes a folder.</summary>
    string Extension { get; }

    TargetGame Target { get; }

    /// <summary>Choices the export takes (none for most formats).</summary>
    IReadOnlyList<ExportChoice> Choices => [];

    /// <summary>True when the export is a folder of files (the path given is that folder).</summary>
    bool WritesFolder => false;

    ExportResult Export(MapProject project, string path, ExportOptions options);
}

// ---- checks ---------------------------------------------------------------------------------------------------

public sealed record CheckOptions
{
    public TargetGame Target { get; init; } = TargetGame.OpenScct;

    /// <summary>Only rules whose code starts with one of these (null = all).</summary>
    public IReadOnlyList<string>? Only { get; init; }

    /// <summary>
    /// The project compiled and loaded as OpenSCCT's server loads it, for the deep rules (zone leaks, actors in the
    /// void, floating meshes, mesh collision caps, reachability); they are skipped when null.
    /// </summary>
    public Analysis.AnalysisMap? Compiled { get; init; }

    /// <summary>The project's .osmap folder, for rules that look at the files it keeps (pictures, outfits); null when it
    /// has none yet, which skips them.</summary>
    public string? ProjectFolder { get; init; }
}

/// <summary>Map Check: lint rules over a project (limits, references, design rules).</summary>
public interface IMapChecker
{
    IReadOnlyList<Finding> Check(MapProject project, CheckOptions options);
}

// ---- classes ----------------------------------------------------------------------------------------------------

/// <summary>A property of a class, for the inspector, validation and scripts.</summary>
public sealed record PropInfo(string Name, string Type, string? Category, bool Editable, string? Enum, string? Struct,
    string? ObjectClass, int ArrayDim, string? Inner, string? DeclaringClass);

/// <summary>A class an actor can be (Engine.PlayerStart, SBase.SObjective...).</summary>
public sealed record ClassInfo(string Name, string? Super, bool Placeable, bool Abstract, IReadOnlyList<PropInfo> Properties,
    SvJsonObject Defaults)
{
    public string ShortName => Name.Contains('.') ? Name[(Name.LastIndexOf('.') + 1)..] : Name;
}

/// <summary>The classes the map can place, with their property schemas and defaults.</summary>
public interface IClassCatalog
{
    /// <summary>Where the data came from (e.g. "shared/classes.json (669 classes)" or "generated metadata").</summary>
    string SourceDescription { get; }

    /// <summary>Finds a class by full (<c>Engine.Light</c>) or short (<c>Light</c>) name, ignoring case.</summary>
    ClassInfo? Find(string name);

    IEnumerable<ClassInfo> All { get; }

    /// <summary>The super chain, the class first.</summary>
    IEnumerable<ClassInfo> Chain(string name);

    /// <summary>All properties of the class including inherited ones, nearest class first.</summary>
    IReadOnlyList<PropInfo> AllProperties(string name);

    /// <summary>The effective default of a property (the class default object), or null.</summary>
    SvJsonNode? Default(string className, string property);

    bool IsA(string className, string ancestor);

    /// <summary>The value names of an enum in order (full path such as <c>Engine.Actor.EPhysics</c>, or the bare
    /// name), or null when unknown.</summary>
    IReadOnlyList<string>? EnumValues(string enumName) => null;

    /// <summary>The members of a struct (full path such as <c>Core.Object.Vector</c>, or the bare name), or null.</summary>
    IReadOnlyList<PropInfo>? StructMembers(string structName) => null;
}

/// <summary>Turns builder specs back into shapes (so parametric pieces regenerate when a parameter changes).</summary>
public interface IShapeBuilder
{
    /// <summary>Builder kinds this builder makes.</summary>
    IReadOnlyList<string> Kinds { get; }

    /// <summary>Default parameters of a kind (for the UI and scripts).</summary>
    BuilderSpec Defaults(string kind);

    /// <summary>The shape of a spec. Multi-brush pieces (blockout rooms) return several shapes in CSG order.</summary>
    IReadOnlyList<(BrushShape Shape, CsgOper Oper, Vec3 Offset)> Build(BuilderSpec spec);
}

/// <summary>Marks a class with a public parameterless constructor that implements one or more service interfaces;
/// <see cref="EditorServices.Discover"/> creates and registers it, so modules plug in without a shared list.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EditorServiceAttribute : Attribute
{
    /// <summary>Higher wins when two services of one kind exist.</summary>
    public int Priority { get; init; }
}

/// <summary>The services the editor's front ends (app, CLI, API server) use. Filled by <see cref="Discover"/>.</summary>
public static class EditorServices
{
    private static readonly object s_lock = new();
    private static bool s_discovered;

    public static IMapCompiler? Compiler { get; set; }

    public static IWorldPreview? Preview { get; set; }

    public static IMapChecker? Checker { get; set; }

    public static IClassCatalog? Classes { get; set; }

    public static List<IMapImporter> Importers { get; } = new();

    public static List<IMapExporter> Exporters { get; } = new();

    public static List<IShapeBuilder> ShapeBuilders { get; } = new();

    /// <summary>The .content folder of an OpenSCCT checkout or install (shared/classes.json, svmap/...), when known.</summary>
    public static string? ContentRoot { get; set; }

    /// <summary>Adopts a newly populated content folder and invalidates indexes after files change in place.</summary>
    public static void RefreshContent(string root)
    {
        string full = Path.GetFullPath(root);
        ContentRoot = full;
        AssetCatalog.Invalidate(full);
        ContentLibrary.Invalidate(full);
        GameplayPack.Invalidate(full);
        SkeletalLibrary.Invalidate(full);
        UeSchema.Invalidate(full);
        if (Classes is ClassCatalog)
        {
            Classes = ClassCatalog.FromContent(full);
        }
    }

    /// <summary>The OpenSCCT checkout or install root (for launching the game), when known.</summary>
    public static string? OpenScctRoot { get; set; }

    public static IMapImporter? ImporterFor(string path) => Importers.FirstOrDefault(i => i.CanImport(path));

    /// <summary>Imports through <paramref name="importer"/>, then fills what the map's identity lacks from its legends
    /// and the shared catalog, so copies of stock and community maps keep their pictures whatever the source format, and
    /// keeps the import's findings in the project for its import report.</summary>
    public static ImportResult Import(IMapImporter importer, string path, ImportOptions options)
    {
        ImportResult result = importer.Import(path, options);
        Interop.Svmap.IdentityImport.FillMissing(result.Project, options.ContentRoot ?? ContentRoot, Directory.Exists(path) ? Path.GetFullPath(path) : null);
        Interop.ImportReport.Keep(result.Project, result.Findings);
        return result;
    }

    public static IMapExporter? Exporter(string name) =>
        Exporters.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public static IShapeBuilder? ShapeBuilder(string kind) =>
        ShapeBuilders.FirstOrDefault(b => b.Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase));

    /// <summary>Creates every [EditorService] class of the editor's assemblies (once) and finds the content folder.</summary>
    public static void Discover(params Assembly[] extra)
    {
        lock (s_lock)
        {
            if (s_discovered)
            {
                return;
            }

            s_discovered = true;
            IEnumerable<Assembly> assemblies = new[] { typeof(EditorServices).Assembly }.Concat(extra).Distinct();
            var found = assemblies
                .SelectMany(SafeTypes)
                .Select(t => (Type: t, Attr: t.GetCustomAttribute<EditorServiceAttribute>()))
                .Where(x => x.Attr is not null && !x.Type.IsAbstract && x.Type.GetConstructor(Type.EmptyTypes) is not null)
                .OrderBy(x => x.Attr!.Priority)
                .ThenBy(x => x.Type.FullName, StringComparer.Ordinal);
            foreach ((Type type, EditorServiceAttribute? _) in found)
            {
                object service = Activator.CreateInstance(type)!;
                Register(service);
            }

            ContentRoot ??= ContentLocator.FindContentRoot();
            OpenScctRoot ??= ContentLocator.FindOpenScctRoot();
        }
    }

    /// <summary>Registers one service object under every interface it implements (later registrations win).</summary>
    public static void Register(object service)
    {
        if (service is IMapCompiler c)
        {
            Compiler = c;
        }

        if (service is IWorldPreview p)
        {
            Preview = p;
        }

        if (service is IMapChecker k)
        {
            Checker = k;
        }

        if (service is IClassCatalog cat)
        {
            Classes = cat;
        }

        if (service is IMapImporter i)
        {
            Importers.Add(i);
        }

        if (service is IMapExporter e)
        {
            Exporters.Add(e);
        }

        if (service is IShapeBuilder b)
        {
            ShapeBuilders.Add(b);
        }
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try
        {
            return a.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}

/// <summary>Finds OpenSCCT's content and checkout without configuration: environment variables first
/// (OSE_CONTENT, OSE_OPENSCCT), then (for the checkout) the one this build was compiled against, then the OpenSCCT
/// clone the build tools use (OSE_OPENSCCT_GIT, or the main checkout behind the pinned worktree), then an OpenSCCT
/// clone next to the editor (../OpenSCCT), then the OpenSCCT install data folder.</summary>
public static class ContentLocator
{
    /// <summary>The OpenSCCT checkout the editor was compiled against (normally the pinned ..\OpenSCCT-Editor-deps), so
    /// the content builder matches the libraries. Null when the build did not record one.</summary>
    public static string? BuiltAgainst { get; } = typeof(ContentLocator).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "OpenScctRoot")?.Value;

    public static string? FindContentRoot()
    {
        static bool HasContent(string root) => File.Exists(Path.Combine(root, "shared", "classes.json")) ||
            File.Exists(Path.Combine(root, "installed.json"));

        if (GameExe.ManagedLauncherRoot is { } managed)
        {
            string downloaded = Path.Combine(managed, "packs");
            return HasContent(downloaded) ? downloaded : null;
        }
        foreach (string? c in Candidates("OSE_CONTENT", ".content"))
        {
            if (c is not null && HasContent(c))
            {
                return Path.GetFullPath(c);
            }
        }

        string packs = Path.Combine(GameExe.LauncherRoot, "packs");
        return HasContent(packs) ? packs : null;
    }

    public static string? FindOpenScctRoot()
    {
        string? env = Environment.GetEnvironmentVariable("OSE_OPENSCCT");
        if (string.IsNullOrEmpty(env) && BuiltAgainst is { Length: > 0 } built && File.Exists(Path.Combine(built, "OpenSCCT.slnx")))
        {
            return Path.GetFullPath(built);
        }

        foreach (string? c in Candidates("OSE_OPENSCCT", ""))
        {
            if (c is not null && File.Exists(Path.Combine(c, "OpenSCCT.slnx")))
            {
                return Path.GetFullPath(c);
            }
        }

        return null;
    }

    private static IEnumerable<string?> Candidates(string envVar, string sub)
    {
        string? env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(env))
        {
            yield return env;
        }

        // The clone the build tools use, and the main checkout behind the worktree this build was compiled against:
        // content lives in the clone itself, not in its pinned worktree.
        if (Environment.GetEnvironmentVariable("OSE_OPENSCCT_GIT") is { Length: > 0 } git)
        {
            yield return Path.Combine(git, sub);
        }

        if (MainCheckout(BuiltAgainst) is { } main)
        {
            yield return Path.Combine(main, sub);
        }

        // Walk up from the tool's folder and from the working folder, looking for an OpenSCCT clone beside it.
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? d = new(start);
            for (int i = 0; d is not null && i < 8; i++, d = d.Parent)
            {
                yield return Path.Combine(d.FullName, "OpenSCCT", sub);
                yield return Path.Combine(d.FullName, sub);
            }
        }
    }

    /// <summary>The main checkout of a git worktree (its .git file points into <c>&lt;main&gt;/.git/worktrees</c>), the folder
    /// itself when it is not a worktree, or null.</summary>
    private static string? MainCheckout(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        try
        {
            string dotGit = Path.Combine(folder, ".git");
            if (File.Exists(dotGit) && File.ReadLines(dotGit).FirstOrDefault() is { } line && line.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                var gitDir = new DirectoryInfo(Path.GetFullPath(Path.Combine(folder, line["gitdir:".Length..].Trim())));
                if (gitDir.Parent is { Name: "worktrees" } worktrees && worktrees.Parent is { Name: ".git" } repo && repo.Parent is { } main)
                {
                    return main.FullName;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return folder;
        }

        return folder;
    }
}
