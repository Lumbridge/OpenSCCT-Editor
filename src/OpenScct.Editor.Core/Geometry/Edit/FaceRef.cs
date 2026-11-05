using System.Globalization;

namespace OpenScct.Editor.Core.Geometry.Edit;

/// <summary>One face of a brush: the brush id and the face's index in its shape. Written <c>Brush3:2</c>.</summary>
public readonly record struct FaceRef(string Brush, int Face)
{
    /// <summary>Parses <c>Brush:face</c>.</summary>
    public static FaceRef Parse(string text)
    {
        int colon = text.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(text[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int face) || face < 0)
        {
            throw new ArgumentException($"a face is Brush:index, not '{text}'");
        }

        return new FaceRef(text[..colon], face);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Brush}:{Face}");
}
