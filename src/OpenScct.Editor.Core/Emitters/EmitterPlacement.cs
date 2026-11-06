using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Interop.Svmap;
using OpenScct.Editor.Core.Interop.T3d;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Emitters;

public sealed record EmitterPlacementResult(IEditCommand Command, IReadOnlyList<Actor> Actors);

/// <summary>Captures portable effects and places independent copies in a single map edit.</summary>
public static class EmitterPlacement
{
    private static readonly string[] s_mapProperties =
    [
        "Platform", "AttachTag", "Base", "Owner", "Level", "PhysicsVolume", "Group", "Region", "ForcedVisibilityZoneTag",
        "bMustInitNetChannels", "bHiddenEd", "bHiddenEdGroup", "bLockLocation",
    ];

    public static bool CanCapture(Actor actor, MapProject? project = null, string? contentRoot = null) =>
        CaptureSystems(actor, project, contentRoot) is { Count: > 0 };

    public static EmitterPreset Capture(string name, string category, IEnumerable<Actor> actors, MapProject? project = null,
        string? contentRoot = null)
    {
        name = name.Trim();
        category = category.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("Enter a name for the effect.");
        }

        var copies = new List<Actor>();
        foreach (Actor actor in actors)
        {
            if (CaptureSystems(actor, project, contentRoot) is not { Count: > 0 } systems)
            {
                continue;
            }

            Actor copy = actor.Clone();
            EmitterBuild.WithSystems(copy, systems, preserveOriginalObjects: true);
            copies.Add(copy);
        }

        if (copies.Count == 0)
        {
            throw new ArgumentException("Select at least one emitter with particle systems.");
        }

        Vec3 pivot = (copies.Select(a => a.Location).Aggregate(Vec3.Min) + copies.Select(a => a.Location).Aggregate(Vec3.Max)) * 0.5;
        var tags = copies.Select(a => a.Props.GetString("Tag")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Actor actor in copies)
        {
            actor.Location -= pivot;
            actor.Editor = new EditorMeta();
            RemoveMapProperties(actor);
            if (actor.Props.GetString("Event") is { } evt && !tags.Contains(evt))
            {
                actor.Props.Remove("Event");
            }
        }

