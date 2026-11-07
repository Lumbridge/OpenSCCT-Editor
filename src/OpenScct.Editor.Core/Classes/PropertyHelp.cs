using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Classes;

/// <summary>Authored explanations scoped by class; inherited settings use their ancestor's explanation.</summary>
public static class PropertyHelp
{
    private static readonly Dictionary<string, Dictionary<string, string>> Help = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Actor"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Tag"] = "A name other objects can send events to. Give a switch the same Event as this object's Tag to connect them.",
            ["Event"] = "The event this object sends when activated. Objects with a matching Tag receive it.",
            ["bHidden"] = "Hide this object's appearance during play. It can still affect gameplay and block players.",
            ["bBlockPlayers"] = "Stop players from walking through this object.",
            ["bBlockActors"] = "Make this object block other actors when the game's collision rules allow it.",
            ["CollisionRadius"] = "The width of this object's collision cylinder, measured from its centre.",
            ["CollisionHeight"] = "Half the height of this object's collision cylinder.",
            ["StaticMesh"] = "The 3D model used to draw this object. The required asset must be installed.",
            ["DrawScale"] = "Resize the object's appearance uniformly. 2 makes it twice as large.",
        },
        ["PlayerStart"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["TeamNumber"] = "Which team spawns here: 0 is spies, 1 is mercenaries. Place starts above a clear floor.",
        },
        ["Light"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["LightBrightness"] = "How bright this light is. Check the lit preview, then play as a spy to judge visibility.",
            ["LightRadius"] = "How far this light reaches. Larger values light more of the room.",
            ["LightHue"] = "The light's colour around the colour wheel. Use the colour swatch to choose it visually.",
            ["LightSaturation"] = "How strongly coloured the light is: the original game uses 0 for full colour and 255 for white.",
        },
        ["Mover"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["InitialState"] = "How the door or moving object responds to events. TriggerToggle alternates between its closed and open positions.",
            ["MoveTime"] = "Seconds the moving object takes to travel between its positions.",
            ["StayOpenTime"] = "Seconds a timed door waits before closing. Used by timed opening states.",
            ["KeyPos"] = "Positions the moving object travels between, relative to its starting position. Edit them visually in the Mover panel.",
        },
        ["SInterrupteur"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["UsableBy"] = "Which team can operate this switch. Choose one of the team options in the dropdown.",
            ["sDescriptionText"] = "The prompt players see when they can use this switch, for example Open door.",
            ["fInteractionRadius"] = "How close a player must stand to use this switch.",
        },
        ["SCamera"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["PatrolAngle"] = "How wide an angle the camera sweeps through, in degrees. Zero keeps it looking straight ahead.",
            ["VisibilityMaxDistance"] = "The farthest distance at which the camera can spot a player.",
            ["DetectionDelay"] = "Seconds an intruder must remain visible before the camera reacts.",
        },
        ["SAlarm"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Duration"] = "How many seconds this alarm remains active after it is triggered.",
            ["AlarmName"] = "The short name players see when this alarm is active.",
        },
        ["SLaserTrigger"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["LaserLength"] = "The length of the laser beam. Fit it across the opening you want to protect.",
        },
        ["SComputerObjectiveTrigger"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CaptureTime"] = "How many seconds a spy must interact with this computer to hack it.",
            ["DominationCaptureTime"] = "How long capturing this computer takes in Domination mode.",
            ["TriggerName"] = "The name used for this hackable computer in gameplay messages.",
        },
        ["SObjective"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ObjectiveName"] = "The name shown to players for this objective.",
            ["Triggers"] = "The computers or other gameplay triggers that complete this objective. The objective computer prefab connects these for you.",
        },
    };

    public static string? Find(IClassCatalog? catalog, string className, string property)
    {
        IEnumerable<string> chain = catalog?.Chain(className).Select(c => c.Name) ?? [];
        foreach (string cls in chain.Prepend(className).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string shortName = cls[(cls.LastIndexOf('.') + 1)..];
            if (Help.TryGetValue(shortName, out var properties) && properties.TryGetValue(property, out string? text)) return text;
        }
        return null;
    }

    public static bool Common(string property) => property is "TeamNumber" or "LightBrightness" or "LightRadius" or
        "LightHue" or "LightSaturation" or "ObjectiveName" or "CaptureTime" or "TriggerName" or "Duration" or
        "AlarmName" or "DetectionDelay" or "VisibilityMaxDistance" or "PatrolAngle" or "LaserLength" or "MoveTime" or "StayOpenTime" or "sDescriptionText";
}
