using System.Text.RegularExpressions;
using OpenScct.Editor.Core.Gameplay;

namespace OpenScct.Editor.Core.Classes;

/// <summary>English presentation names. Serialized class paths always keep their original spelling.</summary>
public static partial class ClassNames
{
    private static readonly Dictionary<string, string> s_names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SInterrupteurManager"] = "Switch manager",
        ["SExtincteurEmitter"] = "Fire extinguisher particles",
        ["SVitreEmitter"] = "Window particles",
        ["SProjoInteraction"] = "Searchlight interaction",
        ["SProjoLight"] = "Searchlight beam",
        ["SGamePlayObject"] = "Gameplay object",
        ["SGPOControl"] = "Security zone",
        ["SObjectif"] = "Objective",
        ["SZoneObjectif"] = "Objective zone",
        ["SFlaque"] = "Puddle",
        ["SDouilleEmitter"] = "Shell casing particles",
        ["SFlicFlocEmitter"] = "Water splash particles",
        ["SImpactEffectBeton"] = "Concrete impact effect",
        ["SSparkBeton"] = "Concrete impact sparks",
        ["SGadTorche"] = "Flashlight gadget",
        ["SGadTorcheLaser"] = "Laser sight gadget",
        ["SMinePiegeSpy"] = "Spy trap mine",
        ["SPawnAttaque"] = "Spy character",
        ["SPawnAttaque_1Mesh"] = "Spy character (single mesh)",
        ["SPawnDefense"] = "Mercenary character",
        ["SPawnDefense_1Mesh"] = "Mercenary character (single mesh)",
        ["STeamAttaquant"] = "Spy team",
        ["STeamDefenseur"] = "Mercenary team",
        ["SWeaponAttaque"] = "Spy weapon",
        ["SWeaponDefense"] = "Mercenary weapon",
        ["SWeaponDefenseDummy"] = "Mercenary weapon preview",
        ["SWeaponDefense_Shotgun"] = "Mercenary shotgun",
        ["SWeaponDefense_Uzi"] = "Mercenary rifle",
        ["SWeaponDefenseDummy_Shotgun"] = "Mercenary shotgun preview",
        ["SWeaponDefenseDummy_Uzi"] = "Mercenary rifle preview",
        ["SProjMarqueurSpy"] = "Spy marker projectile",
        ["sMuzzleCanon"] = "Launcher muzzle flash",
        ["SMuzzleGenerique"] = "Generic muzzle flash",
        ["SSmokeCanonEmitter"] = "Launcher smoke particles",
        ["LancementMenu"] = "Menu launcher",
    };

    public static string Title(string className)
    {
        string name = className[(className.LastIndexOf('.') + 1)..];
        Placeable? placeable = Placeables.Stock.FirstOrDefault(p => string.Equals(p.ShortClass, name, StringComparison.OrdinalIgnoreCase));
        if (placeable is not null) return placeable.Title;
        if (s_names.TryGetValue(name, out string? title)) return title;
        if (name.Length > 1 && name[0] == 'S' && char.IsUpper(name[1]) && !name.StartsWith("Static", StringComparison.Ordinal)) name = name[1..];
        return Words().Replace(Acronyms().Replace(name.Replace('_', ' '), "$1 $2"), "$1 $2");
    }

    public static string SearchText(string className) => Title(className) + " " + className;

    [GeneratedRegex("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
    [GeneratedRegex("([A-Z]+)([A-Z][a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex Acronyms();
}
