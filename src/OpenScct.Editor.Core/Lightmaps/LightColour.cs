using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>
/// UE2's FGetHSV (editor 0x11096ab0) with its brightness curve. Lights call it at full value and scale linearly
/// (<see cref="OpenScct.Game.Lighting.ActorLightRules.Colour"/>); zone ambient does not, so a zone's ambient is
/// <c>clamp(0.7 v / (sqrt(v) + 0.01))</c> with <c>v = AmbientBrightness x 1.4 / 255</c>, much brighter at the low values
/// Versus maps use than a linear scale would give. Ported from OpenSCCT's converter (<c>scctpkg/level.py _hsv_color</c>),
/// which matches every stored m_RefAmbientColor of the 84 stock maps.
/// </summary>
public static class LightColour
{
    /// <summary>FGetHSV(hue, saturation, value) as 0..1 per channel (not rounded).</summary>
    public static Vec3 Hsv(int hue, int saturation, double value) => Triangle(hue, saturation) * Scale(value);

    /// <summary>FGetHSV's brightness factor for a value byte: <c>clamp(0.7 v / (sqrt(v) + 0.01))</c> with
    /// <c>v = value x 1.4 / 255</c> (0.82 at 255).</summary>
    public static double Scale(double value)
    {
        double v = Math.Max(0, value) * 1.4 / 255.0;
        return Math.Clamp(v * (0.7 / (0.01 + Math.Sqrt(v))), 0, 1);
    }

    /// <summary>The value byte whose <see cref="Scale"/> is nearest <paramref name="scale"/> (255 past the top).</summary>
    public static int ValueOf(double scale)
    {
        int best = 0;
        for (int b = 1; b < 256; b++)
        {
            if (Math.Abs(Scale(b) - scale) < Math.Abs(Scale(best) - scale))
            {
                best = b;
            }
        }

        return best;
    }

    /// <summary>
    /// FGetHSV's colour before its brightness factor: UE2's hue triangle (red 0, green 85, blue 170, not HSV's
    /// hexcone, so a hue between two primaries is darker) mixed toward white by the saturation (0 the full colour,
    /// 255 white).
    /// </summary>
    public static Vec3 Mix(int hue, int saturation) => Triangle(Math.Clamp(hue, 0, 255), Math.Clamp(saturation, 0, 255));

    private static Vec3 Triangle(int hue, int saturation)
    {
        double r, g, bl;
        if (hue < 86)
        {
            (r, g, bl) = ((85 - hue) / 85.0, hue / 85.0, 0);
        }
        else if (hue < 171)
        {
            (r, g, bl) = (0, (170 - hue) / 85.0, (hue - 85) / 85.0);
        }
        else
        {
            (r, g, bl) = ((hue - 170) / 85.0, 0, (255 - hue) / 84.0);
        }

        double s = saturation / 255.0;
        return new Vec3(r + (s * (1 - r)), g + (s * (1 - g)), bl + (s * (1 - bl)));
    }

    /// <summary>
    /// The colour of a hue and saturation byte pair as FGetHSV makes it, scaled so its brightest channel is 1: exactly
    /// the light's ratios of red, green and blue. Colour pickers show this times their brightness.
    /// </summary>
    public static Vec3 Chroma(int hue, int saturation)
    {
        Vec3 c = Mix(hue, saturation);
        double top = Math.Max(c.X, Math.Max(c.Y, c.Z));
        return top <= 0 ? new Vec3(1, 1, 1) : c / top;
    }

    /// <summary>The brightest channel of <see cref="Mix"/> (below 1 between two primaries).</summary>
    public static double Peak(int hue, int saturation)
    {
        Vec3 c = Mix(hue, saturation);
        return Math.Max(c.X, Math.Max(c.Y, c.Z));
    }

    /// <summary>
    /// The hue and saturation bytes whose <see cref="Chroma"/> is nearest a colour's (its brightness is left out).
    /// <paramref name="fitHue"/> or <paramref name="fitSaturation"/> false keeps that byte and fits only the other
    /// (a picker drag that moved only one of them). A colour several pairs give equally (white at any hue, red at hue 0
    /// and 255) keeps <paramref name="hue"/> and then <paramref name="saturation"/>, so picking a colour a light already
    /// has never changes its bytes. Black has no chroma and keeps both.
    /// </summary>
    public static (int Hue, int Saturation) FromColour(double r, double g, double b, int hue, int saturation, bool fitHue = true, bool fitSaturation = true)
    {
        double top = Math.Max(r, Math.Max(g, b));
        if (top <= 0)
        {
            return (hue, saturation);
        }

        var want = new Vec3(r / top, g / top, b / top);
        (int Hue, int Saturation) best = (hue, saturation);
        double bestDistance = double.MaxValue;
        for (int h = fitHue ? 0 : hue; h < (fitHue ? 256 : hue + 1); h++)
        {
            for (int s = fitSaturation ? 0 : saturation; s < (fitSaturation ? 256 : saturation + 1); s++)
            {
                Vec3 d = Chroma(h, s) - want;
                double distance = (d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z);
                const double Tie = 1e-12;
                if (distance < bestDistance - Tie || (distance <= bestDistance + Tie && Preferred(h, s, best, hue, saturation)))
                {
                    best = (h, s);
                    bestDistance = Math.Min(distance, bestDistance);
                }
            }
        }

        return best;
    }

    /// <summary>Of two equally near pairs, the one that keeps the current hue, then the current saturation.</summary>
    private static bool Preferred(int h, int s, (int Hue, int Saturation) best, int hue, int saturation) =>
        (h == hue && best.Hue != hue) || (h == best.Hue && s == saturation && best.Saturation != saturation);

    /// <summary>A zone's m_RefAmbientColor (the pawn ambient lighting.json carries): FGetHSV as truncated bytes.</summary>
    public static (int R, int G, int B) ZoneAmbientBytes(int hue, int saturation, int brightness)
    {
        Vec3 c = Hsv(hue, saturation, brightness);
        return ((int)(c.X * 255), (int)(c.Y * 255), (int)(c.Z * 255));
    }

    /// <summary>The ambient fill of a zone's lightmap texels in stored units (0..1 = 0..255): half of FGetHSV, truncated
    /// to bytes as FLightMap::GetTextureData (0x111b1d10) does.</summary>
    public static Vec3 LightmapAmbient(int hue, int saturation, int brightness)
    {
        Vec3 c = Hsv(hue, saturation, brightness) * (0.5 * 255);
        return new Vec3(Math.Floor(c.X), Math.Floor(c.Y), Math.Floor(c.Z)) / 255;
    }
}
