using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Audio;

/// <summary>Sound placement and editing use the original engine's property units.</summary>
public static class SoundAuthoring
{
    public static string? Sound(Actor actor) => PropValues.AssetPath(actor.Props["AmbientSound"] ??
        ClassDefaults.Catalog.Default(actor.Class, "AmbientSound"));

    public static double Number(Actor actor, string property, double fallback) =>
        (actor.Props[property] ?? ClassDefaults.Catalog.Default(actor.Class, property)) is SvJsonNumber n ? PropValues.Num(n) : fallback;

    public static Actor Create(MapProject project, string sound, Vec3 at, double volume = 128, double pitch = 64, double radius = 64)
    {
        string stem = "Sound_" + AssetPaths.Safe(AssetPaths.Split(sound).Name);
        string id = stem;
        var used = project.AllRefs().Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 2; used.Contains(id); i++) id = stem + "_" + i;
        var actor = new Actor(id, "Engine.AmbientSound") { Location = at, Editor = new EditorMeta { Folder = "Sounds" } };
        Set(actor, sound, volume, pitch, radius);
        return actor;
    }

    public static void Set(Actor actor, string sound, double volume, double pitch, double radius)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sound);
        if (!double.IsFinite(volume) || !double.IsFinite(pitch) || !double.IsFinite(radius))
            throw new ArgumentException("Sound settings must be finite numbers.");
        actor.Props.Set("AmbientSound", PropValues.AssetRef(sound));
        actor.Props.Set("SoundVolume", SvJsonNode.Integer((int)Math.Round(Math.Clamp(volume, 0, 255))));
        actor.Props.Set("SoundPitch", SvJsonNode.Integer((int)Math.Round(Math.Clamp(pitch, 1, 255))));
        actor.Props.Set("SoundRadius", SvJsonNode.Float((float)Math.Clamp(radius, 0, 255)));
    }

    public static ReplaceObjectsCommand Edit(IEnumerable<Actor> actors, string sound, double volume, double pitch, double radius)
    {
        var command = new ReplaceObjectsCommand("Edit sound sources");
        foreach (Actor actor in actors)
        {
            Actor copy = actor.Clone();
            Set(copy, sound, volume, pitch, radius);
            if (copy.Props.ToJson().ToString() != actor.Props.ToJson().ToString()) command.Actor(actor, copy);
        }
        return command;
    }
}
