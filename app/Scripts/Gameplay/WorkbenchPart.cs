using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Assets;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports.Overlays;

namespace OpenScctEditor.Gameplay;

/// <summary>Copied actor properties and which of them a paste takes (the Properties panel ticks them).</summary>
public static class PropertyClipboard
{
    public static CopiedProperties? Copied { get; private set; }

    /// <summary>The properties a paste takes.</summary>
    public static HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

    public static event Action? Changed;

    public static void Set(CopiedProperties copy)
    {
        Copied = copy;
        Keys.Clear();
        Keys.UnionWith(PropertyCopy.DefaultKeys(copy));
        Changed?.Invoke();
    }

    public static void Tick(string key, bool on)
    {
        if (on)
        {
            Keys.Add(key);
        }
        else
        {
            Keys.Remove(key);
        }

        Changed?.Invoke();
    }
}

/// <summary>How the next zone on-complete action is added: its delay and type, and a new message's lines (the
/// Missions panel sets them).</summary>
public static class ZoneActionDraft
{
    public static double Delay { get; set; }

    public static MagicType Type { get; set; } = MagicType.Trigger;

    public static string Spy { get; set; } = "Zone complete";

    public static string Merc { get; set; } = "Zone complete";
}

/// <summary>
/// The gameplay workbenches' actions: mover keys and preview, SMagicEvent timelines, camera networks, zone tips, zone
/// on-complete actions, and copy/paste of properties and property sets. The workbench panels run these too; all work
/// headless through <c>--run=</c> and report on the status line.
/// </summary>
[ShellPart(Order = -27)]
public sealed class WorkbenchPart : IShellPart
{
    private EditorContext _ctx = null!;

    public void Attach(MainWindow window, EditorContext context)
    {
        _ctx = context;
        context.SessionReplaced += () =>
        {
            MoverPreview.Stop();
            MagicPreview.Stop();
        };
        context.SelectionChanged += MoverGhosts.Update;
        context.ProjectChanged += _ => MoverGhosts.Update();
        ViewOverlays.Switched += MoverGhosts.Update;
        MoverActions();
        MagicActions();
        CameraActions();
        TipActions();
        MissionActions();
        PropertyActions();
    }

    /// <summary>The selected movers, SMagicEvents, cameras...: the selected actors that pass a test, in selection order.</summary>
    public static List<Actor> Selected(EditorContext ctx, Func<Actor, bool> test) =>
        ctx.Selection.Items.Where(r => r.Kind == ObjectKind.Actor).Select(r => ctx.Project.FindActor(r.Id)).OfType<Actor>().Where(test).ToList();

    private void Add(string id, string title, string menu, Action run, int order, string? description = null, Func<bool>? enabled = null,
        Func<bool>? isChecked = null, string? shortcut = null) =>
        _ctx.AddAction(new EditorAction(id, title, menu, run)
        {
            Order = order,
            Description = description,
            IsEnabled = enabled ?? (() => true),
            IsChecked = isChecked,
            Shortcut = shortcut,
        });

    /// <summary>Runs a workbench edit, reporting a refusal (an invalid value, the 8-key limit...) on the status line.</summary>
    private bool Run(Func<IEditCommand> edit, string done)
    {
        try
        {
            IEditCommand cmd = edit();
            if (cmd is ReplaceObjectsCommand { IsEmpty: true })
            {
                _ctx.SetStatus("Nothing to change");
                return false;
            }

            _ctx.Execute(cmd);
            _ctx.SetStatus(done);
            return true;
        }
        catch (ArgumentException ex)
        {
            _ctx.SetStatus(ex.Message);
            return false;
        }
    }

    // ---- movers -----------------------------------------------------------------------------------------------------

    private Actor? Mover => Selected(_ctx, MoverBase.IsMover).FirstOrDefault();

