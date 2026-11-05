using System.Diagnostics;
using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Scripting.Live;

/// <summary>A running editor that takes live connections, as its discovery file describes it.</summary>
public sealed record LiveInstance(int Pid, int Port, string Token, string? Map, string? Folder, string Version, DateTime Started, string File);

/// <summary>
/// Discovery files for live editing. Each running editor writes <c>editor-&lt;pid&gt;.json</c> under
/// <c>%LOCALAPPDATA%\OpenSCCT Editor\live</c> (the user's own app data; a run with <c>OSE_USER_DATA</c> uses its
/// <c>live</c> subfolder, see <see cref="UserFolders"/>) with its port and a random token; clients read it to connect.
/// Files of editors that are no longer running are ignored and cleaned up.
/// </summary>
public static class LiveDiscovery
{
    /// <summary>The discovery folder: <c>OSE_LIVE_DIR</c>, else <see cref="UserFolders.Live"/>.</summary>
    public static string Folder =>
        Environment.GetEnvironmentVariable("OSE_LIVE_DIR") is { Length: > 0 } dir
            ? dir
            : UserFolders.Live;

    public static string FileFor(int pid) => Path.Combine(Folder, $"editor-{pid.ToString(CultureInfo.InvariantCulture)}.json");

    /// <summary>Writes (or rewrites) this process's discovery file.</summary>
    public static string Write(int port, string token, string? map, string? folder, string version, DateTime started)
    {
        int pid = Environment.ProcessId;
        string file = FileFor(pid);
        Directory.CreateDirectory(Folder);
        SvJsonObject o = ApiJson.Obj(
            ("pid", SvJsonNode.Integer(pid)),
            ("port", SvJsonNode.Integer(port)),
            ("token", ApiJson.Str(token)),
            ("map", map is null ? null : ApiJson.Str(map)),
            ("folder", folder is null ? null : ApiJson.Str(folder)),
            ("version", ApiJson.Str(version)),
            ("started", ApiJson.Str(started.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))));
        // Write then move, so a client never reads half a file.
        string temp = file + ".tmp";
        File.WriteAllText(temp, ApiJson.Write(o));
        File.Move(temp, file, overwrite: true);
        return file;
    }

    public static void Remove(int pid)
    {
        try
        {
            File.Delete(FileFor(pid));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The running editors, newest first. Files of editors that exited are deleted.</summary>
    public static List<LiveInstance> Running()
    {
        var list = new List<LiveInstance>();
        if (!Directory.Exists(Folder))
        {
            return list;
        }

        foreach (string file in Directory.GetFiles(Folder, "editor-*.json"))
        {
            LiveInstance? instance = Read(file);
            if (instance is null)
            {
                continue;
            }

            if (!IsAlive(instance.Pid))
            {
                TryDelete(file);
                continue;
            }

            list.Add(instance);
        }

        return list.OrderByDescending(i => i.Started).ToList();
    }

    /// <summary>The editor to connect to: the one with this pid, or the newest running one.</summary>
    public static LiveInstance Find(int? pid = null)
    {
        List<LiveInstance> running = Running();
        if (running.Count == 0)
        {
            throw new ApiException("no OpenSCCT Editor is running with live editing on (start the editor, or open the project " +
                "folder instead: ose serve <map.osmap>)");
        }

        if (pid is not { } p)
        {
            return running[0];
        }

        return running.FirstOrDefault(i => i.Pid == p)
            ?? throw new ApiException($"no running editor has pid {p} (running: {string.Join(", ", running.Select(i => i.Pid))})", "pid");
    }

    private static LiveInstance? Read(string file)
    {
        try
        {
            if (ApiJson.Parse(File.ReadAllText(file), "discovery file") is not SvJsonObject o)
            {
                return null;
            }

            int pid = (int)ApiJson.Number(o, "pid", 0);
            int port = (int)ApiJson.Number(o, "port", 0);
            string? token = ApiJson.GetString(o, "token");
            if (pid <= 0 || port <= 0 || token is null)
            {
                return null;
            }

            DateTime started = DateTime.TryParse(ApiJson.GetString(o, "started"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime t) ? t : File.GetLastWriteTimeUtc(file);
            return new LiveInstance(pid, port, token, ApiJson.GetString(o, "map"), ApiJson.GetString(o, "folder"),
                ApiJson.GetString(o, "version") ?? "", started, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ApiException)
        {
            return null;
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
