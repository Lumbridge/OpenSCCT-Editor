using System.Diagnostics;
using System.Globalization;
using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Session;

/// <summary>What a running editor left in its marker: its process, when it started, the map it had open and its
/// newest autosave.</summary>
public sealed record SessionMarker(string File, int ProcessId, DateTime StartedUtc, string? Title, string? Autosave);

/// <summary>
/// Tells a crash from a clean exit. Each running editor keeps a marker file named after its process in a folder, and
/// deletes it when it closes normally. A marker whose process is no longer running belongs to an editor that crashed
/// or was killed, and its newest autosave is the one to offer back. Several editors can run at once: each has its own
/// marker, and a running one is never taken for a crashed one.
/// </summary>
public static class SessionMarkers
{
    private const string Format = "osmap.session/1";

    /// <summary>Creates this process's marker in <paramref name="folder"/>; returns its path. The name carries the
    /// start time as well as the process id, so an editor that gets a crashed one's reused id has its own marker.</summary>
    public static string Create(string folder)
    {
        Directory.CreateDirectory(folder);
        DateTime started = StartTimeUtc(Process.GetCurrentProcess()) ?? DateTime.UtcNow;
        string name = $"editor-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-{started.Ticks.ToString(CultureInfo.InvariantCulture)}.json";
        string path = Path.Combine(folder, name);
        Write(new SessionMarker(path, Environment.ProcessId, started, null, null));
        return path;
    }

    /// <summary>Records the open map and the autosave that holds its unsaved work: null when there is none (the map
    /// was just opened or saved), so a crash then offers nothing older than the map's own files.</summary>
    public static void Update(string path, string? title, string? autosave)
    {
        if (Read(path) is { } m)
        {
            Write(m with { Title = title, Autosave = autosave });
        }
    }

    /// <summary>Deletes a marker (a clean exit, or a crash that has been dealt with).</summary>
    public static void Remove(string path) => File.Delete(path);

    /// <summary>The markers of editors that are no longer running, newest start first.</summary>
    public static IReadOnlyList<SessionMarker> Crashed(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.GetFiles(folder, "editor-*.json")
            .Select(Read)
            .OfType<SessionMarker>()
            .Where(m => !IsRunning(m))
            .OrderByDescending(m => m.StartedUtc)
            .ToList();
    }

    private static bool IsRunning(SessionMarker m)
    {
        try
        {
            using Process p = Process.GetProcessById(m.ProcessId);
            // A reused process id belongs to another program that started later. When its start time cannot be read
            // (another user's process), only a process of the editor's own name can be the one that wrote the marker.
            return StartTimeUtc(p) is { } started
                ? Math.Abs((started - m.StartedUtc).TotalSeconds) < 2
                : string.Equals(p.ProcessName, Path.GetFileNameWithoutExtension(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static DateTime? StartTimeUtc(Process p)
    {
        try
        {
            return p.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static void Write(SessionMarker m)
    {
        var o = new SvJsonObject();
        o.Add("format", SvJsonNode.String(Format));
        o.Add("pid", SvJsonNode.Integer(m.ProcessId));
        o.Add("started", SvJsonNode.String(m.StartedUtc.ToString("O", CultureInfo.InvariantCulture)));
        if (m.Title is not null)
        {
            o.Add("title", SvJsonNode.String(m.Title));
        }

        if (m.Autosave is not null)
        {
            o.Add("autosave", SvJsonNode.String(m.Autosave));
        }

        File.WriteAllBytes(m.File + ".tmp", CanonicalJson.ToFileBytes(o));
        File.Move(m.File + ".tmp", m.File, overwrite: true);
    }

    private static SessionMarker? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || SvJson.Parse(File.ReadAllBytes(path), Path.GetFileName(path)) is not SvJsonObject o
                || o["pid"] is not SvJsonNumber pid || !pid.TryGetInt32(out int id))
            {
                return null;
            }

            DateTime started = o["started"] is SvJsonString s && DateTime.TryParse(s.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t)
                ? t.ToUniversalTime() : DateTime.MinValue;
            return new SessionMarker(path, id, started, (o["title"] as SvJsonString)?.Value, (o["autosave"] as SvJsonString)?.Value);
        }
        catch (Exception ex) when (ex is IOException or OpenScct.Content.SvMapFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
