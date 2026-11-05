using System.Collections.Concurrent;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Game.Lighting;

namespace OpenScct.Editor.Core.Classes;

/// <summary>What an actor is, for its icon in the views, the outliner and the class lists. One kind per icon.</summary>
public enum ActorKind
{
    /// <summary>Anything else: the generic actor mark.</summary>
    Actor,
    PointLight,
    SpotLight,
    SunLight,
    TriggerLight,
    PlayerStart,
    SpyStart,
    MercStart,
    Camera,
    CameraNetwork,
    Laser,
    MotionSensor,
    PresenceSensor,
    Alarm,
    Mine,
    Computer,
    BombTarget,
    Disk,
    Flag,
    FlagDropZone,
    Objective,
    Mission,
    AmmoStation,
    HealthStation,
    Switch,
    Door,
    Lift,
    Mover,
    Trigger,
    Message,
    Volume,
    Zone,
    Sound,
    Emitter,
    Projector,
    MagicEvent,
    Keypoint,
    Note,
    ZoneTip,
    HelpPath,
    Conveyor,
    Glass,
    GameplayObject,
    StaticMesh,
    Pawn,
}

/// <summary>
/// The one class to <see cref="ActorKind"/> mapping. A class takes the kind of the nearest class in its super chain
/// that has one (<see cref="IClassCatalog.Chain"/>), so a subclass gets its parent's icon: SBase.STriggerPlayerStart
/// is a player start, SBase.SMineLaser a mine. A class with no such ancestor (a map's own class the catalog does not
/// know, a particle emitter object) is matched by the whole CamelCase words of its name. An actor's own
/// values refine the kind: a light's LightEffect makes it a spot or sun light, a player start's TeamNumber a spy
/// (0) or mercenary (1) start.
/// </summary>
public static class ActorKinds
{
    /// <summary>Kinds by full class name; the nearest class of the chain listed here wins.</summary>
    private static readonly Dictionary<string, ActorKind> s_classes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Lights.
        ["Engine.Light"] = ActorKind.PointLight,
        ["Engine.Spotlight"] = ActorKind.SpotLight,
        ["Engine.SunLight"] = ActorKind.SunLight,
        ["SBase.STriggerLight"] = ActorKind.TriggerLight,
        ["SBase.SProjoFixe"] = ActorKind.SpotLight,
        ["SBase.SProjoLight"] = ActorKind.SpotLight,

        // Starts.
        ["Engine.PlayerStart"] = ActorKind.PlayerStart,
        ["Engine.NavigationPoint"] = ActorKind.Keypoint,

        // Security.
        ["SBase.SCamera"] = ActorKind.Camera,
        ["SBase.SStickyCam"] = ActorKind.Camera,
        ["SBase.SCamNetwork"] = ActorKind.CameraNetwork,
        ["SBase.SLaserTrigger"] = ActorKind.Laser,
        ["SBase.SLaserProjector"] = ActorKind.Laser,
        ["SBase.SLaserBeam"] = ActorKind.Laser,
        ["SBase.SVolumetricSensor"] = ActorKind.MotionSensor,
        ["Engine.MotionTrackingVision"] = ActorKind.MotionSensor,
        ["SBase.SPresenceTrigger"] = ActorKind.PresenceSensor,
        ["SBase.SPresDetector"] = ActorKind.PresenceSensor,
        ["SBase.SAlarm"] = ActorKind.Alarm,
        ["SBase.SMines"] = ActorKind.Mine,
        ["SBase.SBallTrap"] = ActorKind.Mine,

        // Objectives.
        ["SBase.SObjectiveTrigger"] = ActorKind.Objective,
        ["SBase.SComputerObjectiveTrigger"] = ActorKind.Computer,
        ["SBase.SBombTargetObjectiveTrigger"] = ActorKind.BombTarget,
        ["SBase.SDisk"] = ActorKind.Disk,
        ["SBase.SFlag"] = ActorKind.Flag,
        ["SBase.SFlagDropZone"] = ActorKind.FlagDropZone,
        ["SBase.SObjective"] = ActorKind.Objective,
        ["SBase.SMission"] = ActorKind.Mission,

        // Stations and switches.
        ["SBase.SAmmoStock"] = ActorKind.AmmoStation,
        ["SBase.SInfirmary"] = ActorKind.HealthStation,
        ["SBase.SInterrupteur"] = ActorKind.Switch,
        ["SBase.SInterrupteurManager"] = ActorKind.Switch,
        ["SBase.SGPOControl"] = ActorKind.Switch,

