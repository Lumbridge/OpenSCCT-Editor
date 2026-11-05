using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Viewports;
using OpenScctEditor.Viewports.Overlays;

namespace OpenScctEditor.Gameplay;

/// <summary>
/// The mover workbench's preview: one mover drawn where it is some seconds into an activation, as the game moves it
/// (<see cref="MoverTimeline"/>). Only the drawing moves; the map is not changed.
/// </summary>
public static class MoverPreview
{
    public static string? Id { get; private set; }

    /// <summary>Seconds into the activation.</summary>
    public static double Time { get; private set; }

    public static bool Playing { get; set; }

    public static event Action? Changed;

    /// <summary>Draws a mover at its pose <paramref name="seconds"/> into an activation.</summary>
    public static void Show(string id, double seconds)
    {
        if (EditorContext.Instance.Project.FindActor(id) is not { } a || !MoverBase.IsMover(a))
        {
            return;
        }

        if (Id is { } old && !string.Equals(old, id, StringComparison.OrdinalIgnoreCase))
        {
            ViewportLayout.Instance?.Scene.ClearPose(old);
        }

        Id = id;
        MoverTimeline timeline = MoverTimeline.Build(MoverSetup.Read(a));
        Time = Math.Clamp(seconds, 0, timeline.Duration);
        MoverPose pose = timeline.PoseAt(Time);
        ViewportLayout.Instance?.Scene.Pose(id, pose.Location, pose.Rotation);
        Changed?.Invoke();
    }

    /// <summary>Draws the mover where the map has it again.</summary>
    public static void Stop()
    {
        if (Id is { } id)
        {
            ViewportLayout.Instance?.Scene.ClearPose(id);
        }

        Id = null;
        Playing = false;
        Time = 0;
        Changed?.Invoke();
    }

    /// <summary>Moves a playing preview on, starting the activation again after its end.</summary>
    public static void Advance(double seconds)
    {
        if (!Playing || Id is not { } id || EditorContext.Instance.Project.FindActor(id) is not { } a)
        {
            return;
        }

        double duration = MoverTimeline.Build(MoverSetup.Read(a)).Duration;
        Show(id, duration <= 0 ? 0 : (Time + seconds) % duration);
    }
}

/// <summary>
/// Faint copies of the selected mover at each of its other keys (the mover workbench's ghosts), drawn while the Mover
/// Paths overlay is on. Only the drawing; the map is not changed.
/// </summary>
public static class MoverGhosts
{
    private static readonly Color Ghost = new(0.55f, 0.85f, 1f, 0.6f);

    public static void Update()
    {
        if (ViewportLayout.Instance?.Scene is not { } scene)
        {
            return;
        }

        EditorContext ctx = EditorContext.Instance;
        IViewOverlay? paths = ViewOverlays.All.FirstOrDefault(o => o.Id == "movers");
        if (paths is null || !ViewOverlays.IsOn(paths) || WorkbenchPart.Selected(ctx, MoverBase.IsMover).FirstOrDefault() is not { } mover)
        {
            scene.ClearGhosts();
            return;
        }

        MoverSetup m = MoverSetup.Read(mover);
        int drawnAt = MoverEdits.ShownKey(mover) ?? m.StartKey;
        scene.ShowGhosts(mover, Enumerable.Range(0, m.Keys.Count).Where(i => i != drawnAt).Select(i => (m.KeyPose(i).Location, m.KeyPose(i).Rotation)), Ghost);
    }
}

/// <summary>
/// The SMagicEvent workbench's preview: the firings of the activations played so far, so the views can mark what has
/// fired by the preview time, and what the events do by then, through other SMagicEvents too (<see cref="EventPreview"/>):
/// the movers they trigger drawn where one activation has taken them, and the trigger lights switched in the lit view
/// and the Lit Preview.
/// </summary>
public static class MagicPreview
{
    private static readonly HashSet<string> s_posed = new(StringComparer.OrdinalIgnoreCase);
    private static EventPreview? s_chain;
    private static IReadOnlyDictionary<string, bool>? s_lights;

    public static string? Id { get; private set; }

    public static double Time { get; private set; }

    public static IReadOnlyList<MagicFiring> Firings { get; private set; } = [];

    public static bool Playing { get; set; }

    public static double Length { get; private set; }

    /// <summary>What the events have done by the preview time, or null when nothing is previewed.</summary>
    public static EventState? State { get; private set; }

    public static event Action? Changed;

    /// <summary>Plays the SMagicEvent's activations through the game's logic and shows the state at <paramref name="seconds"/>.</summary>
    public static void Show(string id, IReadOnlyList<MagicActivation> activations, double seconds)
    {
        if (EditorContext.Instance.Project.FindActor(id) is not { } a || !MagicEvents.IsMagicEvent(a))
        {
            return;
        }

        Id = id;
        Firings = MagicEvents.Simulate(MagicEvents.Read(a), activations);
        // Played once here, through every relay, so the timeline reaches what relays do after this event's own end.
        s_chain = EventPreview.Build(EditorContext.Instance.Project, GameplayState.Instance.Graph, id, activations);
        Length = new[]
        {
            activations.Select(x => x.Time).DefaultIfEmpty(0).Max() + MagicEvents.Length(MagicEvents.Read(a)),
            Firings.Select(f => f.Time).DefaultIfEmpty(0).Max(),
            s_chain.End,
        }.Max() + 1;
        Time = Math.Clamp(seconds, 0, Length);
        Apply();
        ViewOverlays.Invalidate();
        Changed?.Invoke();
    }

    public static void Seek(double seconds)
    {
        Time = Math.Clamp(seconds, 0, Length);
        Apply();
        ViewOverlays.Invalidate();
        Changed?.Invoke();
    }

    public static void Stop()
    {
        Id = null;
        s_chain = null;
        Firings = [];
        Playing = false;
        Time = 0;
        Apply();
        ViewOverlays.Invalidate();
        Changed?.Invoke();
    }

    /// <summary>Works out what the events have done by the preview time; draws each mover a trigger has reached where
    /// one activation started by the latest such trigger has taken it (<see cref="MoverTimeline"/>), the others where the
    /// map has them; and switches the trigger lights (only when one changed, as that relights the views).</summary>
    private static void Apply()
    {
        MapProject p = EditorContext.Instance.Project;
        State = s_chain?.At(Time);
        if (ViewportLayout.Instance?.Scene is not { } scene)
        {
            return;
        }

        IReadOnlyDictionary<string, bool>? lights = State?.Lights;
        if (!SameLights(lights, s_lights))
        {
            s_lights = lights;
            scene.SetLightStates(lights);
        }

        IReadOnlyDictionary<string, double> started = State?.Movers ?? new Dictionary<string, double>();
        foreach (string gone in s_posed.Where(id => !started.ContainsKey(id)).ToList())
        {
            scene.ClearPose(gone);
            s_posed.Remove(gone);
        }

        foreach ((string id, double at) in started)
        {
            MoverPose pose = MoverTimeline.Build(MoverSetup.Read(p.FindActor(id)!)).PoseAt(Time - at);
            scene.Pose(id, pose.Location, pose.Rotation);
            s_posed.Add(id);
        }
    }

    private static bool SameLights(IReadOnlyDictionary<string, bool>? a, IReadOnlyDictionary<string, bool>? b) =>
        a is null || b is null ? a == b : a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out bool on) && on == kv.Value);

    public static void Advance(double seconds)
    {
        if (Playing && Id is not null)
        {
            Seek(Time + seconds > Length ? 0 : Time + seconds);
        }
    }
}
