using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Launcher.Versioning;

namespace OpenScct.Editor.Core.Updates;

/// <summary>
/// Where updates live. In the editor's data folder (<c>%LOCALAPPDATA%\OpenSCCT-Editor\updates</c>, or OSE_DATA): the
/// release list cache, downloads, the copied helper and its log. In the install folder, under the hidden
/// <c>.update</c> folder: the unpacked new build, the previous build after a swap and the swap's journal, so every move
/// is a rename on one volume.
/// </summary>
public sealed class UpdatePaths
{
    public const string WorkFolderName = ".update";
    public const string StagedMarkName = ".staged";
    public const string JournalName = "journal.json";
    public const string ResultName = "result.json";

    public UpdatePaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    /// <summary>The updates folder in the editor's data folder.</summary>
    public static UpdatePaths Default => new(Path.Combine(SvmbuildRunner.DataRoot, "updates"));

    public string Root { get; }

    /// <summary>The release list and its ETag. A local test feed (<see cref="UpdateHttp.ApiVariable"/>) gets a cache of
    /// its own, so its releases are never offered from the real one later.</summary>
    public string Cache => Path.Combine(Root, UpdateHttp.ApiOverride is null ? "cache" : "cache-local");

    public string Downloads => Path.Combine(Root, "downloads");

    /// <summary>Copies of a new build's <c>ose</c> folder that swap the install (a program cannot run from a folder
    /// that is being moved).</summary>
    public string Helpers => Path.Combine(Root, "helper");

    public string Log => Path.Combine(Root, "update.log");

    /// <summary>Held while an update downloads, unpacks or swaps, so two editors never do it at once.</summary>
    public string Lock => Path.Combine(Root, "update.lock");

    public string Helper(string name) => Path.Combine(Helpers, name);

    /// <summary>Which install a recovery entry (<see cref="RecoveryEntry"/>) belongs to, by its id.</summary>
    public string SwapRecord(string id) => Path.Combine(Root, "swaps", id + ".json");

    public string Plan(SemVersion version) => Path.Combine(Root, $"apply-{version}.json");

    /// <summary>Every handoff owns its helper and plan, even when two installs update to the same version.</summary>
    public static string OperationName(string install, SemVersion version) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(install).ToUpperInvariant())))[..16]
        + "-" + version + "-" + Guid.NewGuid().ToString("N");

    public string OperationPlan(string operation) => Path.Combine(Root, $"apply-{operation}.json");

    public string Download(string assetName) => Path.Combine(Downloads, assetName);

    public static string WorkFolder(string install) => Path.Combine(install, WorkFolderName);

    /// <summary>The unpacked new build.</summary>
    public static string Staged(string install, SemVersion version) => Path.Combine(WorkFolder(install), version.ToString());

    /// <summary>The previous build's entries after a swap, kept until the new build has started.</summary>
    public static string Old(string install, SemVersion version) => Path.Combine(WorkFolder(install), "old-" + version);

    public static string Journal(string install) => Path.Combine(WorkFolder(install), JournalName);

    /// <summary>Held by the helper for a whole swap or recovery, so two never change one install at once.</summary>
    public static string SwapLock(string install) => Path.Combine(WorkFolder(install), "swap.lock");

    /// <summary>How the last install or recovery ended (<see cref="UpdateResult"/>); the next editor start reports it.</summary>
    public static string Result(string install) => Path.Combine(WorkFolder(install), ResultName);

    /// <summary>The new editor writes its version here when the helper starts it to check it runs.</summary>
    public static string Probe(string install, SemVersion version) => Path.Combine(WorkFolder(install), $"probe-{version}.txt");

    /// <summary>The install folder of a release exe (its own folder).</summary>
    public static string InstallOf(string exePath) => Path.GetDirectoryName(Path.GetFullPath(exePath))!;
}
