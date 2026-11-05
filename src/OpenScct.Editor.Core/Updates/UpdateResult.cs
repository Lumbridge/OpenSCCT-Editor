using System.Text.Json;
using System.Text.Json.Serialization;
using OpenScct.Launcher.Install;

namespace OpenScct.Editor.Core.Updates;

/// <summary>
/// How the last install or recovery of an install ended, in <c>&lt;install&gt;\.update\result.json</c>. The helper
/// writes it whether or not it restarts the editor (installing on quit restarts nothing), and the next editor start
/// reads it once and says what happened: "Updated to 0.2.0", or why the update was not installed.
/// </summary>
public sealed class UpdateResult
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("installed")]
    public bool Installed { get; set; }

    /// <summary>Why it was not installed, as a sentence for the user (<see cref="UpdateFailure"/>).</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    public static UpdateResult Success(string from, string to) => new() { From = from, To = to, Installed = true };

    public static UpdateResult Failure(string from, string to, string reason) => new() { From = from, To = to, Reason = reason };

    /// <summary>Writes it for the next start; never fails (the log has the same).</summary>
    public void Write(string install, UpdateLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            AtomicFile.WriteAllText(UpdatePaths.Result(install), JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"could not write {UpdatePaths.Result(install)}: {ex.Message}");
        }
    }

    /// <summary>Reads and removes the install's result; null when there is none (or it cannot be read).</summary>
    public static UpdateResult? Read(string install)
    {
        try
        {
            string path = UpdatePaths.Result(install);
            return File.Exists(path) ? JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Consumes the editor's startup notification. Launcher status must use Read instead.</summary>
    public static UpdateResult? Take(string install)
    {
        string path = UpdatePaths.Result(install);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            UpdateResult? result = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(path), Json);
            File.Delete(path);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                // Read again at the next start.
            }

            return null;
        }
    }
}

/// <summary>Why an update was not installed, in words for the person using the editor.</summary>
public static class UpdateFailure
{
    public const string EditorDidNotClose = "The editor did not close in time.";

    public const string PlanDamaged = "The updater's instructions were damaged.";

    public const string FolderDamaged =
        "The editor's version.json is missing or damaged, so the updater could not tell what to replace. Extract the new version's zip over the folder instead.";

    public const string Incomplete = "The downloaded update was incomplete or damaged. It was deleted and is downloaded again next time.";

    public const string AccessDenied = "Windows did not let the updater move the editor's files (access denied).";

    public const string Locked = "Some of the editor's files were still in use (an antivirus scan, or a program with the folder open).";

    public const string Interrupted =
        "The update was interrupted (the computer shut down or the updater was stopped). The previous version was put back.";

    public const string RollbackFailed =
        "The update failed and the previous version could not be put back completely. Extract the release zip over the editor's folder to repair it.";

    public static string InUse(IEnumerable<FolderProcess> left)
    {
        string names = string.Join(", ", left.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase));
        return names.Length == 0
            ? "Other programs were running from the editor's folder."
            : $"Other programs were still running from the editor's folder ({names}). Close them, then restart the editor.";
    }

    public static string NameTaken(string name) =>
        $"The new version adds '{name}', and something of yours in the editor's folder already has that name. Move or rename it, then restart the editor.";

    public static string DidNotStart(string to) => $"Version {to} did not start, so the previous version was put back.";

    public static string OtherVersion(string found, string from) => $"The editor's folder holds version {found}, not {from}.";
}
