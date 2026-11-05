using System.Collections.Concurrent;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// Class defaults and the class tree the compiler needs (collision flags, cylinder sizes, light values, zone actors).
/// From the editor's class catalog when one is registered, else from OpenSCCT's <c>shared/classes.json</c> (merged
/// effective defaults per class), else a small built-in tree with zero defaults. A property a class's defaults leave
/// out is zero (svm.classes/1).
/// </summary>
public sealed class ClassDefaults
{
    private static readonly ConcurrentDictionary<string, SvJsonObject?> s_files = new(StringComparer.OrdinalIgnoreCase);

    // Enough of the class tree to classify actors without any class data.
    private static readonly Dictionary<string, string> s_builtinSupers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Engine.Info"] = "Engine.Actor",
        ["Engine.ZoneInfo"] = "Engine.Info",
        ["Engine.SkyZoneInfo"] = "Engine.ZoneInfo",
        ["Engine.WarpZoneInfo"] = "Engine.ZoneInfo",
        ["Engine.LevelInfo"] = "Engine.ZoneInfo",
        ["Engine.Light"] = "Engine.Actor",
        ["Engine.Spotlight"] = "Engine.Light",
        ["Engine.SunLight"] = "Engine.Light",
        ["Engine.Brush"] = "Engine.Actor",
        ["Engine.Mover"] = "Engine.Brush",
        ["Engine.Volume"] = "Engine.Brush",
        ["Engine.BlockingVolume"] = "Engine.Volume",
        ["Engine.PhysicsVolume"] = "Engine.Volume",
        ["Engine.NavigationPoint"] = "Engine.Actor",
        ["Engine.PlayerStart"] = "Engine.NavigationPoint",
        ["Engine.StaticMeshActor"] = "Engine.Actor",
        ["Engine.Triggers"] = "Engine.Actor",
        ["Engine.Trigger"] = "Engine.Triggers",
    };

    private readonly IClassCatalog? _catalog;
    private readonly SvJsonObject? _classes;
    private readonly ConcurrentDictionary<string, SvJsonObject> _merged = new(StringComparer.OrdinalIgnoreCase);

    private ClassDefaults(IClassCatalog? catalog, SvJsonObject? classes, string source)
    {
        _catalog = catalog;
        _classes = classes;
        Source = source;
    }

    /// <summary>Where the defaults come from, for build reports.</summary>
    public string Source { get; }

    public bool HasData => _catalog is not null || _classes is not null;

    /// <summary>The raw <c>shared/classes.json</c> when it was loaded (for writing <c>sim/classes.json</c>).</summary>
    public SvJsonObject? ClassFile => _classes;

    public static ClassDefaults Create(string? contentRoot, IClassCatalog? catalog)
    {
        SvJsonObject? classes = null;
        string? path = contentRoot is null ? null : Path.Combine(contentRoot, "shared", "classes.json");
        if (path is not null && File.Exists(path))
        {
            classes = s_files.GetOrAdd(Path.GetFullPath(path), p => SvJson.Parse(File.ReadAllBytes(p), "shared/classes.json") is SvJsonObject root
                ? root["classes"] as SvJsonObject
                : null);
        }

        string source = catalog is not null ? catalog.SourceDescription
            : classes is not null ? $"{path} ({classes.Count} classes)"
            : "built-in class tree (no class data)";
        return new ClassDefaults(catalog, classes, source);
    }

    public string? SuperOf(string cls)
    {
        if (_catalog?.Find(cls) is { } info)
        {
            return info.Super;
        }

        if (_classes?[cls] is SvJsonObject entry)
        {
            return entry["super"] is SvJsonString s ? s.Value : null;
        }

        return s_builtinSupers.GetValueOrDefault(cls);
    }

    public bool Known(string cls) => _catalog?.Find(cls) is not null || _classes?[cls] is not null || s_builtinSupers.ContainsKey(cls);

    public bool IsA(string cls, string ancestor)
    {
        if (_catalog is not null && _catalog.Find(cls) is not null)
        {
            return _catalog.IsA(cls, ancestor);
        }

        for (string? c = cls; c is not null; c = SuperOf(c))
        {
            if (string.Equals(c, ancestor, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The effective defaults of a class (merged over its chain).</summary>
    public SvJsonObject DefaultsOf(string cls) => _merged.GetOrAdd(cls, Load);

    public SvJsonNode? Default(string cls, string prop) =>
        _catalog?.Find(cls) is not null ? _catalog.Default(cls, prop) : DefaultsOf(cls)[prop];

    private SvJsonObject Load(string cls)
    {
        if (_catalog?.Find(cls) is { } info)
        {
            return info.Defaults;
        }

        // shared/classes.json defaults are already merged over the chain; walk up for a class listed without any.
        for (string? c = cls; c is not null; c = SuperOf(c))
        {
            if (_classes?[c] is SvJsonObject entry && entry["defaults"] is SvJsonObject d)
            {
                return d;
            }
        }

        return new SvJsonObject();
    }
}
