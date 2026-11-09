using OpenScct.Content.Materials;
using OpenScct.Editor.Core.Identity;

namespace OpenScct.Editor.Core.Assets;

/// <summary>CPU data-map sampler. A 16-bit PNG retains its low bits through displacement and bump generation.</summary>
public sealed class MaterialImage
{
    private readonly Dictionary<int, (int Channels, ushort[] Values)> _high = new();
    private readonly RgbaImage _image;
    public MaterialImage(byte[] bytes)
    {
        _image = RgbaImage.FormatOf(bytes) == "PNG" ? RgbaImage.DecodePng(bytes, (row, _, channels, values) => _high[row] = (channels, values)) : RgbaImage.Decode(bytes);
    }
    public double Sample(double u, double v, MaterialTexture binding)
    {
        double x = (u - Math.Floor(u)) * _image.Width - .5, y = (v - Math.Floor(v)) * _image.Height - .5;
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y); double fx = x - ix, fy = y - iy;
        double At(int xx, int yy)
        {
            xx = (xx % _image.Width + _image.Width) % _image.Width; yy = (yy % _image.Height + _image.Height) % _image.Height;
            double Component(int channel)
            {
                if (_high.TryGetValue(yy, out var high))
                {
                    if (channel == 3 && high.Channels is 1 or 3) return 1;
                    if (high.Channels is 1 or 2) channel = channel == 3 ? 1 : 0;
                    return high.Values[xx * high.Channels + channel] / 65535.0;
                }
                return _image.Pixels[(yy * _image.Width + xx) * 4 + channel] / 255.0;
            }
            return binding.Channel == "luminance" ? .2126 * Component(0) + .7152 * Component(1) + .0722 * Component(2) : Component(binding.Channel switch { "g" => 1, "b" => 2, "a" => 3, _ => 0 });
        }
        double value = (At(ix, iy) * (1 - fx) + At(ix + 1, iy) * fx) * (1 - fy) + (At(ix, iy + 1) * (1 - fx) + At(ix + 1, iy + 1) * fx) * fy;
        if (binding.Srgb && binding.Channel != "a") value = value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        return binding.Invert ? 1 - value : value;
    }
}
