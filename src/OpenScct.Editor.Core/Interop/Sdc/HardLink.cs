using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>File hard links (a second name for the same file; no admin rights needed, same drive only).</summary>
internal static class HardLink
{
    /// <summary>Why the last <see cref="Create"/> failed.</summary>
    public static string LastError { get; private set; } = "";

    /// <summary>Makes <paramref name="link"/> a hard link to the existing file <paramref name="target"/>.</summary>
    public static bool Create(string link, string target)
    {
        bool ok = OperatingSystem.IsWindows() ? CreateHardLinkW(Extended(link), Extended(target), IntPtr.Zero) : UnixLink(target, link) == 0;
        if (!ok)
        {
            LastError = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
        }

        return ok;
    }

    /// <summary>The extended-length (\\?\) form of a Windows path, so a stand-in deep under a long data folder may pass
    /// 260 characters (.NET's own file calls do this themselves; a direct kernel32 call does not).</summary>
    private static string Extended(string path)
    {
        string full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
            : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int UnixLink(string oldPath, string newPath);
}
