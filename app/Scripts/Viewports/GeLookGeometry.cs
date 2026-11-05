using System.Runtime.CompilerServices;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Viewports;

/// <summary>The procedural GE look shared by drawing, selection and framing, cached until its points or settings change.</summary>
public static class GeLookGeometry
{
    private static readonly ConditionalWeakTable<Ge, Cached> s_cache = new();

    public static IReadOnlyList<WorldBrush> Of(Ge ge)
    {
        int key = Key(ge);
        if (s_cache.TryGetValue(ge, out Cached? cached) && cached.Key == key)
        {
            return cached.Brushes;
        }

        List<WorldBrush> brushes;
        try
        {
            brushes = GeGeometry.Build(ge).Select(BrushGeometry.Of).ToList();
        }
        catch (ArgumentException)
        {
            // Map Check reports invalid authored settings; opening their map must remain possible so they can be fixed.
            brushes = [];
        }
        s_cache.AddOrUpdate(ge, new Cached(key, brushes));
        return brushes;
    }

    public static int Key(Ge ge)
    {
        var hash = new HashCode();
        hash.Add(ge.Kind);
        foreach (Vec3 point in ge.Points)
        {
            hash.Add(point);
        }

        hash.Add(ge.Appearance?.Width);
        hash.Add(ge.Appearance?.Radius);
        hash.Add(ge.Appearance?.Spacing);
        hash.Add(ge.Appearance?.Material);
        return hash.ToHashCode();
    }

    private sealed record Cached(int Key, IReadOnlyList<WorldBrush> Brushes);
}
