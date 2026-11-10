using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

public partial class MissionsPanel
{
    private Button _parentMission = null!;

    private List<Actor> Parents => Mission is { } mission ? Context.Project.Actors
        .Where(a => MatchPlan.IsMission(a) && MissionLinks.Contains(a, "Objectives", mission.Id)).ToList() : [];

    private void ChooseParent()
    {
        List<Actor> parents = Parents;
        void Pick(string id)
        {
            SetMission(id);
            Context.Selection.Set([ObjectRef.Actor(id)]);
        }
        if (parents.Count == 1) Pick(parents[0].Id);
        else if (parents.Count > 1)
            MissionActorPicker.Open(_parentMission, parents, "Choose the parent mission", false, ModeName, ids => Pick(ids[0]));
    }

    private void NewZone()
    {
        if (Mission is not { } parent || Dialogs.Host is null) return;
        MapProject project = Context.Project;
        string parentId = parent.Id;
        int count = MissionTree.Refs(parent.Props["Objectives"]).Count(id => project.FindActor(id) is { } a && MatchPlan.IsMission(a));
        MissionZoneDialog.Open(_missionPicker, MissionLinks.Name(parent), $"Zone {count + 1}", (name, needed, sequential) =>
        {
            if (ReferenceEquals(project, Context.Project))
            {
                try
                {
                    string id = project.NewId("Zone");
                    Context.Execute(MissionLinks.CreateZone(project, parentId, id, name, needed, sequential));
                    SetMission(id);
                    Context.Selection.Set([ObjectRef.Actor(id)]);
                    Context.SetStatus($"{name} created. Add its objectives and devices. Parent mission returns to {MissionLinks.Name(project.FindActor(parentId)!)} to add the next zone.");
                }
                catch (ArgumentException ex) { Context.SetStatus(ex.Message); }
            }
            else Context.SetStatus("The map changed; create the zone again in the new map.");
        });
    }
}