    private void MoverActions()
    {
        const string Menu = "Gameplay/Movers";
        Add("gameplay.mover.addKey", "Add Key", Menu, () => Run(() => MoverEdits.AddKey(_ctx.Project, Mover!.Id), $"Added a key to {Mover!.Id}"), 10,
            "Adds a key one leg further along the selected mover's last leg (8 keys at most: the game's arrays hold 8)",
            () => Mover is { } m && MoverSetup.Read(m).Keys.Count < MoverSetup.MaxKeys);
        Add("gameplay.mover.addKeyHere", "Add Key at View Centre", Menu,
            () => Run(() => MoverEdits.AddKey(_ctx.Project, Mover!.Id, _ctx.PlacementPoint?.Invoke()), $"Added a key to {Mover!.Id}"), 20,
            "Adds a key where the view looks", () => Mover is { } m && MoverSetup.Read(m).Keys.Count < MoverSetup.MaxKeys);
        Add("gameplay.mover.deleteKey", "Delete Last Key", Menu,
            () => Run(() => MoverEdits.DeleteKey(_ctx.Project, Mover!.Id, MoverSetup.Read(Mover!).Keys.Count - 1), $"Deleted the last key of {Mover!.Id}"), 30,
            enabled: () => Mover is { } m && MoverSetup.Read(m).Keys.Count > 1);
        Add("gameplay.mover.showNextKey", "Show Next Key", Menu, ShowNextKey, 40,
            "Draws the selected mover at its next key; moving or turning it in the views then places that key (as the stock editor's key pick)",
            () => Mover is { } m && MoverSetup.Read(m).Keys.Count > 1);
        Add("gameplay.mover.showStart", "Show Where It Starts", Menu,
            () => Run(() => MoverEdits.ShowKey(_ctx.Project, Mover!.Id, MoverSetup.Read(Mover!).StartKey), $"{Mover!.Id} drawn where the round starts it"), 50,
            "Puts the selected mover back at its start key, so moving it moves the whole mover again",
            () => Mover is { } m && MoverEdits.ShownKey(m) is not null);
        Add("gameplay.mover.previewClosed", "Preview Closed", Menu, () => PreviewMover(open: false), 100,
            "Draws the selected mover where an activation starts (the map is not changed)", () => Mover is not null);
        Add("gameplay.mover.previewOpen", "Preview Open", Menu, () => PreviewMover(open: true), 110,
            "Draws the selected mover at its open key", () => Mover is not null);
        Add("gameplay.mover.play", "Play Preview", Menu, () =>
        {
            if (MoverPreview.Id is null && Mover is { } m)
            {
                MoverPreview.Show(m.Id, 0);
            }

            MoverPreview.Playing = !MoverPreview.Playing;
            _ctx.SetStatus(MoverPreview.Playing ? $"Playing {MoverPreview.Id}" : $"Paused at {MoverPreview.Time:0.##} s");
        }, 120, enabled: () => Mover is not null || MoverPreview.Id is not null, isChecked: () => MoverPreview.Playing);
        Add("gameplay.mover.stopPreview", "Stop Preview", Menu, () =>
        {
            MoverPreview.Stop();
            _ctx.SetStatus("Movers drawn where the map has them");
        }, 130, enabled: () => MoverPreview.Id is not null);
    }

    /// <summary>Draws the selected mover at the key after the one it is drawn at (from its start key round to it).</summary>
    private void ShowNextKey()
    {
        Actor m = Mover!;
        MoverSetup setup = MoverSetup.Read(m);
        int next = ((MoverEdits.ShownKey(m) ?? setup.StartKey) + 1) % setup.Keys.Count;
        string done = next == setup.StartKey
            ? $"{m.Id} drawn where the round starts it"
            : $"{m.Id} drawn at key {next}: move or turn it in the views to place key {next}";
        Run(() => MoverEdits.ShowKey(_ctx.Project, m.Id, next), done);
    }

    private void PreviewMover(bool open)
    {
        Actor m = Mover!;
        MoverTimeline timeline = MoverTimeline.Build(MoverSetup.Read(m));
        double at = open ? timeline.OpenAt ?? 0 : 0;
        MoverPreview.Playing = false;
        MoverPreview.Show(m.Id, at);
        MoverPose pose = timeline.PoseAt(at);
        _ctx.SetStatus($"{m.Id} {(open ? "open" : "closed")} at {pose.Location} ({at:0.##} s into an activation)");
    }

