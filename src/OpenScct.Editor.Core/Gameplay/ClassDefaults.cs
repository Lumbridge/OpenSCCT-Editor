using OpenScct.Content.Json;
using OpenScct.Editor.Core.Classes;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>The class catalog the gameplay code reads, and actor values that fall back to their class defaults.</summary>
public static class ClassDefaults
{
    private static readonly Lazy<IClassCatalog> s_fallback = new(() => ClassCatalog.FromContent(EditorServices.ContentRoot));

    /// <summary>The class catalog (the registered one, else the content's).</summary>
    public static IClassCatalog Catalog => EditorServices.Classes ?? s_fallback.Value;

    /// <summary>True when the actor's class is <paramref name="fullClass"/> or derives from it.</summary>
    public static bool IsA(Actor a, string fullClass) =>
        string.Equals(a.Class, fullClass, StringComparison.OrdinalIgnoreCase) || Catalog.IsA(a.Class, fullClass);

    /// <summary>An actor's number property: stored, else the class default, else <paramref name="fallback"/>.</summary>
    public static double Number(Actor a, string prop, double fallback) =>
        (a.Props[prop] ?? Catalog.Default(a.Class, prop)) is SvJsonNumber n ? PropValues.Num(n) : fallback;

    /// <summary>An actor's bool property: stored, else the class default, else false (UnrealScript's default).</summary>
    public static bool Flag(Actor a, string prop) => (a.Props[prop] ?? Catalog.Default(a.Class, prop))?.Kind == SvJsonKind.True;
}
