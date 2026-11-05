using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenScct.Editor.Core.Updates;

/// <summary>A process whose program file is inside a folder.</summary>
public sealed record FolderProcess(int Pid, string Name, string Path);

/// <summary>
/// Which processes run a program from a folder: other editors of the same install, and its <c>ose</c> started by other
/// programs. An update swaps the folder only once none is left. Processes this account cannot look into are skipped.
/// </summary>
public static class InstallProcesses
{
    private const uint QueryLimitedInformation = 0x1000;

    /// <summary>Every process (but this one) whose program file is inside <paramref name="folder"/>.</summary>
    public static List<FolderProcess> Running(string folder)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var found = new List<FolderProcess>();
        int self = Environment.ProcessId;
        foreach (Process p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == self || p.Id == 0)
                {
                    continue;
                }

                string? path = ProgramPath(p);
                if (path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(new FolderProcess(p.Id, p.ProcessName, path));
                }
            }
        }

        return found;
    }

    /// <summary>Waits until no process runs from <paramref name="folder"/>; false when some still do after
    /// <paramref name="timeout"/> (<paramref name="left"/> names them).</summary>
    public static bool WaitUntilFree(string folder, TimeSpan timeout, out List<FolderProcess> left)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            left = Running(folder);
            if (left.Count == 0)
            {
                return true;
            }

            if (clock.Elapsed >= timeout)
            {
                return false;
            }

            Thread.Sleep(500);
        }
    }

    /// <summary>Waits for one process to end; true when it has (or never ran).</summary>
    public static bool WaitForExit(int pid, TimeSpan timeout)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static string? ProgramPath(Process p)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                IntPtr handle = OpenProcess(QueryLimitedInformation, false, p.Id);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var text = new StringBuilder(32_768);
                    int size = text.Capacity;
                    return QueryFullProcessImageName(handle, 0, text, ref size) ? text.ToString(0, size) : null;
                }
                finally
                {
                    CloseHandle(handle);
                }
            }

            if (OperatingSystem.IsLinux())
            {
                return File.ResolveLinkTarget($"/proc/{p.Id}/exe", returnFinalTarget: false)?.FullName;
            }

            return p.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