    // ---- SMagicEvent ------------------------------------------------------------------------------------------------

    private Actor? Magic => Selected(_ctx, MagicEvents.IsMagicEvent).FirstOrDefault();

    private void MagicActions()
    {
        const string Menu = "Gameplay/Event Sequencer";
        Add("gameplay.magic.add", "Add Event Firing Selection", Menu, AddMagic, 10,
            "A new SMagicEvent (the stock event sequencer) whose one group fires each selected actor; give it delays in its panel",
            () => _ctx.Selection.Items.Any(r => r.Kind == ObjectKind.Actor));
        Add("gameplay.magic.play", "Play Preview", Menu, () =>
        {
            if (MagicPreview.Id is null && Magic is { } m)
            {
                MagicPreview.Show(m.Id, [new MagicActivation(0)], 0);
            }

            MagicPreview.Playing = !MagicPreview.Playing;
            _ctx.SetStatus(MagicPreview.Playing ? $"Playing {MagicPreview.Id}: {MagicPreview.Firings.Count} firings" : $"Paused at {MagicPreview.Time:0.##} s");
        }, 20, enabled: () => Magic is not null || MagicPreview.Id is not null, isChecked: () => MagicPreview.Playing);
        Add("gameplay.magic.stopPreview", "Stop Preview", Menu, MagicPreview.Stop, 30, enabled: () => MagicPreview.Id is not null);
        Add("gameplay.magic.copyJson", "Copy Timeline JSON", Menu, () =>
        {
            string text = CanonicalJson.ToPrettyString(MagicEvents.Export(_ctx.Project, Magic!));
            Panels.PanelUi.Copy(text);
            _ctx.Log(text);
            _ctx.SetStatus($"Copied {Magic!.Id}'s timeline ({MagicEvents.ExchangeFormat}, the RE+ exchange format)");
        }, 40, enabled: () => Magic is not null);
        Add("gameplay.magic.pasteJson", "Paste Timeline JSON", Menu, () => ImportMagic(DisplayServer.ClipboardGet()), 50,
            "Replaces the selected SMagicEvent's groups (and StopActor) with a scct.smagic-event document from the clipboard",
            () => Magic is not null && DisplayServer.HasFeature(DisplayServer.Feature.Clipboard));
    }

    private void AddMagic()
    {
        List<Actor> targets = Selected(_ctx, _ => true);
        var parts = new List<IEditCommand>();
        var actions = new List<MagicAction>();
        foreach (Actor t in targets)
        {
            (string tag, bool isNew) = Wiring.TagFor(_ctx.Project, t);
            if (isNew)
            {
                parts.Add(ReplaceObjectsCommand.Edit(_ctx.Project, [ObjectRef.Actor(t.Id)], $"Tag {t.Id}", actor: a => a.Props.Set("Tag", SvJsonNode.String(tag))));
            }

            actions.Add(new MagicAction(tag, 0));
        }

        string id = GeShapes.IdSource(_ctx.Project)("SMagicEvent");
        AddObjectsCommand create = MagicEvents.Create(_ctx.Project, GameplayPart.PlacementPoint(_ctx) + new Vec3(0, 0, 64), id, [new MagicGroup(actions)]);
        parts.Add(create);
        _ctx.Execute(new CompositeCommand($"Add SMagicEvent firing {targets.Count} actors", parts));
        _ctx.Selection.Set(create.Added);
        _ctx.SetStatus($"Added {create.Added.First().Id} (Tag {id}): fire it from a sender to run its {actions.Count} actions");
    }

