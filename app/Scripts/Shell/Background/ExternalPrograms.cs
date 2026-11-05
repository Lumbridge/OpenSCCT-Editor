using System.Diagnostics;
using Godot;

namespace OpenScctEditor.Shell.Background;

/// <summary>The one place the editor starts other programs or opens Explorer. Background runs start nothing, so no
/// game or folder window appears on the user's screen.</summary>
public static class ExternalPrograms
{
    /// <summary>Opens a folder in Explorer (the folder is made first).</summary>
    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        if (!BackgroundMode.Refuse($"opening {folder}"))
        {
            OS.ShellOpen(folder);
        }
    }

    /// <summary>Opens a web page or a file (in the program Windows opens it with).</summary>
    public static void Open(string urlOrFile)
    {
        if (!BackgroundMode.Refuse($"opening {urlOrFile}"))
        {
            OS.ShellOpen(urlOrFile);
        }
    }

    /// <summary>Starts a program; returns its process id. Throws when it cannot start, or in background mode.</summary>
    public static int Start(ProcessStartInfo info)
    {
        if (BackgroundMode.Refuse($"starting {info.FileName}"))
        {
            throw new InvalidOperationException("background runs do not start other programs.");
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException($"{info.FileName} did not start.");
        return process.Id;
    }
}
