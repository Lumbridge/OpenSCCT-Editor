using Godot;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Gameplay;

/// <summary>Contextual, searchable linking with an explicit review before one undoable edit.</summary>
public static class MissionLinkGuide
{
    private static readonly (string Kind, string Title)[] s_options =
    [ ("mission", "Link to mission…"), ("objective", "Link to objective…"),
      ("objectives", "Link to objectives…"), ("devices", "Link to devices…") ];

    public static Actor? Selected(EditorContext ctx) => ctx.Selection.Items.Count == 1
        && ctx.Selection.Items.First() is { Kind: ObjectKind.Actor } r ? ctx.Project.FindActor(r.Id) : null;

    public static bool Allows(Actor? actor, string kind) => actor is not null && kind switch
    {
        "mission" => MissionLinks.IsObjective(actor) || MissionLinks.IsTrigger(actor),
        "objective" => MissionLinks.IsTrigger(actor),
        "objectives" => MatchPlan.IsMission(actor),
        "devices" => MissionLinks.IsObjective(actor),
        _ => false,
    };

    public static void Register(EditorContext ctx, Control anchor)
    {
        foreach ((string kind, string title) in s_options)
            ctx.AddAction(new EditorAction("gameplay.link." + kind, title, "Gameplay/Link", () =>
            {
                if (Selected(ctx) is { } actor) Open(anchor, ctx, actor.Id, kind);
            }) { IsEnabled = () => Allows(Selected(ctx), kind), Description = "Choose compatible actors, review the links, then create them in one undo step" });
    }

    public static bool Fill(PopupMenu menu, Control anchor, EditorContext ctx, string? id)
    {
        menu.Clear();
        Actor? actor = id is null ? null : ctx.Project.FindActor(id);
        foreach ((string kind, string title) in s_options)
        {
            if (!Allows(actor, kind)) continue;
            int index = menu.ItemCount;
            menu.AddItem(title, index);
            menu.SetItemMetadata(index, kind);
        }
        return menu.ItemCount > 0;
    }

    public static void Menu(Control anchor, EditorContext ctx, string id)
    {
        var menu = new PopupMenu();
        if (!Fill(menu, anchor, ctx, id)) { menu.Free(); return; }
        menu.IdPressed += index => Open(anchor, ctx, id, menu.GetItemMetadata((int)index).AsString());
        menu.PopupHide += () => menu.QueueFree();
        anchor.AddChild(menu);
        PanelUi.PopupAtMouse(anchor, menu);
    }

    public static void Open(Control anchor, EditorContext ctx, string sourceId, string kind)
    {
        if (ctx.Project.FindActor(sourceId) is not { } source || !Allows(source, kind)) return;
        MapProject project = ctx.Project;
        bool parentChosen = kind is "mission" or "objective";
        bool Candidate(Actor a) => kind switch
        {
            "mission" => MatchPlan.IsMission(a),
            "objective" => MissionLinks.IsObjective(a) && !MatchPlan.IsMission(a),
            "objectives" => MissionLinks.IsObjective(a),
            "devices" => MissionLinks.IsTrigger(a),
            _ => false,
        };
        List<Actor> candidates = project.Actors.Where(Candidate).Where(a =>
        {
            Actor parent = parentChosen ? a : source;
            Actor child = parentChosen ? source : a;
            return MissionLinks.Refusal(project, parent, child) is null
                && !MissionLinks.Contains(parent, MissionLinks.IsTrigger(child) ? "Triggers" : "Objectives", child.Id);
        }).ToList();
        string noun = kind is "mission" ? "mission" : kind is "objective" ? "objective" : kind;
        string instruction = parentChosen
            ? $"Choose the {noun} that {MissionLinks.Label(source)} belongs to. Existing links stay in place."
            : $"Choose the {noun} to add to {MissionLinks.Label(source)}. You can check several and search again.";
        string Review(IReadOnlyList<string> ids) => string.Join("\n", ids.Select(id =>
        {
            string target = project.FindActor(id) is { } actor ? MissionLinks.Label(actor) : id;
            return parentChosen ? $"{target} → {MissionLinks.Label(source)}" : $"{MissionLinks.Label(source)} → {target}";
        })) + "\n\nThese links are added together. Undo removes this step.";
        MissionActorPicker.Open(anchor, candidates, "1. " + instruction, !parentChosen,
            a => ClassNames.Title(a.Class), ids =>
            {
                if (!ReferenceEquals(project, ctx.Project)) { ctx.SetStatus("The map changed; start linking again."); return; }
                try
                {
                    string parent = parentChosen ? ids[0] : sourceId;
                    IReadOnlyList<string> children = parentChosen ? [sourceId] : ids;
                    if (MissionLinks.Add(project, parent, children) is not { } command) { ctx.SetStatus("These actors are already linked."); return; }
                    ctx.Execute(command);
                    ctx.Selection.Set([ObjectRef.Actor(parent)]);
                    MissionsPanel.OpenForSelection();
                    ctx.SetStatus($"Linked {PanelUi.Count(children.Count, "item")} to {MissionLinks.Name(project.FindActor(parent)!)}. Undo restores the previous links.");
                }
                catch (ArgumentException ex) { ctx.SetStatus(ex.Message); }
            }, review: Review, emptyHint: $"No compatible unlinked {noun}. Create one in Missions, or choose another actor.");
    }
}

[ShellPart(Order = -26)]
public sealed class MissionLinkActions : IShellPart
{
    public void Attach(MainWindow window, EditorContext context) => MissionLinkGuide.Register(context, window);
}