    private void ImportMagic(string text)
    {
        try
        {
            (List<MagicGroup>? groups, string? stop, bool hasStop) = MagicEvents.Import(SvJson.Parse(text, "clipboard"));
            string id = Magic!.Id;
            var parts = new List<IEditCommand>();
            if (groups is not null)
            {
                parts.Add(MagicEvents.SetGroups(_ctx.Project, id, groups));
            }

            if (hasStop)
            {
                parts.Add(MagicEvents.SetStopActor(_ctx.Project, id, stop));
            }

            Run(() => new CompositeCommand($"Paste timeline into {id}", parts), $"Pasted the timeline into {id}");
        }
        catch (Exception ex) when (ex is OpenScct.Content.SvMapFormatException or ArgumentException or InvalidDataException)
        {
            _ctx.SetStatus("The clipboard does not hold a timeline: " + ex.Message);
        }
    }

    // ---- camera networks --------------------------------------------------------------------------------------------

    private List<string> Cameras => Selected(_ctx, CameraNetworks.IsNetworkCamera).Select(a => a.Id).ToList();

    private void CameraActions()
    {
        const string Menu = "Gameplay/Camera Network";
        Add("gameplay.camnet.ring", "Link Selected into a Ring", Menu,
            () => Run(() => CameraNetworks.Link(_ctx.Project, Cameras, loop: true), $"Linked {Cameras.Count} cameras into a ring"), 10,
            "Links the selected network cameras in selection order, the last back to the first; the first opens the CamNet view",
            () => Cameras.Count >= 2);
        Add("gameplay.camnet.line", "Link Selected in a Line", Menu,
            () => Run(() => CameraNetworks.Link(_ctx.Project, Cameras, loop: false), $"Linked {Cameras.Count} cameras in a line"), 20,
            enabled: () => Cameras.Count >= 2);
        Add("gameplay.camnet.remove", "Take Selected Out of Their Network", Menu,
            () => Run(() => CameraNetworks.Remove(_ctx.Project, Cameras), $"Took {Cameras.Count} cameras out; their neighbours are joined"), 30,
            enabled: () => Cameras.Count >= 1);
    }

    // ---- tips -------------------------------------------------------------------------------------------------------

    private void TipActions()
    {
        const string Menu = "Gameplay/Help and Tips";
        Add("gameplay.tip.add", "Add Zone Tip with Help Marker", Menu, () => AddTip(marker: true), 10,
            "A hint cylinder (shown with in-game help) and an info marker below it, as the stock maps pair them");
        Add("gameplay.tip.addOnly", "Add Zone Tip", Menu, () => AddTip(marker: false), 20);
    }

    private void AddTip(bool marker)
    {
        Vec3 at = GameplayPart.PlacementPoint(_ctx);
        try
        {
            string? mesh = marker ? HelpTips.DefaultMarkerMesh(_ctx.Project, () => AssetService.Instance.Catalog) : null;
            AddObjectsCommand add = HelpTips.Place(_ctx.Project, at, "New hint", marker: marker, mesh: mesh);
            _ctx.Execute(add);
            _ctx.Selection.Set(add.Added.Take(1));
            _ctx.SetStatus($"Added {add.Added.First().Id}: write its hint in the Tips panel");
        }
        catch (ArgumentException ex)
        {
            _ctx.SetStatus(ex.Message);
        }
    }

    // ---- zone on-complete actions -----------------------------------------------------------------------------------

    private Actor? Mission => Selected(_ctx, a => ClassDefaults.IsA(a, "SBase.SObjective")).FirstOrDefault();