        // Movers.
        ["Engine.Mover"] = ActorKind.Mover,
        ["SBase.SLift"] = ActorKind.Lift,
        ["SBase.SLiftDoor"] = ActorKind.Door,
        ["SoftBody.ESBStripDoorActor"] = ActorKind.Door,

        // Triggers, volumes and zones.
        ["Engine.Triggers"] = ActorKind.Trigger,
        ["SBase.SGETrigger"] = ActorKind.Trigger,
        ["SBase.STriggerInteraction"] = ActorKind.Trigger,
        ["SBase.SMessageTrigger"] = ActorKind.Message,
        ["Engine.Brush"] = ActorKind.Volume,
        ["Engine.Volume"] = ActorKind.Volume,
        ["Engine.AntiPortalActor"] = ActorKind.Volume,
        ["Engine.DecoVolumeObject"] = ActorKind.Volume,
        ["Engine.ERainVolume"] = ActorKind.Volume,
        ["Engine.ZoneInfo"] = ActorKind.Zone,

        // Sound.
        ["Engine.AmbientSound"] = ActorKind.Sound,
        ["Engine.SoundTrigger"] = ActorKind.Sound,
        ["Engine.MusicTrigger"] = ActorKind.Sound,
        ["SBase.SAmbientSoundTrigger"] = ActorKind.Sound,
        ["SBase.SNoiseEmitter"] = ActorKind.Sound,
        ["SBase.SNotifyNoise"] = ActorKind.Sound,

        // Effects.
        ["Engine.Emitter"] = ActorKind.Emitter,
        ["Engine.Effects"] = ActorKind.Emitter,
        ["SBase.SMuzzleFlash"] = ActorKind.Emitter,
        ["SBase.SSprinkler"] = ActorKind.Emitter,
        ["SBase.SShaker"] = ActorKind.MagicEvent,
        ["Engine.Projector"] = ActorKind.Projector,

        // Events, notes and paths.
        ["SBase.SMagicEvent"] = ActorKind.MagicEvent,
        ["Engine.Keypoint"] = ActorKind.Keypoint,
        ["Engine.Note"] = ActorKind.Note,
        ["SBase.SZoneTips"] = ActorKind.ZoneTip,
        ["SBase.SHelpPath"] = ActorKind.HelpPath,
        ["Svm.Conveyor"] = ActorKind.Conveyor,

