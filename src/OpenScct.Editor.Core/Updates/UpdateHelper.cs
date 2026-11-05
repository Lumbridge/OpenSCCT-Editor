using System.Diagnostics;
using OpenScct.Launcher.Install;

namespace OpenScct.Editor.Core.Updates;

/// <summary>
/// Starts the program that swaps the install after the editor quits: a copy of a build's <c>ose</c> folder in
/// <c>updates\helper\&lt;name&gt;</c> (a program cannot run from a folder being moved). It runs hidden, with no console
/// window, and inherits the editor's environment (background runs stay in the background).
/// </summary>
public static class UpdateHelper
{
    private const string CompleteMark = ".complete";

    /// <summary>Copies <paramref name="cliFolder"/> to the helper folder <paramref name="name"/> (once; a complete copy
    /// is reused) and returns the helper's exe.</summary>
    public static string Prepare(UpdatePaths paths, string cliFolder, string name)
    {
        string target = paths.Helper(name);
        string exe = Path.Combine(target, EditorRelease.CliExeName);
        if (File.Exists(Path.Combine(target, CompleteMark)) && File.Exists(exe))
        {
            return exe;
        }

        string temp = target + ".copying";
        SafeDelete.Tree(temp, paths.Root);
        SafeDelete.Tree(target, paths.Root);
        CopyTree(cliFolder, temp);
        Directory.Move(temp, target);
        File.WriteAllText(Path.Combine(target, CompleteMark), "");
        return exe;
    }

    /// <summary>Starts <c>ose update &lt;args&gt;</c> from the helper folder.</summary>
    public static Process? Start(string helperExe, UpdatePaths paths, params string[] args)
    {
        Directory.CreateDirectory(paths.Root);
        var start = new ProcessStartInfo(helperExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = paths.Root,
        };
        start.ArgumentList.Add("update");
        foreach (string a in args)
        {
            start.ArgumentList.Add(a);
        }

        return Process.Start(start);
    }

    /// <summary>Waits until the helper has written its ready file and returns what it says
    /// (<see cref="UpdateSwap.ReadyWaiting"/> or <see cref="UpdateSwap.ReadyBusy"/>); null when it has not after
    /// <paramref name="timeout"/> or it exited without.</summary>
    public static string? WaitReady(Process helper, string readyFile, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(helper);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (TryRead(readyFile) is { } said)
            {
                return said;
            }

            if (helper.HasExited || clock.Elapsed >= timeout)
            {
                return TryRead(readyFile);
            }

            Thread.Sleep(50);
        }
    }

    private static string? TryRead(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string dir in Directory.EnumerateDirectories(from))
        {
            if (!new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
            }
        }
    }
}
