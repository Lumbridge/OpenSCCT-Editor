using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>
/// What a newly placed static light starts with. A stock Light bakes into the lightmaps only
/// (bApplyToStaticLighting), so the room brightens but a player standing in it stays dark, and a spy hiding there is
/// lit differently from what everyone sees: a stealth trap. New static lights therefore also light players in game
/// (bApplyToInGameLighting); the author can still turn either off. Lights read from a map keep what they had.
/// </summary>
public static class NewLights
{
    public static void Place(Actor a)
    {
        bool Flag(string prop) => (a.Props[prop] ?? ClassDefaults.Catalog.Default(a.Class, prop))?.Kind == SvJsonKind.True;
        if (ClassDefaults.IsA(a, "Engine.Light") && a.Props["bApplyToInGameLighting"] is null && Flag("bStatic") &&
            Flag("bApplyToStaticLighting") && !Flag("bApplyToInGameLighting"))
        {
            a.Props["bApplyToInGameLighting"] = SvJsonNode.True;
        }
    }
}