        return new EmitterPreset(Guid.NewGuid().ToString("N"), name, category.Length == 0 ? "Other" : category,
            "", EmitterLibrary.ModeOf(copies), copies, EmitterLibrary.UserGroup);
    }

    public static EmitterPlacementResult Place(MapProject project, EmitterPreset preset, Vec3 at,
        double? loopMin = null, double? loopMax = null)
    {
        if (!double.IsFinite(at.X) || !double.IsFinite(at.Y) || !double.IsFinite(at.Z))
        {
            throw new ArgumentException("The placement point must be finite.");
        }

        if (preset.UnavailableReason is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        if ((loopMin ?? loopMax) is { } min)
        {
            preset = Loop(preset, min, loopMax ?? min);
        }

        if (preset.Actors.Count == 0)
        {
            throw new ArgumentException("The effect has no emitter actors.");
        }

        var used = project.AllRefs().Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        used.Add(T3dExport.LevelInfoName(project));
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Actor[] added = preset.Actors.Select(a =>
        {
            string id = InteropValues.FreeId(a.Id, used);
            renames.Add(a.Id, id);
            return a.Clone(id);
        }).ToArray();

        var usedTags = project.Actors.SelectMany(a => new[] { a.Tag, a.Props.GetString("Event") })
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string tag in added.Select(a => a.Props.GetString("Tag")).OfType<string>()
                     .Where(t => t.Length > 0 && !t.Equals("None", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            tags.Add(tag, InteropValues.FreeId($"FX_{added[0].Id}_{tag}", usedTags));
        }

        foreach (Actor actor in added)
        {
            actor.Location += at;
            actor.Editor = new EditorMeta { Folder = SvMapImport.EmittersFolder };
            RemoveMapProperties(actor);
            InteropValues.RenameActorRefs(actor.Props, renames);
            foreach (string key in new[] { "Tag", "Event" })
            {
                if (actor.Props.GetString(key) is { } old && tags.TryGetValue(old, out string? renamed))
                {
                    actor.Props.Set(key, SvJsonNode.String(renamed));
                }
            }
        }

        return new EmitterPlacementResult(new AddObjectsCommand($"Place {preset.Name}", actors: added), added);
    }

    /// <summary>Turns each particle system into a finite round followed by its own random reset delay.</summary>
    public static EmitterPreset Loop(EmitterPreset preset, double min, double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || min < 0 || max > 3600 || min > max)
        {
            throw new ArgumentException("Enter a loop wait between 0 and 3600 seconds, with the first number no larger than the second.");
        }

        var range = new SvJsonObject();
        range.Add("Min", SvJsonNode.Float((float)min));
        range.Add("Max", SvJsonNode.Float((float)max));
        Actor[] actors = preset.Actors.Select(a => a.Clone()).ToArray();
        bool foundSystems = false;
        foreach (Actor actor in actors)
        {
            if (EmitterBuild.SystemsOf(actor) is not { Count: > 0 } systems)
            {
                continue;
            }

            foundSystems = true;
            // OpenSCCT applies the reset range on the particle systems. Its map loader ignores the owner's range.
            actor.Props.Set("AutoReset", SvJsonNode.False);
            actor.Props.Set("AutoDestroy", SvJsonNode.False);
            EmitterBuild.WithSystems(actor, new SvJsonArray(systems.Select(s => s is SvJsonObject part ? LoopPart(part, range) : s)));
        }

        if (!foundSystems)
        {
            throw new ArgumentException("This effect has no particle systems to loop.");
        }

        return preset with { Actors = actors, Mode = "periodic" };
    }

    private static SvJsonObject LoopPart(SvJsonObject part, SvJsonObject range)
    {
        PropSet props = PropSet.FromJson(part["props"]);
        if (props.GetBool("Disabled") && !props.GetBool("TriggerDisabled", true))
        {
            return part;
        }

        props.Set("Disabled", SvJsonNode.False);
        props.Set("AutoDestroy", SvJsonNode.False);
        props.Set("AutoReset", SvJsonNode.True);
        props.Set("AutoResetTimeRange", range);
        props.Set("RespawnDeadParticles", SvJsonNode.False);
        props.Set("ParticlesPerSecond", SvJsonNode.Float(0));
        var burst = PropSet.FromJson(props["SpawnOnTriggerRange"]);
        int count = (int)Math.Clamp(Math.Floor(Math.Max(burst.GetFloat("Min"), burst.GetFloat("Max"))), 0, 4096);
        if (count > 0)
        {
            float rate = props.GetFloat("SpawnOnTriggerPPS");
            props.Remove("SpawnOnTriggerRange");
            props.Remove("SpawnOnTriggerPPS");
            props.Set("MaxParticles", SvJsonNode.Integer(count));
            props.Set("AutomaticInitialSpawning", SvJsonNode.False);
            props.Set("InitialParticlesPerSecond", SvJsonNode.Float(rate > 0 ? rate : 10000));
        }

        var result = PropSet.FromJson(part);
        result.Set("props", props.ToJson());
        return result.ToJson();
    }

    private static void RemoveMapProperties(Actor actor)
    {
        foreach (string key in s_mapProperties)
        {
            actor.Props.Remove(key);
        }
    }

    private static SvJsonArray? CaptureSystems(Actor actor, MapProject? project, string? contentRoot)
    {
        if (EmitterBuild.SystemsOf(actor, project) is { } systems)
        {
            return systems;
        }

        return actor.Source?.Contains(T3dImport.ObjectsKey, StringComparison.Ordinal) == true
            ? T3dImport.ParticleSystemsOf(actor, new T3dTypes(UeSchema.Load(contentRoot ?? EditorServices.ContentRoot), project?.Id ?? "MyLevel"))
            : null;
    }
}
