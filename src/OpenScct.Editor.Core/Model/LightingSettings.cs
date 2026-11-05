namespace OpenScct.Editor.Core.Model;

/// <summary>How the lightmapper bakes.</summary>
public enum LightmapMode
{
    /// <summary>Supersampled soft shadows, merged charts, no size cap, clean padding. The default.</summary>
    Quality,

    /// <summary>Samples as the original editor: one lightmap per brush face at the original's sizes (the 256-texel cap),
    /// one shadow bit per texel centre and a 3x3 filter, per-light byte rounding. Matches the stock lightmaps closely,
    /// not exactly: the filter's weights are a guess and charts are packed into this editor's atlases.</summary>
    Faithful,
}

/// <summary>
/// The map's lightmap settings, saved with the project (they change the compiled map, so they are not workspace
/// state). Texel size on a face is its LightMapScale divided by <see cref="Density"/>.
/// </summary>
public sealed class LightingSettings
{
    public const double DefaultDensity = 2;
    public const int DefaultSamples = 4;
    public const double DefaultLightSize = 8;
    public const double DefaultSunSize = 0.5;
    public const int DefaultBounceSamples = 64;
    public const int DefaultAtlasSize = 1024;

    // The ranges Problem checks (the settings panel clamps to them too).
    public const double MinDensity = 0.125, MaxDensity = 16;
    public const int MinSamples = 1, MaxSamples = 16;
    public const double MaxLightSize = 256, MaxSunSize = 10, MaxBounce = 2;
    public const int MinBounceSamples = 4, MaxBounceSamples = 1024;

    /// <summary>The atlas sides allowed.</summary>
    public static readonly int[] AtlasSizes = [512, 1024, 2048, 4096];

    public LightmapMode Mode { get; set; } = LightmapMode.Quality;

    /// <summary>Texels per LightMapScale: 1 is the original's density (32 uu texels on a default face), 2 is twice as
    /// sharp. Faithful mode ignores it.</summary>
    public double Density { get; set; } = DefaultDensity;

    /// <summary>Shadow samples per texel side where a shadow edge crosses the texel (4 = 16 rays). Quality mode.</summary>
    public int Samples { get; set; } = DefaultSamples;

    /// <summary>Radius of point and spot lights in uu, for soft shadow edges (0 = hard). Quality mode.</summary>
    public double LightSize { get; set; } = DefaultLightSize;

    /// <summary>The sun's angular diameter in degrees, for soft sun shadows (0 = hard). Quality mode.</summary>
    public double SunSize { get; set; } = DefaultSunSize;

    /// <summary>Gain of one bounce of indirect light; 0 (the default) keeps the original's look, where corners that only
    /// see indirect light stay dark for the spy.</summary>
    public double Bounce { get; set; }

    public int BounceSamples { get; set; } = DefaultBounceSamples;

    /// <summary>Lightmap atlas side in texels (512, 1024, 2048 or 4096).</summary>
    public int AtlasSize { get; set; } = DefaultAtlasSize;

    /// <summary>Rebake an imported map's own lightmaps with the project's lights, keeping its lightmap layout. A converted
    /// map keeps only its in-game lights, so the lights that only lit the original's lightmaps have to be added back
    /// first, or the relit map comes out darker (the build warns).</summary>
    public bool RelightImported { get; set; }

    /// <summary>The LightRadius the bake gives a light that stores none, in place of the class default (64); null keeps
    /// the class default. The editors that baked the stock maps disagree on it (a light that stores no radius bakes as
    /// 32 on some maps, 64 on others), so an .sdc import measures it against the cooked bake. Only the bake uses it: the
    /// game still gets the class default.</summary>
    public double? BakeDefaultLightRadius { get; set; }

    public bool IsDefault =>
        Mode == LightmapMode.Quality && Density == DefaultDensity && Samples == DefaultSamples && LightSize == DefaultLightSize &&
        SunSize == DefaultSunSize && Bounce == 0 && BounceSamples == DefaultBounceSamples && AtlasSize == DefaultAtlasSize && !RelightImported
        && BakeDefaultLightRadius is null;

    public LightingSettings Clone() => (LightingSettings)MemberwiseClone();

    /// <summary>Checks the ranges; returns the first problem or null.</summary>
    public string? Problem() =>
        Density is < MinDensity or > MaxDensity || double.IsNaN(Density) ? "density is 0.125 to 16"
        : Samples is < MinSamples or > MaxSamples ? "samples is 1 to 16"
        : LightSize is < 0 or > MaxLightSize || double.IsNaN(LightSize) ? "lightSize is 0 to 256 uu"
        : SunSize is < 0 or > MaxSunSize || double.IsNaN(SunSize) ? "sunSize is 0 to 10 degrees"
        : Bounce is < 0 or > MaxBounce || double.IsNaN(Bounce) ? "bounce is 0 to 2"
        : BounceSamples is < MinBounceSamples or > MaxBounceSamples ? "bounceSamples is 4 to 1024"
        : !AtlasSizes.Contains(AtlasSize) ? "atlasSize is 512, 1024, 2048 or 4096"
        : BakeDefaultLightRadius is < 0 or > 255 || (BakeDefaultLightRadius is { } r && double.IsNaN(r)) ? "bakeDefaultLightRadius is 0 to 255"
        : null;
}