        // Props.
        ["SBase.SVitre"] = ActorKind.Glass,
        ["SBase.SBasicGamePlayObject"] = ActorKind.GameplayObject,
        ["Engine.StaticMeshActor"] = ActorKind.StaticMesh,
        ["Engine.Decoration"] = ActorKind.StaticMesh,
        ["Engine.ESoftBodyActor"] = ActorKind.StaticMesh,
        ["SBase.SAnimatedMesh"] = ActorKind.StaticMesh,
        ["SBase.SFish"] = ActorKind.StaticMesh,
        ["SBase.SFish_Follow"] = ActorKind.StaticMesh,
        ["SBase.SExtincteur"] = ActorKind.GameplayObject,
        ["Engine.Pawn"] = ActorKind.Pawn,
    };

    /// <summary>For classes the catalog does not know: CamelCase word runs of the short name, most specific first
    /// (what a gameplay class is wins over how it fires: SLaserTrigger is a laser).</summary>
    private static readonly (string[] Words, ActorKind Kind)[] s_names =
    [
        (["Player", "Start"], ActorKind.PlayerStart),
        (["Trigger", "Light"], ActorKind.TriggerLight),
        (["Spot", "Light"], ActorKind.SpotLight),
        (["Spotlight"], ActorKind.SpotLight),
        (["Sun", "Light"], ActorKind.SunLight),
        (["Sunlight"], ActorKind.SunLight),
        (["Cam", "Network"], ActorKind.CameraNetwork),
        (["Laser"], ActorKind.Laser),
        (["Computer"], ActorKind.Computer),
        (["Bomb"], ActorKind.BombTarget),
        (["Flag", "Drop"], ActorKind.FlagDropZone),
        (["Flag"], ActorKind.Flag),
        (["Disk"], ActorKind.Disk),
        (["Mission"], ActorKind.Mission),
        (["Objective"], ActorKind.Objective),
        (["Alarm"], ActorKind.Alarm),
        (["Camera"], ActorKind.Camera),
        (["Mine"], ActorKind.Mine),
        (["Sensor"], ActorKind.MotionSensor),
        (["Presence"], ActorKind.PresenceSensor),
        (["Ammo"], ActorKind.AmmoStation),
        (["Infirmary"], ActorKind.HealthStation),
        (["Interrupteur"], ActorKind.Switch),
        (["Switch"], ActorKind.Switch),
        (["Lift"], ActorKind.Lift),
        (["Elevator"], ActorKind.Lift),
        (["Door"], ActorKind.Door),
        (["Conveyor"], ActorKind.Conveyor),
        (["Message"], ActorKind.Message),
        (["Zone", "Tip"], ActorKind.ZoneTip),
        (["Tip"], ActorKind.ZoneTip),
        (["Help", "Path"], ActorKind.HelpPath),
        (["Magic", "Event"], ActorKind.MagicEvent),
        (["Sound"], ActorKind.Sound),
        (["Music"], ActorKind.Sound),
        (["Emitter"], ActorKind.Emitter),
        (["Particle"], ActorKind.Emitter),
        (["Projector"], ActorKind.Projector),
        (["Trigger"], ActorKind.Trigger),
        (["Volume"], ActorKind.Volume),
        (["Zone"], ActorKind.Zone),
        (["Mover"], ActorKind.Mover),
        (["Light"], ActorKind.PointLight),
        (["Static", "Mesh"], ActorKind.StaticMesh),
        (["Mesh"], ActorKind.StaticMesh),
        (["Keypoint"], ActorKind.Keypoint),
        (["Note"], ActorKind.Note),
        (["Pawn"], ActorKind.Pawn),
    ];

    private static readonly ConcurrentDictionary<string, ActorKind> s_cache = new(StringComparer.OrdinalIgnoreCase);
    private static IClassCatalog? s_cachedFor;

    /// <summary>The kind of a class ("Engine.Light", "SBase.SCamera" or a short name), following its super chain in
    /// <paramref name="catalog"/>; by its name when the catalog does not know it.</summary>
    public static ActorKind OfClass(string className, IClassCatalog? catalog)
    {
        if (!ReferenceEquals(catalog, s_cachedFor))
        {
            s_cache.Clear();
            s_cachedFor = catalog;
        }

        return s_cache.GetOrAdd(className ?? "", c => Find(c, catalog));
    }

    /// <summary>The kind of an actor: its class's, refined by its own values (stored, else the class defaults): a
    /// light's LightEffect (spot, sun), a player start's TeamNumber (1 is the mercenaries').</summary>
    public static ActorKind Of(Actor a, IClassCatalog? catalog)
    {
        ActorKind kind = OfClass(a.Class, catalog);
        switch (kind)
        {
            case ActorKind.PointLight:
                return LightEffect(a, catalog) switch
                {
                    ActorLightRules.LE_Spotlight or ActorLightRules.LE_StaticSpot => ActorKind.SpotLight,
                    ActorLightRules.LE_Sunlight => ActorKind.SunLight,
                    _ => kind,
                };
            case ActorKind.PlayerStart:
                return Number(a, catalog, "TeamNumber") == 1 ? ActorKind.MercStart : ActorKind.SpyStart;
            default:
                return kind;
        }
    }

    /// <summary>Whether the actor's facing matters (starts, cameras, lasers, spot and sun lights, switches, stations,
    /// emitters…), so the views draw its direction arrow with its icon. Point lights, sounds, zones, triggers and the
    /// like face nowhere in particular.</summary>
    public static bool ShowsDirection(ActorKind kind, Actor? a = null, IClassCatalog? catalog = null) => kind switch
    {
        ActorKind.PointLight or ActorKind.Sound or ActorKind.Zone or ActorKind.Trigger or ActorKind.Volume or
            ActorKind.MagicEvent or ActorKind.Note or ActorKind.Keypoint or ActorKind.ZoneTip or ActorKind.Message or
            ActorKind.Disk or ActorKind.Flag or ActorKind.FlagDropZone or ActorKind.Objective or ActorKind.Mission or
            ActorKind.PresenceSensor or ActorKind.Alarm => false,
        ActorKind.TriggerLight => a is not null && LightEffect(a, catalog) is ActorLightRules.LE_Spotlight or ActorLightRules.LE_StaticSpot
            or ActorLightRules.LE_Sunlight,
        _ => true,
    };

    /// <summary>A short name of the kind for people ("Spot light").</summary>
    public static string Title(ActorKind kind) => kind switch
    {
        ActorKind.Actor => "Actor",
        ActorKind.PointLight => "Light",
        ActorKind.SpotLight => "Spot light",
        ActorKind.SunLight => "Sun light",
        ActorKind.TriggerLight => "Trigger light",
        ActorKind.PlayerStart => "Player start",
        ActorKind.SpyStart => "Spy start",
        ActorKind.MercStart => "Mercenary start",
        ActorKind.CameraNetwork => "Camera network",
        ActorKind.MotionSensor => "Motion sensor",
        ActorKind.PresenceSensor => "Presence sensor",
        ActorKind.BombTarget => "Bomb target",
        ActorKind.FlagDropZone => "Flag drop zone",
        ActorKind.AmmoStation => "Ammo station",
        ActorKind.HealthStation => "Health station",
        ActorKind.Zone => "Zone info",
        ActorKind.MagicEvent => "Magic event",
        ActorKind.ZoneTip => "Zone tip",
        ActorKind.HelpPath => "Help path",
        ActorKind.GameplayObject => "Gameplay object",
        ActorKind.StaticMesh => "Static mesh",
        _ => kind.ToString(),
    };

    private static ActorKind Find(string className, IClassCatalog? catalog)
    {
        if (className.Length == 0)
        {
            return ActorKind.Actor;
        }

        if (s_classes.TryGetValue(className, out ActorKind exact))
        {
            return exact;
        }

        if (catalog is not null)
        {
            foreach (ClassInfo c in catalog.Chain(className))
            {
                if (s_classes.TryGetValue(c.Name, out ActorKind kind))
                {
                    return kind;
                }
            }
        }

        // Not under any class above (or not in the catalog at all): what its name says, else the generic mark.
        return ByName(className);
    }

    private static ActorKind ByName(string className)
    {
        string[] words = Words(className[(className.LastIndexOf('.') + 1)..]);
        foreach ((string[] part, ActorKind kind) in s_names)
        {
            if (HasWords(words, part))
            {
                return kind;
            }
        }

        return ActorKind.Actor;
    }

    /// <summary>The CamelCase words of a name: "SZoneTips" is S, Zone, Tips; "HUDText" is HUD, Text.</summary>
    private static string[] Words(string name)
    {
        var words = new List<string>();
        int start = 0;
        for (int i = 1; i <= name.Length; i++)
        {
            bool split = i == name.Length || name[i] == '_' ||
                (char.IsUpper(name[i]) && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])))) ||
                (char.IsDigit(name[i]) != char.IsDigit(name[i - 1]));
            if (split)
            {
                if (i > start && name[start] != '_')
                {
                    words.Add(name[start..i]);
                }

                start = i < name.Length && name[i] == '_' ? i + 1 : i;
            }
        }

        return [.. words];
    }

    /// <summary>True when <paramref name="part"/> is a run of whole words of <paramref name="words"/>, its last word
    /// allowed to start a longer one ("Tip" matches "Tips").</summary>
    private static bool HasWords(string[] words, string[] part)
    {
        for (int start = 0; start + part.Length <= words.Length; start++)
        {
            bool match = true;
            for (int i = 0; i < part.Length && match; i++)
            {
                string word = words[start + i];
                match = i == part.Length - 1
                    ? word.StartsWith(part[i], StringComparison.OrdinalIgnoreCase)
                    : word.Equals(part[i], StringComparison.OrdinalIgnoreCase);
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static int LightEffect(Actor a, IClassCatalog? catalog) => (a.Props["LightEffect"] ?? catalog?.Default(a.Class, "LightEffect")) switch
    {
        SvJsonNumber n => (int)PropValues.Num(n),
        SvJsonString s => s.Value switch
        {
            "LE_Spotlight" => ActorLightRules.LE_Spotlight,
            "LE_StaticSpot" => ActorLightRules.LE_StaticSpot,
            "LE_Sunlight" => ActorLightRules.LE_Sunlight,
            _ => 0,
        },
        _ => 0,
    };

    private static double Number(Actor a, IClassCatalog? catalog, string prop) =>
        (a.Props[prop] ?? catalog?.Default(a.Class, prop)) is SvJsonNumber n ? PropValues.Num(n) : 0;
}
