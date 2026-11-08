using System.Security.Cryptography;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>What <see cref="SdcWriter.Write"/> writes and from what.</summary>
public sealed record SdcWriteOptions
{
    /// <summary>The editor copy (Packages\MapsEd, with brushes) or the cooked copy the game loads (Packages\Maps).</summary>
    public SdcLayout Layout { get; init; } = SdcLayout.Editor;

    /// <summary>The original install, for the classes of assets the map refers to (from its packages).</summary>
    public string? GameRoot { get; init; }

    public UeSchema? Schema { get; init; }

    /// <summary>The .content folder (class defaults and material sizes for a world built from the brushes); null
    /// uses <see cref="EditorServices.ContentRoot"/>.</summary>
    public string? ContentRoot { get; init; }

    /// <summary>The .sdc the project was imported from; null finds it from the project's source (by its hash). Its
    /// objects the project does not hold (the baked world, map textures and meshes, script classes, emitters) are
    /// written back from it.</summary>
    public string? SourceFile { get; init; }

    /// <summary>False writes everything from the project alone, as for a map made in this editor.</summary>
    public bool UseSource { get; init; } = true;

    public Action<string>? Progress { get; init; }
}

/// <summary>A write that cannot give a file the stock editor or game would accept; the message says why.</summary>
public sealed class SdcWriteRefusedException(string message) : InvalidOperationException(message);

/// <summary>What was written: the unpacked package, the findings, and counts by origin.</summary>
public sealed record SdcWriteResult(byte[] Package, IReadOnlyList<Finding> Findings, SdcWriteStats Stats)
{
    /// <summary>The editor world was regenerated from recovered brushes rather than copied from the source.</summary>
    public bool WorldRebuilt { get; init; }
    /// <summary>Project ids written under another export name (the package already held an object of that name).</summary>
    public IReadOnlyDictionary<string, string> Renamed { get; init; } = new Dictionary<string, string>();

    /// <summary>Project actors not in the file as themselves: conveyors written as RE+ belts (or left out) and imported
    /// belts left out for the belts lowered in their place.</summary>
    public IReadOnlySet<string> HandedOff { get; init; } = new HashSet<string>();
}

/// <summary>How many exports came from where.</summary>
public sealed record SdcWriteStats(int Exports, int FromProject, int Copied, int Rewritten, int New, int Removed, bool SourceUsed, string? Source);

/// <summary>
/// Writes a project as an original map package (.sdc), in the editor-copy layout the stock editor saves to
/// Packages\MapsEd or the cooked layout it saves to Packages\Maps (SDC-FORMAT.md). Every actor, brush, volume, mover
/// and GE is written from the project. A project imported from an .sdc keeps that file's tables and objects: names and
/// imports keep their indices and every object keeps its export slot, so what the project does not edit (the baked
/// world, StaticMeshInstances, map textures and meshes, script classes, particle emitters) is written back unchanged.
/// </summary>
public static class SdcWriter
{
    /// <summary>The unpacked package of <paramref name="project"/> under the package name <paramref name="packageName"/>
    /// (the .sdc file's name: the engine roots the map's object paths at it).</summary>
    /// <exception cref="SdcWriteRefusedException">An editor copy asked of a cooked map with no recovered brushes.</exception>
    public static SdcWriteResult Write(MapProject project, string packageName, SdcWriteOptions options)
    {
        var findings = new List<Finding>();
        project = SdcCompatibility.Prepare(project, options.ContentRoot ?? EditorServices.ContentRoot, options.GameRoot, findings);
        if (findings.Any(f => f.Severity == Severity.Error))
            throw new SdcWriteRefusedException(string.Join(Environment.NewLine, findings.Where(f => f.Severity == Severity.Error).Select(f => f.Message)));
        try
        {
            project = Stairs.StairGeometry.Lower(GeGeometry.Expand(project));
        }
        catch (ArgumentException ex)
        {
            throw new SdcWriteRefusedException(ex.Message);
        }
        (UPackage? src, SdcMap? srcMap) = options.UseSource ? Source(project, options, findings) : (null, null);
        bool recoveredEditor = srcMap?.Layout != SdcLayout.Editor && options.Layout == SdcLayout.Editor &&
            project.Brushes.Any(Compile.BakedContent.IsRecovered);
        if (srcMap?.Layout == SdcLayout.Cooked && options.Layout == SdcLayout.Editor && !recoveredEditor)
        {
            // Without recovered brushes there is no editable world to regenerate the missing editor records from.
            throw new SdcWriteRefusedException(@"this cooked map has no recovered CSG brushes. Recover its brushes before exporting an editor copy "
                + @"for Packages\MapsEd, or choose the playable cooked copy for Packages\Maps");
        }

        if (recoveredEditor)
        {
            // Preserve map-local resources and classes, but compile the world from the editable brushes.
            // Export must never change the open project's baked-world preferences.
            project = Session.ProjectSnapshot.Take(project);
            project.Baked = null;
            findings.Add(Finding.Info("sdc.recoveredEditor", "Rebuilt the editor world and lightmap records from the recovered brushes. "
                + (src is null ? "" : "Kept the source package's map-local assets and classes. ")
                + "Lighting uses the available lights and may differ from the original; Build All in RE+ refreshes mesh lighting."));
        }

        var plan = new SdcPackagePlan(project, packageName, options, src, srcMap, findings, recoveredEditor);
        byte[] package = plan.Build();
        return new SdcWriteResult(package, findings, plan.Stats) { Renamed = plan.Renamed, HandedOff = plan.HandedOff, WorldRebuilt = recoveredEditor };
    }

    /// <summary>Writes the .sdc file (one zlib chunk, as a normal stock save).</summary>
    public static SdcWriteResult WriteFile(MapProject project, string path, SdcWriteOptions options)
    {
        SdcWriteResult result = Write(project, Path.GetFileNameWithoutExtension(path), options);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, UPackageBuilder.Pack(result.Package));
        return result;
    }

    /// <summary>The project's source package when it is still the file the project was imported from.</summary>
    private static (UPackage?, SdcMap?) Source(MapProject p, SdcWriteOptions o, List<Finding> findings)
    {
        string? file = o.SourceFile ?? (p.Source.Kind == "sdc" ? p.Source.File : null);
        if (file is null)
        {
            return (null, null);
        }

        if (!File.Exists(file))
        {
            findings.Add(Finding.Warning("sdc.sourceMissing",
                $"the map was imported from {file}, which is gone: its baked world, map textures, meshes and script classes cannot be written back"));
            return (null, null);
        }

        byte[] bytes = File.ReadAllBytes(file);
        if (o.SourceFile is null && p.Source.Sha256 is { Length: > 0 } sha && Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha)
        {
            findings.Add(Finding.Warning("sdc.sourceChanged",
                $"{file} changed since the map was imported from it: its objects are not written back (import it again to keep them)"));
            return (null, null);
        }

        // Exports are matched by the names the import gave them, which are rooted at the project's map id.
        (byte[] package, int chunks) = SdcContainer.Unwrap(bytes);
        UPackage pkg = UPackage.Parse(p.Id, package, chunks);
        SdcMap map = SdcMapReader.Read(pkg, o.Schema);
        return (pkg, map);
    }
}
