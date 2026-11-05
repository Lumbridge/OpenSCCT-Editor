using System.Runtime.CompilerServices;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Analysis;

/// <summary>
/// The compiled analysis map of an open session, kept until the project changes, so overlays, queries and Map Check
/// share one compile. <see cref="Version"/> counts the session's edits; a map stored for an older version is stale.
/// </summary>
public sealed class AnalysisCache
{
    private static readonly ConditionalWeakTable<EditorSession, AnalysisCache> s_caches = new();
    private readonly EditorSession _session;
    private readonly object _gate = new();
    private AnalysisMap? _map;
    private int _mapVersion = -1;
    private int _version;

    private AnalysisCache(EditorSession session)
    {
        _session = session;
        session.ProjectChanged += _ => Interlocked.Increment(ref _version);
    }

    public static AnalysisCache Of(EditorSession session) => s_caches.GetValue(session, s => new AnalysisCache(s));

    /// <summary>Bumped by every edit of the session's project.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>The stored map when it matches the project as it is now, else null.</summary>
    public AnalysisMap? Current
    {
        get
        {
            lock (_gate)
            {
                return _mapVersion == Version ? _map : null;
            }
        }
    }

    /// <summary>The current map, compiled from the session's project now when there is none (on the caller's thread).</summary>
    public AnalysisMap Get(Action<string>? progress = null)
    {
        if (Current is { } map)
        {
            return map;
        }

        int version = Version;
        map = AnalysisMap.Build(_session.Project, _session.Folder, progress);
        Store(map, version);
        return map;
    }

    /// <summary>
    /// Keeps a map built from a snapshot taken at <paramref name="version"/> (a background build in the app); a map for
    /// an older project is dropped.
    /// </summary>
    public void Store(AnalysisMap map, int version)
    {
        lock (_gate)
        {
            if (version == Version)
            {
                _map = map;
                _mapVersion = version;
            }
        }
    }
}
