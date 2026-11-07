using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Lightmaps;

/// <summary>
/// What one bake reads: the charts to light, the lights, the shadow scene and the zones' ambient. Built from a
/// compile (<see cref="Compile.WorldLighting"/>) or from a converted map's own lightmap layout
/// (<see cref="ConvertedWorld"/>).
/// </summary>
public sealed class BakeScene
{
    public required IReadOnlyList<LightmapChart> Charts { get; init; }

    /// <summary>Every light; only <see cref="BakeLight.Baked"/> ones are baked.</summary>
    public required IReadOnlyList<BakeLight> Lights { get; init; }

    public required TriangleBvh Occluders { get; init; }

    /// <summary>RGB light transmitted through glass, after opaque shadow visibility; null leaves it unchanged.</summary>
    public Func<BakeLight, Vec3, Vec3>? Transmission { get; init; }

    /// <summary>The lightmap ambient fill of a zone in stored units (<see cref="LightColour.LightmapAmbient"/>).</summary>
    public required Func<int, Vec3> Ambient { get; init; }

    /// <summary>The mean diffuse colour of a chart piece's material (0..1), for bounce light; null means mid grey.</summary>
    public Func<LightmapChart, ChartPiece, Vec3>? Albedo { get; init; }

    /// <summary>Which chart and piece holds a point of an occluder triangle (by the triangle's
    /// <see cref="OccluderTriangle.Source"/>), or null when the occluder is no lit surface (static meshes, unlit
    /// surfaces). Bounce light needs it; without it there is no bounce.</summary>
    public Func<int, Vec3, (int Chart, int Piece)?>? ChartAt { get; init; }

    /// <summary>Whether a light may light a chart at all, beyond its reach (the original's per-surface light lists:
    /// an imported copy's own, <see cref="SourceLightLists"/>, or an authored map's, LeafLightLists); null admits every
    /// light in reach.</summary>
    public Func<LightmapChart, BakeLight, bool>? Admits { get; init; }
}

/// <summary>How one bake pass samples (the progressive preview runs several, coarse first).</summary>
public sealed record BakeQuality
{
    /// <summary>Trace shadows (off: every light reaches every texel it faces, the first preview pass).</summary>
    public bool Shadows { get; init; } = true;

    /// <summary>The original's sampling: a shadow bit per texel centre, filtered 3x3, per-light byte rounding.</summary>
    public bool Faithful { get; init; }

    /// <summary>Shadow samples per texel side on shadow edges (1 = one ray per texel).</summary>
    public int Samples { get; init; } = 1;

    /// <summary>Point and spot light radius for soft shadows, uu.</summary>
    public double LightSize { get; init; }

    /// <summary>Sun angular diameter, degrees.</summary>
    public double SunSize { get; init; }

    /// <summary>Gain of one bounce (0 = none).</summary>
    public double Bounce { get; init; }

    public int BounceSamples { get; init; } = 64;

    /// <summary>The full-quality pass the settings ask for.</summary>
    public static BakeQuality From(LightingSettings s) => s.Mode == LightmapMode.Faithful
        ? new BakeQuality { Faithful = true }
        : new BakeQuality
        {
            Samples = s.Samples,
            LightSize = s.LightSize,
            SunSize = s.SunSize,
            Bounce = s.Bounce,
            BounceSamples = s.BounceSamples,
        };
}
