using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Identity;

/// <summary>
/// A copy of the game that starts in the background (OpenSCCT's background <c>override.cfg</c> beside it): Godot reads
/// the settings file beside the exe before any of the game's code runs, so the window starts minimised and never takes
/// the focus, and the game's own folder is not touched. The copy is the exe, the console exe, the pack and the .NET
/// data folder, hard-linked into the work folder (copied when the game is on another drive).
/// </summary>
internal static class BackgroundGame
{
    /// <summary>The settings that start the window minimised and never focused.</summary>
    private const string Settings = "[display]\n\nwindow/size/no_focus=true\nwindow/size/mode=1\n";

    /// <summary>
    /// Makes the copy of the game beside <paramref name="exe"/> (the main or the console exe) in <paramref name="folder"/>
    /// and returns the exe to run: the copy's console build when there is one (its output can be read). Throws an
    /// <see cref="IOException"/> saying why when the copy cannot be made: the game is never started without the settings.
    /// </summary>
    public static string Create(string exe, string folder, Action<string>? progress)
    {
        string from = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        string main = MainName(exe);
        try
        {
            IEnumerable<string> files = Directory.EnumerateFiles(from, main + ".*")
                .Concat(Directory.EnumerateDirectories(from, "data_*").SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)));
            bool copying = false;
            foreach (string file in files)
            {
                string link = Path.Combine(folder, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                if (!HardLink(link, file))
                {
                    if (!copying)
                    {
                        copying = true;
                        progress?.Invoke($"the game cannot be linked into {folder} ({LinkError()}): copying it");
                    }

                    File.Copy(file, link);
                }
            }

            string copy = Path.Combine(folder, main + ".exe");
            if (!File.Exists(copy))
            {
                throw new IOException($"{main}.exe is not beside {Path.GetFileName(exe)}");
            }

            File.WriteAllText(Path.Combine(folder, "override.cfg"), Settings);
            return GameExe.ConsoleOf(copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"the game was not started: its background copy in {folder} could not be made ({ex.Message}), "
                + "and without it the game window could take the focus", ex);
        }
    }

    /// <summary>The name of the main exe without its extension: <c>OpenSCCT</c> for both OpenSCCT.exe and
    /// OpenSCCT.console.exe (whose pack and main exe are named after the main exe).</summary>
    private static string MainName(string exe)
    {
        string name = Path.GetFileNameWithoutExtension(exe);
        return name.EndsWith(".console", StringComparison.OrdinalIgnoreCase) ? name[..^".console".Length] : name;
    }

    /// <summary>Makes <paramref name="link"/> a second name of the existing file <paramref name="target"/> (same drive only).</summary>
    private static bool HardLink(string link, string target) =>
        OperatingSystem.IsWindows() ? CreateHardLinkW(link, target, IntPtr.Zero) : UnixLink(target, link) == 0;

    private static string LinkError() => new Win32Exception(Marshal.GetLastPInvokeError()).Message;

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int UnixLink(string oldPath, string newPath);
}
