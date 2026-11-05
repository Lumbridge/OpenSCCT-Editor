using System.Diagnostics;
using Godot;
using OpenScctEditor.Shell.Background;

namespace OpenScctEditor.Shell;

/// <summary>
/// The editor's shutdown fence. Work off the main thread runs through <see cref="BackgroundJobs"/>, which hands it
/// <see cref="Token"/> and counts its named jobs here. Once the fence closes (<see cref="Quit"/>, or the scene tree exiting by any
/// other route, such as Godot's own <c>--quit-after</c>) the token is cancelled, results posted back to the main thread
/// are dropped, and worker threads may no longer call the engine (<see cref="TryEngine"/>). A quit hides the window at
/// once and lets named jobs (build, export, import...) wind down for up to <see cref="DrainTime"/>; loads and
/// previews are not waited for, since their results are dropped and their cache files are written whole or not at
/// all. Before the tree quits, no worker is inside an engine call and none can start one, so nothing touches Godot
/// while it shuts down.
/// </summary>
public static class EditorLifetime
{
    /// <summary>How long a quit waits for named jobs in flight before it quits without them.</summary>
    public static readonly TimeSpan DrainTime = TimeSpan.FromSeconds(2);

    /// <summary>How long the fence then waits for a worker that is inside an engine call (one image decode, say).</summary>
    private static readonly TimeSpan s_engineCallTime = TimeSpan.FromSeconds(5);

    private static readonly CancellationTokenSource s_quit = new();
    private static readonly object s_gate = new();
    private static int s_jobs;
    private static int s_inEngine;
    private static bool s_closed;

    /// <summary>Cancelled when the editor starts to quit.</summary>
    public static CancellationToken Token => s_quit.Token;

    /// <summary>True once the editor has started to quit (it cannot be called off).</summary>
    public static bool IsQuitting => s_quit.IsCancellationRequested;

    /// <summary>Raised on the main thread when the editor starts to quit, before any job is waited for.</summary>
    public static event Action? Quitting;

    /// <summary>Named jobs (<see cref="BackgroundJobs.Run{T}(string, Func{CancellationToken, T}, Action{T})"/>) started
    /// and not yet finished: the ones a quit waits for.</summary>
    public static int JobsInFlight => Volatile.Read(ref s_jobs);

    internal static void JobStarted() => Interlocked.Increment(ref s_jobs);

    internal static void JobEnded() => Interlocked.Decrement(ref s_jobs);

    /// <summary>Runs <paramref name="action"/> on the main thread at the end of this frame (call it from the main
    /// thread; workers use <see cref="BackgroundJobs.Post"/>). Dropped once the editor is quitting, also when it was
    /// queued before: the nodes it would touch are going away, and Godot runs what is left in its queue after the
    /// tree has been freed.</summary>
    public static void Defer(Action action) => Callable.From(() =>
    {
        if (!IsQuitting)
        {
            action();
        }
    }).CallDeferred();

    /// <summary>
    /// Runs engine work on a worker thread (decoding or scaling an <see cref="Image"/>, posting to the main thread)
    /// unless the editor is quitting. Returns false, without running it, once the quit has begun: the engine may be
    /// shutting down. Keep the work short; the quit waits for it.
    /// </summary>
    public static bool TryEngine(Action work)
    {
        lock (s_gate)
        {
            if (s_closed)
            {
                return false;
            }

            s_inEngine++;
        }

        try
        {
            work();
            return true;
        }
        finally
        {
            lock (s_gate)
            {
                s_inEngine--;
                Monitor.PulseAll(s_gate);
            }
        }
    }

    /// <summary>
    /// Quits the editor: closes the fence (cancels background work and drops its results), hides the window, waits
    /// (frames still running) until named jobs in flight are done or <see cref="DrainTime"/> has passed, then quits
    /// the tree with <paramref name="exitCode"/>. A second call while quitting does nothing.
    /// </summary>
    public static void Quit(SceneTree tree, int exitCode = 0)
    {
        if (!Close())
        {
            return;
        }

        // The editor is gone for the user from here on: nothing they click or type can reach it, and the window
        // does not stay on screen while jobs wind down.
        tree.Root.GuiDisableInput = true;
        BackgroundWindow.HideForQuit();

        var clock = Stopwatch.StartNew();
        void Check()
        {
            if (JobsInFlight > 0 && clock.Elapsed < DrainTime)
            {
                return;
            }

            tree.ProcessFrame -= Check;
            Finish();
        }

        void Finish()
        {
            if (JobsInFlight > 0)
            {
                GD.Print($"Quit: {JobsInFlight} background job(s) still running after {DrainTime.TotalSeconds:0} s; quitting without them.");
            }

            WaitForWorkers();
            tree.Quit(exitCode);
        }

        if (JobsInFlight == 0)
        {
            Finish();
            return;
        }

        tree.ProcessFrame += Check;
    }

    /// <summary>
    /// The scene tree is exiting (called from the main window's <c>_ExitTree</c>). After <see cref="Quit"/> this does
    /// nothing; when the tree quit some other way (Godot's <c>--quit-after</c>) it closes the fence here, before the
    /// engine shuts down, and waits for any worker inside an engine call.
    /// </summary>
    public static void TreeExiting()
    {
        Close();
        WaitForWorkers();
    }

    /// <summary>Closes the fence: no worker may enter the engine, the token is cancelled and <see cref="Quitting"/> is
    /// raised. Returns false when it was already closed.</summary>
    private static bool Close()
    {
        lock (s_gate)
        {
            if (s_closed)
            {
                return false;
            }

            s_closed = true;
        }

        s_quit.Cancel();
        try
        {
            Quitting?.Invoke();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Quit: {ex.Message}");
        }

        return true;
    }

    private static void WaitForWorkers()
    {
        if (!WaitForEngineCalls(s_engineCallTime))
        {
            GD.PrintErr("Quit: a background job is still inside an engine call; quitting anyway.");
        }
    }

    /// <summary>Waits until no worker is inside <see cref="TryEngine"/> (none can enter once the fence closed).</summary>
    private static bool WaitForEngineCalls(TimeSpan wait)
    {
        var clock = Stopwatch.StartNew();
        lock (s_gate)
        {
            while (s_inEngine > 0)
            {
                TimeSpan left = wait - clock.Elapsed;
                if (left <= TimeSpan.Zero)
                {
                    return false;
                }

                Monitor.Wait(s_gate, left);
            }
        }

        return true;
    }
}
