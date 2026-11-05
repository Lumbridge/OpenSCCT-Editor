using System.Numerics;
using OpenScct.Editor.Core.Compile;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>
/// The solid texels of a masked texture: a shadow ray through a grate or a fence passes its holes, as the original's
/// shadow render alpha-tested masked materials. A point is solid where the bilinearly filtered alpha is half or more,
/// as the card's alpha test passes the filtered texel; the texture repeats.
/// </summary>
public sealed class AlphaMask
{
    private readonly byte[] _alpha;

    private AlphaMask(int width, int height, byte[] alpha, ulong key)
    {
        Width = width;
        Height = height;
        _alpha = alpha;
        Key = key;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>A key of the mask's texels (part of each masked occluder's key).</summary>
    public ulong Key { get; }

    /// <summary>The mask of an RGBA image, or null when every texel is solid (no holes to let light through).</summary>
    public static AlphaMask? FromRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        var alpha = new byte[width * height];
        bool holes = false;
        var hash = new Hash64();
        hash.Add(width);
        hash.Add(height);
        hash.Add("bilinear");
        for (int i = 0; i < alpha.Length; i++)
        {
            alpha[i] = rgba[(4 * i) + 3];
            holes |= alpha[i] < 128;
            hash.Add(alpha[i]);
        }

        return holes ? new AlphaMask(width, height, alpha, hash.Value) : null;
    }

    /// <summary>True when the texture is solid at texture coordinate <paramref name="uv"/> (bilinear, wrapping).</summary>
    public bool Blocks(Vector2 uv)
    {
        float fx = ((uv.X - MathF.Floor(uv.X)) * Width) - 0.5f, fy = ((uv.Y - MathF.Floor(uv.Y)) * Height) - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        int xa = Wrap(x0, Width), xb = Wrap(x0 + 1, Width), ya = Wrap(y0, Height) * Width, yb = Wrap(y0 + 1, Height) * Width;
        float top = (_alpha[ya + xa] * (1 - tx)) + (_alpha[ya + xb] * tx);
        float bottom = (_alpha[yb + xa] * (1 - tx)) + (_alpha[yb + xb] * tx);
        return (top * (1 - ty)) + (bottom * ty) >= 127.5f;
    }

    private static int Wrap(int i, int n) => ((i % n) + n) % n;
}
