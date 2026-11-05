using System.Collections.Concurrent;
using Godot;

namespace OpenScctEditor.Shell.Shots;

/// <summary>Collects engine errors and warnings (any thread) between screenshots, so each shot in the manifest lists
/// what went wrong while it was being set up.</summary>
public sealed partial class ShotLog : Logger
{
    private readonly ConcurrentQueue<string> _lines = new();

    public void Add(string line) => _lines.Enqueue(line);

    /// <summary>Everything collected since the last call.</summary>
    public List<string> Drain()
    {
        var list = new List<string>();
        while (_lines.TryDequeue(out string? line))
        {
            list.Add(line);
        }

        return list;
    }

    public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType,
        Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        string kind = errorType == (int)ErrorType.Warning ? "WARNING" : "ERROR";
        _lines.Enqueue($"{kind}: {(rationale.Length > 0 ? rationale : code)} ({Path.GetFileName(file)}:{line} {function})");
    }

    public override void _LogMessage(string message, bool error)
    {
        if (error)
        {
            _lines.Enqueue(message.TrimEnd());
        }
    }
}