    private void MissionActions()
    {
        const string Menu = "Gameplay/Zones";
        Add("gameplay.zone.fireSelected", "On Complete: Fire Selected", Menu, () =>
        {
            Actor mission = Mission!;
            List<Actor> targets = Selected(_ctx, a => !ReferenceEquals(a, mission));
            Run(() => ZoneActions.Add(_ctx.Project, mission.Id, targets.Select(t => t.Id).ToList(), ZoneActionDraft.Delay, ZoneActionDraft.Type),
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{mission.Id} fires {targets.Count} actor{(targets.Count == 1 ? "" : "s")} {ZoneActionDraft.Delay:0.##} s after it completes"));
        }, 10, "The selected mission (or objective) fires the other selected actors when it is complete, through its own SMagicEvent relay, " +
            "with the delay and type set in the Missions panel", () => Mission is not null && _ctx.Selection.Count >= 2);
        Add("gameplay.zone.message", "On Complete: Show a Message", Menu,
            () => Run(() => ZoneActions.AddMessage(_ctx.Project, Mission!.Id, ZoneActionDraft.Spy, ZoneActionDraft.Merc, delay: ZoneActionDraft.Delay),
                $"{Mission!.Id} shows \"{ZoneActionDraft.Spy}\" when complete"), 20,
            "The selected mission shows the Missions panel's message when it is complete", () => Mission is not null);
    }

    // ---- properties -------------------------------------------------------------------------------------------------

    private void PropertyActions()
    {
        Add("edit.props.copy", "Copy Properties", "Edit", () =>
        {
            Actor a = _ctx.Project.FindActor(_ctx.Selection.Primary!.Value.Id)!;
            PropertyClipboard.Set(PropertyCopy.Copy(a));
            Panels.PanelUi.Copy(CanonicalJson.ToCanonicalString(PropertyClipboard.Copied!.ToJson()));
            _ctx.SetStatus($"Copied {a.Props.Count} properties of {a.Id}; {PropertyClipboard.Keys.Count} will paste (tick them in the Properties panel)");
        }, 115, "Copies the actor's stored values to paste onto other actors", () => _ctx.Selection.Primary is { Kind: ObjectKind.Actor }, shortcut: "Ctrl+Alt+C");
        Add("edit.props.paste", "Paste Properties", "Edit", PasteProperties, 135,
            "Pastes the ticked copied values onto the selected actors (those their class has)",
            () => _ctx.Selection.Items.Any(r => r.Kind == ObjectKind.Actor), shortcut: "Ctrl+Alt+V");
        Add("gameplay.propset.importIni", "Import PropertiesSets.ini…", "Gameplay/Property Sets", () =>
            Dialogs.OpenFile("Import the original game's PropertiesSets.ini", ["*.ini ; Property sets"], ImportIni), 10, "Reads the stock editor's property sets from an original install (System/PropertiesSets.ini) into your library");
    }

    private void ImportIni(string file)
    {
        try
        {
            List<PropertySet> sets = PropertySets.ImportIni(file);
            PropertySets.Save(PropertySets.LibraryFile, sets);
            _ctx.Log($"Imported {sets.Count} property sets into {PropertySets.LibraryFile}");
            _ctx.SetStatus($"Imported {sets.Count} property sets");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OpenScct.Content.SvMapFormatException)
        {
            _ctx.Log($"Cannot import {file}: {ex.Message}", Severity.Error);
        }
    }

    private void PasteProperties()
    {
        // A copy made in another editor window (or another map) arrives as text on the system clipboard.
        if (DisplayServer.HasFeature(DisplayServer.Feature.Clipboard) && CopiedProperties.TryParse(DisplayServer.ClipboardGet()) is { } fromClipboard
            && !SameCopy(fromClipboard, PropertyClipboard.Copied))
        {
            PropertyClipboard.Set(fromClipboard);
        }

        if (PropertyClipboard.Copied is not { } copy)
        {
            _ctx.SetStatus("Copy an actor's properties first");
            return;
        }

        var skipped = new List<string>();
        List<string> ids = Selected(_ctx, _ => true).Select(a => a.Id).ToList();
        if (Run(() => PropertyCopy.Paste(_ctx.Project, copy, PropertyClipboard.Keys, ids, skipped), $"Pasted {PropertyClipboard.Keys.Count} properties of {copy.From}")
            && skipped.Count > 0)
        {
            _ctx.Log($"Not pasted (the class has no such property): {string.Join(", ", skipped)}", Severity.Warning);
        }
    }

    private static bool SameCopy(CopiedProperties a, CopiedProperties? b) =>
        b is not null && CanonicalJson.ToCanonicalString(a.ToJson()) == CanonicalJson.ToCanonicalString(b.ToJson());
}
