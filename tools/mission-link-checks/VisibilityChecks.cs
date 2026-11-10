using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Content.Loading;
using OpenScct.Core;
using OpenScct.Core.Engine;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Game.SBase;
using Actor = OpenScct.Editor.Core.Model.Actor;

internal static class VisibilityChecks
{
    public static void Run(Action<bool, string> check, string root, string? content)
    {
        MapProject p = ProjectStore.Load(Path.Combine(root, "Zones.osmap"));
        Actor device = p.FindActor("Computer3")!;
        Actor objective = p.FindActor("Task3")!;
        check(!ObjectiveVisibility.Hidden(p, device), "Inactive devices are shown by default");
        objective.Props.Set(ObjectiveVisibility.Property, SvJsonNode.Bool(true));
        check(ObjectiveVisibility.Hidden(p, device), "Objective visibility propagates to its device");
        Actor shared = p.FindActor("Task4")!;
        shared.Props.Set("Triggers", PropValues.Array([PropValues.ActorRef(device.Id), PropValues.ActorRef("Computer4")]));
        check(!ObjectiveVisibility.Hidden(p, device), "Shared device stays shown when any owner leaves hiding off");
        device.Props.Set(ObjectiveVisibility.Property, SvJsonNode.Bool(true));
        check(ObjectiveVisibility.Hidden(p, device), "An explicit device setting overrides shared owners");
        device.Props.Remove(ObjectiveVisibility.Property);
        shared.Props.Set("Triggers", PropValues.Array([PropValues.ActorRef("Computer4")]));
        var orphan = new Actor("UnlinkedComputer", "SBase.SComputerObjectiveTrigger");
        p.Actors.Add(orphan);
        string folder = Path.Combine(root, "Visibility.osmap");
        ProjectStore.Save(p, folder);
        p = ProjectStore.Load(folder);
        check(ObjectiveVisibility.Hidden(p, p.FindActor(device.Id)!), "Visibility choice survives save and reopen");
        var built = new MapCompiler().Compile(p, Path.Combine(root, "visibility-compiled"),
            new CompileOptions { FastLighting = true, ContentRoot = content, ProjectFolder = folder });
        check(built.Ok, "Visibility map passes strict compilation");
        var loaded = new MapLoader(ActorClassRegistry.Create(CorePackage.Info, EnginePackage.Info, SBasePackage.Info))
            .Load(new World(1), SvMap.Load(built.OutputFolder));
        var unlinked = (SComputerObjectiveTrigger)loaded.FindActor(orphan.Id)!;
        check(unlinked.HasInactiveVisibility && !unlinked.bHidden && !unlinked.bActive,
            "Compiled unlinked computer is visible and inactive from map load");
        check(!loaded.UnappliedProps.Any(p => p.Key.EndsWith(ObjectiveVisibility.Property, StringComparison.Ordinal)),
            "The game's property loader applies every visibility flag");
        var story = (SMission)loaded.FindActor("Story")!;
        story.Desactivate();
        story.Reset();
        var hidden = (SComputerObjectiveTrigger)loaded.FindActor(device.Id)!;
        var shown = (SComputerObjectiveTrigger)loaded.FindActor("Computer4")!;
        check(hidden.bHidden && !hidden.bActive && !shown.bHidden && !shown.bActive,
            "Pending Zone 2 obeys each objective's visibility choice after preparation");
        ((SObjective)loaded.FindActor("Task0")!).NotifyTaken(null, null);
        ((SObjective)loaded.FindActor("Task1")!).NotifyTaken(null, null);
        check(!hidden.bHidden && hidden.bActive && hidden.ComputerInteraction is { bUsable: true },
            "Completing Zone 1 reveals and activates the hidden Zone 2 objective");
        ((SObjective)loaded.FindActor("Task3")!).NotifyTaken(null, hidden);
        check(hidden.bHidden && !hidden.bActive && !shown.bHidden && !shown.bActive,
            "Completed objectives retain their selected inactive visibility");
    }
}
