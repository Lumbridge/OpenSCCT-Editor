using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using OpenScct.Launcher.Install;

namespace OpenScct.Editor.Core.Updates;

/// <summary>
/// The way back from a swap cut off half way (the computer shuts down, the helper is killed). From the first rename
/// until the new build has started once, the install may have no exe, so no editor can start to put it right. While
/// a swap runs, the helper keeps a RunOnce entry for this account (Windows) that runs <c>ose update recover</c> from
/// the helper's own copy at the next sign-in; a swap that finishes or is rolled back removes it. The entry names a
/// small record in <c>updates\swaps</c> rather than the install path, so the command stays short whatever the path.
/// </summary>
public static class RecoveryEntry
{
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ValuePrefix = "OpenSCCT Editor update ";

    /// <summary>A short id for an install folder (the same folder always gets the same id).</summary>
    public static string Id(string install)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(install)).ToUpperInvariant()));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>Records the install and adds the entry that runs <paramref name="helperExe"/> to recover it.</summary>
    public static void Set(string install, string helperExe, UpdatePaths paths, UpdateLog log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string id = Id(install);
        try
        {
            string record = paths.SwapRecord(id);
            Directory.CreateDirectory(Path.GetDirectoryName(record)!);
            AtomicFile.WriteAllText(record, JsonSerializer.Serialize(new SwapRecord { Install = Path.GetFullPath(install) }));
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunOnceKey);
            key.SetValue(ValuePrefix + id, $"\"{helperExe}\" update recover --swap \"{record}\" --no-restart");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Write($"could not add the recovery entry: {ex.Message}");
        }
    }

    /// <summary>Removes the install's entry and record, if any.</summary>
    public static void Remove(string install, UpdatePaths? paths, UpdateLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string id = Id(install);
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunOnceKey, writable: true);
            key?.DeleteValue(ValuePrefix + id, throwOnMissingValue: false);
            if (paths is not null && File.Exists(paths.SwapRecord(id)))
            {
                File.Delete(paths.SwapRecord(id));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Write($"could not remove the recovery entry: {ex.Message}");
        }
    }

    /// <summary>The commands of every entry still waiting (the helpers they run must not be cleaned up).</summary>
    public static List<string> Pending()
    {
        var found = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return found;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunOnceKey);
            if (key is null)
            {
                return found;
            }

            foreach (string name in key.GetValueNames())
            {
                if (name.StartsWith(ValuePrefix, StringComparison.Ordinal) && key.GetValue(name) is string command)
                {
                    found.Add(command);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Nothing known to keep.
        }

        return found;
    }

    /// <summary>The install a swap record names; null when it cannot be read.</summary>
    public static string? InstallOf(string record)
    {
        try
        {
            return JsonSerializer.Deserialize<SwapRecord>(File.ReadAllText(record))?.Install;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private sealed class SwapRecord
    {
        [JsonPropertyName("install")]
        public string Install { get; set; } = string.Empty;
    }
}
