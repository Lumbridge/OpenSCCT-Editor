using OpenScct.Content.Json;
using OpenScct.Editor.Core.Interop.Sdc;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop;

/// <summary>A class an original map defines itself (UnrealScript compiled into its MyLevel package): its name, its
/// parent class and its script source, when the package still holds it.</summary>
public sealed record MapClass(string Name, string? Parent, string? Script);

/// <summary>
/// The classes an original map defines itself, such as RE+'s <c>SCharacterOutfits</c> and <c>SMatchSizeRules</c> or a
/// map's own scripts (<c>AerTreadScroll</c>). An .sdc import keeps them in <see cref="MapProject.Extra"/> under
/// <see cref="ExtraKey"/> (<c>{Name: {parent, script}}</c>), so the export for the original editor can hand their
/// source back: a new map in the stock editor has none of them, and its T3D import skips actors of a class it does
/// not know.
/// </summary>
public static class MapClasses
{
    public const string ExtraKey = "mapClasses";

    /// <summary>Reads the classes a package defines (top-level <c>Class</c> exports) with their <c>ScriptText</c>.</summary>
    public static List<MapClass> Read(UPackage pkg)
    {
        var classes = new List<MapClass>();
        foreach (UExport e in pkg.Exports.Where(e => e.ClassRef == 0 && e.Outer == 0))
        {
            string? parent = e.SuperRef == 0 ? null : pkg.ObjectName(e.SuperRef);
            string? script = null;
            UExport? text = pkg.Exports.FirstOrDefault(t => t.Outer == e.Index + 1 && t.Name.Equals("ScriptText", StringComparison.OrdinalIgnoreCase)
                && pkg.ClassName(t).Equals("TextBuffer", StringComparison.OrdinalIgnoreCase));
            if (text is not null)
            {
                try
                {
                    // UTextBuffer: its (empty) property list, Pos, Top, then the text.
                    UReader r = pkg.Reader(text);
                    r.Name();
                    r.I32();
                    r.I32();
                    script = r.FString();
                }
                catch (InvalidDataException)
                {
                    script = null; // a cooked copy may keep the export but not a readable text
                }
            }

            classes.Add(new MapClass(e.Name, parent, string.IsNullOrWhiteSpace(script) ? null : script));
        }

        return classes;
    }

    /// <summary>Stores the classes in the project (nothing when the map defines none).</summary>
    public static void Keep(MapProject p, IReadOnlyList<MapClass> classes)
    {
        if (classes.Count == 0)
        {
            return;
        }

        var o = new SvJsonObject();
        foreach (MapClass c in classes.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var entry = new SvJsonObject();
            if (c.Parent is not null)
            {
                entry.Add("parent", SvJsonNode.String(c.Parent));
            }

            if (c.Script is not null)
            {
                entry.Add("script", SvJsonNode.String(c.Script));
            }

            o.TryAdd(c.Name, entry);
        }

        p.Extra.TryAdd(ExtraKey, o);
    }

    /// <summary>The classes a project keeps from its import, by name.</summary>
    public static Dictionary<string, MapClass> Of(MapProject p)
    {
        var map = new Dictionary<string, MapClass>(StringComparer.OrdinalIgnoreCase);
        if (p.Extra[ExtraKey] is SvJsonObject o)
        {
            foreach ((string name, SvJsonNode value) in o)
            {
                var e = value as SvJsonObject;
                map[name] = new MapClass(name, (e?["parent"] as SvJsonString)?.Value, (e?["script"] as SvJsonString)?.Value);
            }
        }

        return map;
    }

    /// <summary>True when an actor's class is one the map defines itself: a class of the map's own package (as an .sdc
    /// import names it), of MyLevel (as a T3D import does), or one the project keeps from its import.</summary>
    public static bool IsMapClass(MapProject p, Actor a, IReadOnlyDictionary<string, MapClass> known)
    {
        int dot = a.Class.LastIndexOf('.');
        string package = dot > 0 ? a.Class[..dot] : "";
        return package.Equals(p.Id, StringComparison.OrdinalIgnoreCase) || package.Equals(T3d.T3dTypes.LevelPackage, StringComparison.OrdinalIgnoreCase)
            || known.ContainsKey(a.ShortClass);
    }
}
