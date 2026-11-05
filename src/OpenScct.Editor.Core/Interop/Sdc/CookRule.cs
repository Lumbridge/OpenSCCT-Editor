using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Sdc;

/// <summary>
/// What the stock editor's cook leaves out of a cooked copy's Level.Actors (0x10ee13b0 with filter 0x10edd1e0;
/// svmbuild mapconv/source.py left_out_by_cook): an actor with a LightType, unless it matters at run time (in-game
/// or dynamic lighting, an ESpotShadow effect, or both a heat radius and intensity), and a brush that is not a
/// volume. The cook writes such an actor to the file only when a saved object refers to it (IgnoredEchelonLights,
/// ObjectLights), as an export outside Level.Actors; otherwise it is gone, and only its light in the baked atlases and
/// mesh colours remains. Over 100 editor/cooked pairs the rule predicts every dropped light.
/// </summary>
public static class CookRule
{
    public const int LightEffectESpotShadow = 22;

    /// <param name="value">The actor's effective value of a property (its own, else the class default).</param>
    public static bool LeftOut(Func<string, SvJsonNode?> value, bool isBrush)
    {
        if (Int(value("LightType")) != 0)
        {
            return !(value("bApplyToInGameLighting")?.Kind == SvJsonKind.True || value("bDynamicLight")?.Kind == SvJsonKind.True
                || Int(value("LightEffect")) == LightEffectESpotShadow
                || (PropValues.Num(value("HeatRadius")) != 0 && PropValues.Num(value("HeatIntensity")) != 0));
        }

        return isBrush;
    }

    /// <summary>An actor with a LightType the cook drops: a static light whose only job is the bake.</summary>
    public static bool DropsLight(Func<string, SvJsonNode?> value) => Int(value("LightType")) != 0 && LeftOut(value, isBrush: false);

    private static long Int(SvJsonNode? n) => n is SvJsonNumber num && num.TryGetInt64(out long v) ? v : 0;
}
