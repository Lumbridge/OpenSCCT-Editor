using Godot;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;

namespace OpenScctEditor.Shell;

/// <summary>
/// Runs slow work (build, check, import, export, texture and mesh loads, previews) off the main thread so the editor
/// never freezes, then hands the result back on the main thread. Work gets a snapshot of the map, never the live
/// project. Every job gets <see cref="EditorLifetime"/>'s quit token: once the editor starts to quit no job starts,
/// results are no longer posted, and engine calls from workers go through <see cref="EditorLifetime.TryEngine"/>.
/// Named jobs (<see cref="Run{T}(string, Func{CancellationToken, T}, Action{T})"/>) are counted, and a quit waits a
/// moment for them; loads and previews (<see cref="Start(Action{CancellationToken})"/>) are not.
/// </summary>
public static class BackgroundJobs
{
    private static int s_running;

    /// <summary>Names of the jobs running now (the status bar shows them).</summary>
    public static IReadOnlyCollection<string> Running => s_names.Keys.ToArray();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> s_names = new();

    public static bool IsBusy => Volatile.Read(ref s_running) > 0;

    /// <summary>Raised on the main thread when a job starts or ends.</summary>
    public static event Action? Changed;

    /// <summary>True while a job with this name runs (one build at a time, one import at a time...).</summary>
    public static bool IsRunning(string name) => s_names.ContainsKey(name);

    /// <summary>A named job (shown in the status bar, one at a time per name): <paramref name="work"/> on a worker,
    /// then <paramref name="done"/> with its result on the main thread.</summary>
    public static void Run<T>(string name, Func<T> work, Action<T> done) => Run(name, _ => work(), done);

    /// <summary>A named job whose work stops when the token is cancelled (the editor is quitting).</summary>
    public static void Run<T>(string name, Func<CancellationToken, T> work, Action<T> done)
    {
        if (EditorLifetime.IsQuitting)
        {
            return;
        }

        if (!s_names.TryAdd(name, 0))
        {
            EditorContext.Instance.Log($"{name} is already running.", Severity.Warning);
            return;
        }

        Interlocked.Increment(ref s_running);
        Changed?.Invoke();
        EditorContext.Instance.SetStatus(name + "…");
        Launch(cancel =>
        {
            try
            {
                T result = work(cancel);
                Post(() => Finish(name, () => done(result)));
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // The editor is closing; nobody waits for the result.
            }
            catch (Exception ex)
            {
                Post(() => Finish(name, () => EditorContext.Instance.Log($"{name} failed: {ex.Message}", Severity.Error)));
            }

            return Task.CompletedTask;
        }, counted: true);
    }

    /// <summary>Starts <paramref name="work"/> on a worker thread with the quit token. Returns false (nothing started)
    /// once the editor is quitting. Engine calls in the work go through <see cref="EditorLifetime.TryEngine"/>.</summary>
    public static bool Start(Action<CancellationToken> work) => Start(cancel =>
    {
        work(cancel);
        return Task.CompletedTask;
    });

    /// <summary>Starts asynchronous <paramref name="work"/> on a worker thread with the quit token (see
    /// <see cref="Start(Action{CancellationToken})"/>).</summary>
    public static bool Start(Func<CancellationToken, Task> work) => Launch(work, counted: false);

    /// <summary>Starts <paramref name="work"/> on a worker thread; a <paramref name="counted"/> job holds a quit back
    /// for a moment (see <see cref="EditorLifetime.JobsInFlight"/>).</summary>
    private static bool Launch(Func<CancellationToken, Task> work, bool counted)
    {
        if (EditorLifetime.IsQuitting)
        {
            return false;
        }

        CancellationToken cancel = EditorLifetime.Token;
        if (counted)
        {
            EditorLifetime.JobStarted();
        }

        Task.Run(async () =>
        {
            try
            {
                cancel.ThrowIfCancellationRequested();
                await work(cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // The editor is closing.
            }
            catch (Exception ex)
            {
                Post(() => EditorContext.Instance.Log($"Background work failed: {ex.Message}", Severity.Error));
            }
            finally
            {
                if (counted)
                {
                    EditorLifetime.JobEnded();
                }
            }
        });
        return true;
    }

    /// <summary>Starts <paramref name="work"/> on a worker thread and returns its task (a cancelled task once the
    /// editor is quitting).</summary>
    public static Task<T> StartTask<T>(Func<CancellationToken, T> work)
    {
        if (EditorLifetime.IsQuitting)
        {
            return Task.FromCanceled<T>(EditorLifetime.Token);
        }

        CancellationToken cancel = EditorLifetime.Token;
        return Task.Run(() => work(cancel));
    }

    /// <summary>Runs an action on the main thread at the next idle point (safe from any thread). Dropped once the
    /// editor is quitting, also when it was posted before.</summary>
    public static void Post(Action action) => EditorLifetime.TryEngine(() => Callable.From(() =>
    {
        if (!EditorLifetime.IsQuitting)
        {
            action();
        }
    }).CallDeferred());

    private static void Finish(string name, Action done)
    {
        s_names.TryRemove(name, out _);
        Interlocked.Decrement(ref s_running);
        try
        {
            done();
        }
        finally
        {
            Changed?.Invoke();
        }
    }
}
