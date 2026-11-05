using System.Globalization;

namespace OpenScct.Editor.Core.Updates;

/// <summary>The updater's log (<c>updates\update.log</c>): one line per step, appended, trimmed to its newer half
/// once it passes about 1 MB. Writing never fails an update.</summary>
public sealed class UpdateLog
{
    private const long MaxBytes = 1 << 20;
    private readonly object _gate = new();

    public UpdateLog(string path)
    {
        Path = path;
    }

    public string Path { get; }

    /// <summary>Also writes each line here (the command line tool's console).</summary>
    public TextWriter? Echo { get; init; }

    public void Write(string message)
    {
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{Environment.ProcessId}] {message}");
        Echo?.WriteLine(message);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                {
                    string text = File.ReadAllText(Path);
                    int cut = text.IndexOf('\n', text.Length / 2);
                    File.WriteAllText(Path, cut < 0 ? "" : text[(cut + 1)..]);
                }

                File.AppendAllText(Path, line + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log is for diagnosis only.
            }
        }
    }
}
