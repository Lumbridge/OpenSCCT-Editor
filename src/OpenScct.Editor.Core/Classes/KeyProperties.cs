using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Classes;

/// <summary>
/// The properties that matter most for each class, in the order the Inspector lists them above the folded categories.
/// Every actor inherits well over a hundred Engine.Actor properties; the few a map maker sets on a camera, a switch or
/// a door are spread across those categories. The lists come from what the stock maps store on each class (the
/// values their makers changed from the defaults), plus what each class declares for itself.
/// A class uses the list of the nearest class in its super chain that has one, so a list names inherited properties
/// too and a subclass without a list takes its parent's. Names a class does not have are ignored.
/// </summary>
public static class KeyProperties
{
    private static readonly string[] s_events = ["Tag", "Event"];
    private static readonly string[] s_interaction = ["fInteractionRadius", "fInteractionHeight", "bInteractInFront"];
    private static readonly string[] s_damage = ["bDamageable", "HitPoints"];
    private static readonly string[] s_visions = ["HeatIntensity", "EMFIntensity"];
    private static readonly string[] s_blocking = ["bBlockActors", "bBlockPlayers"];
    private static readonly string[] s_cylinder = ["CollisionRadius", "CollisionHeight"];

    private static readonly string[] s_light =
    [
        "LightBrightness", "LightHue", "LightSaturation", "LightRadius", "LightType", "LightEffect", "LightCone",
        "SpotWidth", "SpotHeight", "EchelonRange", "bAffectOwnZoneOnly", "bCorona",
    ];

    private static readonly string[] s_zone =
    [
        "LocationName", "ZoneTag", "AmbientBrightness", "AmbientHue", "AmbientSaturation", "bDistanceFog", "DistanceFogColor",
        "DistanceFogStart", "DistanceFogEnd", "AmbientSound", "SoundVolume", "ZoneEffect", "ManualExcludes",
    ];

    private static readonly string[] s_zoneTip =
        ["sMessage_ATT", "sMessage_DEF", "bShowOnlyWithIngameHelp", "bStayPermanently", .. s_cylinder, "Tag"];

