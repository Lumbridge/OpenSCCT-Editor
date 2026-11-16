using OpenScct.Content.Json;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Audio;

/// <summary>Sound placement and editing use the original engine's property units.</summary>
public static class SoundAuthoring
{
    public static bool Loops(Actor actor) => actor.Props["SoundLoop"]?.Kind != SvJsonKind.False;
    public static double RepeatInterval(Actor actor) => Number(actor, "SoundRepeatInterval", 0);
    public static string? Sound(Actor actor) => PropValues.AssetPath(actor.Props["AmbientSound"] ??
        ClassDefaults.Catalog.Default(actor.Class, "AmbientSound"));

    public static double Number(Actor actor, string property, double fallback) =>
        (actor.Props[property] ?? ClassDefaults.Catalog.Default(actor.Class, property)) is SvJsonNumber n ? PropValues.Num(n) : fallback;

    public static Actor Create(MapProject project, string sound, Vec3 at, double volume = 128, double pitch = 64, double radius = 64, bool loop = true, double repeatInterval = 0)
    {
        string stem = "Sound_" + AssetPaths.Safe(AssetPaths.Split(sound).Name);
        string id = stem;
        var used = project.AllRefs().Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 2; used.Contains(id); i++) id = stem + "_" + i;
        var actor = new Actor(id, "Engine.AmbientSound") { Location = at, Editor = new EditorMeta { Folder = "Sounds" } };
        Set(actor, sound, volume, pitch, radius, loop, repeatInterval);
        return actor;
    }

    public static void Set(Actor actor, string sound, double volume, double pitch, double radius, bool? loop = null, double? repeatInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sound);
        if (!double.IsFinite(volume) || !double.IsFinite(pitch) || !double.IsFinite(radius))
            throw new ArgumentException("Sound settings must be finite numbers.");
        actor.Props.Set("AmbientSound", PropValues.AssetRef(sound));
        actor.Props.Set("SoundVolume", SvJsonNode.Integer((int)Math.Round(Math.Clamp(volume, 0, 255))));
        actor.Props.Set("SoundPitch", SvJsonNode.Integer((int)Math.Round(Math.Clamp(pitch, 1, 255))));
        actor.Props.Set("SoundRadius", SvJsonNode.Float((float)Math.Clamp(radius, 0, 255)));
        if (loop is { } repeat) actor.Props.Set("SoundLoop", SvJsonNode.Bool(repeat));
        if (repeatInterval is { } seconds)
        {
            ValidateInterval(seconds);
            actor.Props.Set("SoundRepeatInterval", SvJsonNode.Float((float)seconds));
        }
    }

    public static ReplaceObjectsCommand Edit(IEnumerable<Actor> actors, string sound, double volume, double pitch, double radius, bool? loop = null, double? repeatInterval = null)
    {
        var command = new ReplaceObjectsCommand("Edit sound sources");
        foreach (Actor actor in actors)
        {
            Actor copy = actor.Clone();
            Set(copy, sound, volume, pitch, radius, loop, repeatInterval);
            if (copy.Props.ToJson().ToString() != actor.Props.ToJson().ToString()) command.Actor(actor, copy);
        }
        return command;
    }
    public static ReplaceObjectsCommand EditLoop(IEnumerable<Actor> actors, bool loop, double repeatInterval)
    {
        ValidateInterval(repeatInterval);
        var command = new ReplaceObjectsCommand(loop ? "Loop placed sounds" : "Play placed sounds once");
        foreach (Actor actor in actors.Where(a => Sound(a) is not null && (Loops(a) != loop || Math.Abs(RepeatInterval(a) - repeatInterval) > .000001)))
        {
            Actor copy = actor.Clone(); copy.Props.Set("SoundLoop", SvJsonNode.Bool(loop));
            copy.Props.Set("SoundRepeatInterval", SvJsonNode.Float((float)repeatInterval)); command.Actor(actor, copy);
        }
        return command;
    }
    private static void ValidateInterval(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 86400) throw new ArgumentException("Sound repeat interval must be between 0 and 86400 seconds.");
    }
}
