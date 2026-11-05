using Godot;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Assets;

/// <summary>
/// The square tile asset and prefab pictures sit on in their grids: a fill with rounded corners and a hairline edge,
/// transparent outside, so a dark mesh, a thin texture strip or a sparse wireframe still reads as one tile. Pure image
/// work, safe on a worker thread.
/// </summary>
public static class PictureTile
{
    /// <summary>An empty tile of <paramref name="size"/> pixels in <paramref name="fill"/>.</summary>
    public static Image Make(int size, Color fill)
    {
        Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        image.Fill(fill);
        Shape(image, fill);
        return image;
    }

    /// <summary><paramref name="picture"/> centred on a tile of <paramref name="size"/> (blended, so its transparent
    /// parts show the tile). A picture larger than the tile is scaled down to fit inside its edge.</summary>
    public static Image Put(Image picture, int size, Color fill)
    {
        Image tile = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        tile.Fill(fill);
        if (picture.IsCompressed())
        {
            picture.Decompress();
        }

        picture.Convert(Image.Format.Rgba8);
        int inner = size - 2;
        int w = picture.GetWidth(), h = picture.GetHeight();
        if (w > inner || h > inner)
        {
            float k = (float)inner / Math.Max(w, h);
            picture.Resize(Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k)), Image.Interpolation.Bilinear);
            w = picture.GetWidth();
            h = picture.GetHeight();
        }

        tile.BlendRect(picture, new Rect2I(0, 0, w, h), new Vector2I((size - w) / 2, (size - h) / 2));
        Shape(tile, fill);
        return tile;
    }

    /// <summary>Rounds the corners (transparent outside them) and draws the hairline edge over what is there.</summary>
    private static void Shape(Image image, Color fill)
    {
        int size = image.GetWidth();
        Color edge = fill.Blend(EditorTheme.Divider);
        float r = EditorTheme.RadiusM;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Distance outside the inner rectangle the corner arcs are centred on.
                float dx = Math.Max(Math.Max(r - (x + 0.5f), x + 0.5f - (size - r)), 0);
                float dy = Math.Max(Math.Max(r - (y + 0.5f), y + 0.5f - (size - r)), 0);
                float d = MathF.Sqrt(dx * dx + dy * dy);
                bool onEdge = x == 0 || y == 0 || x == size - 1 || y == size - 1 || d > r - 1;
                if (!onEdge)
                {
                    continue;
                }

                Color c = edge;
                c.A *= Math.Clamp(r - d + 0.5f, 0, 1);
                image.SetPixel(x, y, c);
            }
        }
    }
}