    /// <summary>Lists by short class name.</summary>
    private static readonly Dictionary<string, string[]> s_lists = new(StringComparer.OrdinalIgnoreCase)
    {
        // Every other actor: how it is wired to the rest of the map.
        ["Actor"] = s_events,

        // A plain brush is geometry: the Inspector's Brush section has what matters (volumes have lists below).
        ["Brush"] = [],

        // Lights.
        ["Light"] = [.. s_light, "Tag"],
        ["STriggerLight"] = [.. s_light, "bInitialyOn", "TriggerMethode", "Tag"],
        ["SFlashGen"] = ["fTempsMin", "fTempsMax", "SND_Tonnerre", .. s_light, "Tag"],

        // Starts.
        ["PlayerStart"] = ["TeamNumber", "bMercDeathMatchStart", "bEnabled", "Priority", .. s_events],

        // Movers.
        ["Mover"] =
        [
            "InitialState", "MoveTime", "StayOpenTime", "NumKeys", "KeyNum", "MoverEncroachType", "bTriggerOnceOnly", "StaticMesh",
            "OpeningSound", "ClosingSound", "MoveAmbientSound", .. s_events,
        ],
        ["SLift"] = ["DoorsAndSwitches", "LiftDoors", "MoveTime", "NumKeys", "KeyNum", "StaticMesh", "MoveAmbientSound", .. s_events],

        // Security.
        ["SCamera"] =
        [
            "DetectionDelay", "VisibilityMaxDistance", "VisibilityConeAngle", "PatrolAngle", "RotationVelocity", "AlertOnEventFrom",
            .. s_damage, "StickyNeutralizationTime", .. s_events,
        ],
        ["SOrientableObject"] = ["PitchConstraint", "YawConstraint", "RollConstraint", .. s_events],
        ["SCamNetwork"] = ["CamName", "NextCam", "PrevCam", "bFirstCam", "PitchConstraint", "YawConstraint", "Tag"],
        ["SProjoFixe"] =
        [
            "sDescriptionText", "PitchConstraint", "YawConstraint", "CameraOffset", "LightBrightness", "LightHue", "LightSaturation",
            "SpotWidth", "SpotHeight", "EchelonRange", "fInteractionRadius", "fInteractionHeight", "Tag",
        ],
        ["SLaserTrigger"] = ["LaserLength", "bHideLaserInNormalVision", "DetectedClass", "ERControl", .. s_events],
        ["SVolumetricSensor"] = ["Volumes", "DetectionDelay", "MinDetectedVelocity", "DetectedClass", "ERControl", .. s_events],
        ["SPresenceTrigger"] = ["DetectedClass", .. s_cylinder, "bDetectGrab", "bUntrigger", .. s_events],
        ["SAlarm"] =
        [
            "AlarmName", "AlarmDescription", "AlarmDescriptionSpy", "Duration", "MessagePriority", "ObjectiveTriggersToLock",
            "MoversToLock", "Events", .. s_events,
        ],
        ["SAlarmZoneInfo"] = ["bDetectIntruders", .. s_zone],
        ["SMines"] = ["TempsActivation", .. s_damage, .. s_events],
        ["SPresDetector"] = ["fDetectionRadius", "fWarningDuration", "TempsActivation", .. s_damage, .. s_events],
        ["SBallTrap"] = ["ProjectileClass", "fFireRate", "iVelocityMin", "iVelocityMax", .. s_events],
        ["SGPOControl"] = ["ZoneDescription", "CylinderWidth", "CylinderHeight", "Tag"],

        // Objectives.
        ["SObjectiveTrigger"] = ["TriggerName", "CaptureTime", "HideWhileInactive", .. s_interaction, .. s_events],
        ["SComputerObjectiveTrigger"] =
            ["TriggerName", "CaptureTime", "DominationCaptureTime", "HideWhileInactive", "InteractionDistanceToCenter", "StaticMesh", .. s_events],
        ["SBombTargetObjectiveTrigger"] = ["TriggerName", "CaptureTime", "HideWhileInactive", "StaticMesh", .. s_events],
        ["SDisk"] = ["TriggerName", "CaptureTime", "HideWhileInactive", "Group", "ResetTime", .. s_events],
        ["SFlag"] = ["TriggerName", "DropZone", "HideWhileInactive", "ResetTime", .. s_interaction, .. s_events],
        ["SFlagDropZone"] = ["TriggerName", "CaptureTime", .. s_interaction, .. s_cylinder, .. s_events],
        ["SObjective"] =
        [
            "ObjectiveName", "Description", "DescriptionDEF", "Triggers", "HideWhileInactive", "RandomTriggers", "RespawnTime", "ActivationTime",
            "TriggersERVisibilityZones", .. s_events,
        ],
        ["SMission"] =
        [
            "GameMode", "ObjectiveName", "Description", "DescriptionDEF", "Objectives", "MinimumObjectives", "RandomObjectives",
            "bChained", "AtResetEvent", .. s_events,
        ],

        // Switches, stations and other things players use.
        ["SBasicGamePlayObject"] = ["sDescriptionText", "UsableBy", .. s_interaction, .. s_damage, "StaticMesh", .. s_events],
        ["SInterrupteur"] =
        [
            "sDescriptionText", "UsableBy", "Event", "Tag", .. s_interaction, "iHackingTime", "sWarnHackingText", "bTriggerOnceOnly",
            "bInitialyUsable", "bInitialyActive", "bMaintainsSwitch", "iReusableTime", "UseRelativeLocation", "StaticMesh",
        ],
        ["SInterrupteurManager"] = ["SInterrupteurList", "Tag"],
        ["SAmmoStock"] = [.. s_interaction, "UsableBy", "StaticMesh", .. s_blocking, "Tag"],
        ["SInfirmary"] = [.. s_interaction, "UsableBy", "StaticMesh", .. s_blocking, "Tag"],
        ["SSprinkler"] = ["Duration", "WetVolumeRadius", "WetVolumeHeight", "WetVolumeZOffset", .. s_events],
        ["STriggerInteraction"] = ["bIsActive", "Description", "iPriority", .. s_events],

        // Props.
        ["StaticMeshActor"] = ["StaticMesh", "Skins", .. s_blocking, "bShadowCast", "bUnlit", "AmbientGlow", .. s_visions, "Tag"],
        ["SGamePlayObject"] =
        [
            "StaticMesh", .. s_damage, "DamagedMeshes", "TakeDamageTypes", "DestroySound", "DestroyOnEventFrom", .. s_blocking,
            "bPushable", "TriggeredMesh", "TriggeredSkin", .. s_visions, .. s_events,
        ],
        ["SVitre"] = ["StaticMesh", "Skins", "HeatOpacity", "EMFOpacity", .. s_damage, "DamagedMeshes", "Tag"],
        ["SExtincteur"] = ["StaticMesh", .. s_visions, "Tag"],
        ["SAnimatedMesh"] =
            ["Mesh", "Animations", "bAutoStart", "bChainedAnims", "LoopStatus_0forInfinite", "ActorsToAttach", "bHidden", .. s_events],
        ["SFish"] =
        [
            "StaticMesh", "Mesh", "AnimsFish", "bPilote", "fSpeedMin", "fSpeedMax", "fEloignementMaxX", "fEloignementMaxY",
            "fEloignementMaxZ", "fRotHmin", "fRotHmax", "Tag",
        ],
        ["SFish_Follow"] = ["Pilot", "StaticMesh", "AnimsFish", "Tag"],
        ["SHelpPath"] = ["StaticMesh", "Tag"],

        // Triggers and events.
        ["Triggers"] = [.. s_events, .. s_cylinder],
        ["Trigger"] =
        [
            "TriggerType", "ClassProximityType", "Event", "Tag", .. s_cylinder, "bTriggerOnceOnly", "bInitiallyActive", "ReTriggerDelay",
            "RepeatTriggerTime", "Message",
        ],
        ["UseTrigger"] = ["Message", .. s_events, .. s_cylinder],
        ["LineOfSightTrigger"] = ["SeenActorTag", "MaxViewDist", "MaxViewAngle", "bEnabled", .. s_events],
        ["MaterialTrigger"] = ["MaterialsToTrigger", .. s_events],
        ["SGETrigger"] = ["GEMask", "bToggle", .. s_events],
        ["SMagicEvent"] = ["Groups", "StopActor", .. s_events],
        ["SMessageTrigger"] = ["MessageSpy", "MessageMerc", "DisplayTime", "Priority", "Tag"],
        ["SShaker"] = ["ShakeEvents", "Tag"],
        ["SZoneTips"] = s_zoneTip,
        ["SEndZoneTip"] = ["EndGameDelay", .. s_zoneTip],
        ["Note"] = ["Text"],

        // Sound.
        ["AmbientSound"] = ["AmbientSound", "SoundRadius", "SoundVolume", "SoundPitch", "bFullVolume", "Tag"],
        ["SAmbientSoundTrigger"] = ["AmbientSound", "SoundRadius", "SoundVolume", "SoundPitch", "bToggle", "bInitialyOn", .. s_events],
        ["SoundTrigger"] = ["Sound", "SoundVolume", "SoundRadius", "SoundPitch", .. s_events],
        ["MusicTrigger"] = ["Song", "bToggle", "FadeInTime", "FadeOutTime", "bInstigatorOnly", .. s_events],
        ["SNotifyNoise"] = ["fIntensity", "fTimeRateInSec", "bEnabled", .. s_events],

        // Effects.
        ["Emitter"] = ["Emitters", "bNetworkTrigger", "AutoDestroy", "AutoReset", .. s_events],
        ["SFlareEmitter"] = ["SoundEffect", "Emitters", "bNetworkTrigger", .. s_events],
        ["SMuzzleFlash"] = ["Lifetime", "Tag"],
        ["Projector"] =
        [
            "ProjTexture", "FOV", "MaxTraceDistance", "FrameBufferBlendingOp", "MaterialBlendingOp", "bProjectBSP", "bProjectStaticMesh",
            "bProjectActor", "bLevelStatic", "bGradient", "ProjectTag", "Tag",
        ],
        ["ERainVolume"] = ["m_RainSize", "m_RainFalloff", "m_RainFadeScale", "m_maxDistFromCamera", "m_weatherProperties", "Tag"],

        // Zones and volumes.
        ["ZoneInfo"] = [.. s_zone, "Tag"],
        ["LevelInfo"] = ["Title", "Author", "RecommendedNumPlayers", "GEActivationMask", .. s_zone],
        ["Volume"] = ["LocationName", "LocationPriority", "AssociatedActorTag", "Tag"],
        ["SDamageVolume"] = ["bActive", "bToggle", "HitPoints", "DamageType", "Period", "DamagedClass", .. s_events],
        ["DecorationList"] = ["Decorations", "Tag"],
    };

    /// <summary>The key properties of a class, most important first: the list of the nearest class in its super chain
    /// that has one. A class the catalog does not know is looked up by its own name, then as a plain actor.</summary>
    public static IReadOnlyList<string> For(IClassCatalog? catalog, string className)
    {
        IEnumerable<string> chain = catalog?.Chain(className).Select(c => c.Name) ?? [];
        foreach (string cls in chain.Prepend(className).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (s_lists.TryGetValue(cls[(cls.LastIndexOf('.') + 1)..], out string[]? list))
            {
                return list;
            }
        }

        return catalog?.Find(className) is null && className.Length > 0 ? s_events : [];
    }

    /// <summary>The key properties every one of the classes has, in the first class's order (several objects selected).</summary>
    public static IReadOnlyList<string> ForAll(IClassCatalog? catalog, IEnumerable<string> classNames)
    {
        List<string>? shared = null;
        foreach (string cls in classNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<string> list = For(catalog, cls);
            shared = shared is null ? [.. list] : shared.Where(n => list.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        return shared ?? [];
    }
}
