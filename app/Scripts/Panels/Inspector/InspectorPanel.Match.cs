using Godot;
using OpenScct.Core.Match;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>The selected actors' player range: the counts they are in the match for, and their state outside and
/// inside it (movers can hold a key). Win targets and the layout at a count are in the Match panel.</summary>
public partial class InspectorPanel
{
    /// <summary>Option ids: the default state, hidden, then key n as n.</summary>
    private const int StateDefault = -2;
    private const int StateHidden = -1;

    private void MatchSection()
    {
        List<ObjectRef> actors = _refs.Where(r => r.Kind == ObjectKind.Actor).ToList();
        PanelUi.Header(_sections, "Match size");
        List<Actor> Actors() => actors.Select(Index.Actor).OfType<Actor>().ToList();
        IEditCommand Set(string name, Func<ActorMatch, ActorMatch> change) => ReplaceObjectsCommand.Edit(Context.Project, actors, name,
            actor: a => a.Match = change(a.Match ?? new ActorMatch(PlayerRange.Any)).OrNull(), mergeKey: $"inspector:match:{name}");

        HBoxContainer row = PanelUi.Row(_sections, "Players", "Everyone in the match, bots included; 0 for no limit");
        AxisFields range = AxisFields.Create(["From", "To"], words: true);
        row.AddChild(range);
        range.Edited += (axis, v, final) =>
        {
            int n = Math.Clamp((int)Math.Round(v), 0, MatchRuleSet.MostPlayers);
            if (final && Actors().Any(a => (axis == 0 ? Range(a).Min : Range(a).Max) != n))
            {
                Execute(Set("Set player range", m => m with { Players = WithEnd(m.Players, axis == 0, n) }));
            }
        };
        _syncers.Add(() =>
        {
            var ranges = Actors().Select(Range).Distinct().ToList();
            range.SetValues(ranges is [var r] ? [r.Min, r.Max] : [null, null]);
        });

        bool movers = Actors().Count > 0 && Actors().All(MatchPlan.IsMover);
        int keys = movers ? Actors().Min(a => Math.Max((int)ClassDefaults.Number(a, "NumKeys", 1), 1)) : 0;
        var outside = new List<(string, int)> { (movers ? "Default: closed at key 0" : "Default: left out", StateDefault) };
        var inside = new List<(string, int)> { ("Default: as built", StateDefault), ("Hidden", StateHidden) };
        if (!movers)
        {
            outside.Add(("Hidden", StateHidden));
        }

        for (int key = 0; movers && key < keys; key++)
        {
            outside.Add(($"Held at key {key}", key));
            inside.Add(($"Held at key {key}" + (key == keys - 1 && key > 0 ? " (open)" : ""), key));
        }

        if (movers)
        {
            outside.Add(("Hidden", StateHidden));
        }

        OptionRow("Outside", "What the actor does in a match outside its range", outside,
            () => Actors().Select(a => StateId(a.Match?.Outside)).ToList(),
            id => Set("Set outside state", m => m with { Outside = HoldOf(id) }));
        OptionRow("Inside", "What the actor does in a match inside its range", inside,
            () => Actors().Select(a => StateId(a.Match?.Inside)).ToList(),
            id => Set("Set inside state", m => m with { Inside = HoldOf(id) }));

        PanelContainer baked = PanelUi.Callout(_sections, Severity.Warning,
            "Lights and static meshes are baked into the map for every count: gating one is a build error.");
        Label win = PanelUi.Caption(_sections, "Win targets are set (Match panel).");
        _syncers.Add(() =>
        {
            List<Actor> list = Actors();
            baked.Visible = list.Any(a => a.Match is { HasRule: true } && MatchPlan.IsRenderOnly(a));
            win.Visible = list.Any(a => a.Match is { HasWin: true });
        });
    }

    private static PlayerRange Range(Actor a) => a.Match?.Players ?? PlayerRange.Any;

    /// <summary>
    /// The range with one end set to <paramref name="n"/>; the other end follows when it would pass it, so the range
    /// never stores a minimum above its maximum.
    /// </summary>
    private static PlayerRange WithEnd(PlayerRange r, bool min, int n) =>
        min ? new PlayerRange(n, r.Max > 0 && n > r.Max ? n : r.Max)
            : new PlayerRange(r.Min > 0 && n > 0 && n < r.Min ? n : r.Min, n);

    // "normal" is the default inside the range (and never valid outside it), so a stored "normal" shows as the default.
    private static int StateId(Hold? hold) => hold switch
    {
        null or { Kind: HoldKind.Normal } => StateDefault,
        { Kind: HoldKind.Hidden } => StateHidden,
        { Key: var key } => key,
    };

    private static Hold? HoldOf(int id) => id switch
    {
        StateDefault => null,
        StateHidden => Hold.Hidden,
        _ => Hold.AtKey(id),
    };
}
