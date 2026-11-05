using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Edit;

/// <summary>The per-face poly flags the surface tools edit, by name (UE2 Surface Properties).</summary>
public static class SurfaceFlags
{
    public static readonly IReadOnlyList<(string Name, int Bit, string Help)> All =
    [
        ("invisible", PolyFlags.Invisible, "Not drawn (still collides)"),
        ("masked", PolyFlags.Masked, "Alpha-tested texture"),
        ("translucent", PolyFlags.Translucent, "See-through"),
        ("twoSided", PolyFlags.TwoSided, "Drawn from both sides"),
        ("unlit", PolyFlags.Unlit, "Full bright, no lighting"),
        ("specialLit", PolyFlags.SpecialLit, "Lit only by special lights"),
        ("modulated", PolyFlags.Modulated, "Multiplies what is behind"),
        ("fakeBackdrop", PolyFlags.FakeBackdrop, "Shows the skybox"),
        ("mirror", PolyFlags.Mirrored, "Mirror surface"),
        ("environment", PolyFlags.Environment, "Environment mapped"),
        ("noSmooth", PolyFlags.NoSmooth, "No texture filtering"),
        ("portal", PolyFlags.Portal, "Zone portal"),
        ("antiPortal", PolyFlags.AntiPortal, "Anti-portal (occluder)"),
        ("semisolid", PolyFlags.Semisolid, "Does not cut the BSP"),
        ("notSolid", PolyFlags.NotSolid, "No collision"),
    ];

    /// <summary>The bits of a comma-separated list of flag names (ignoring case), or a number.</summary>
    public static int Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        int bits = 0;
        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n))
            {
                bits |= n;
                continue;
            }

            (string Name, int Bit, string Help) hit = All.FirstOrDefault(f => string.Equals(f.Name, part, StringComparison.OrdinalIgnoreCase));
            bits |= hit.Name is null
                ? throw new ArgumentException($"unknown surface flag '{part}' ({string.Join(", ", All.Select(f => f.Name))})")
                : hit.Bit;
        }

        return bits;
    }

    /// <summary>The names of the flags set in <paramref name="bits"/>.</summary>
    public static IEnumerable<string> Names(int bits) => All.Where(f => (bits & f.Bit) != 0).Select(f => f.Name);
}
