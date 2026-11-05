using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Interop;
using OpenScct.Editor.Core.Interop.T3d;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Classes;

/// <summary>Where a property set was found.</summary>
public enum PropertySetSource
{
    /// <summary>The map's own <c>propsets.json</c>.</summary>
    Map,

    /// <summary>The user library.</summary>
    Library,
}

/// <summary>A named preset of property values for one class (the stock editor's "Use Properties Set", e.g.
/// <c>SCamera.blinde</c>), applied to actors of that class or its subclasses.</summary>
public sealed record PropertySet(string Class, string Name, PropSet Props, PropertySetSource Source = PropertySetSource.Library, string? Note = null)
{
    /// <summary>The stock editor's name: <c>ShortClass.Name</c>.</summary>
    public string Key => $"{InteropValues.ShortClass(Class)}.{Name}";
}

/// <summary>
/// Property sets: a map's own (<c>&lt;map&gt;.osmap/propsets.json</c>, which travels with the map) and the user library
/// (<c>OSE_PROPSETS</c>, else <c>propsets.json</c> in <see cref="UserFolders.Settings"/>). Both are <c>osmap.propsets/1</c> files.
/// The stock sets come from the original game's <c>System/PropertiesSets.ini</c> with <see cref="ImportIni"/>; they are
/// game data, so the editor never ships them.
/// </summary>
public static class PropertySets
{
    public const string Format = "osmap.propsets/1";
    public const string FileName = "propsets.json";

    public static string LibraryFile =>
        Environment.GetEnvironmentVariable("OSE_PROPSETS") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(UserFolders.Settings, FileName);

    public static string? MapFile(string? mapFolder) => mapFolder is null ? null : Path.Combine(mapFolder, FileName);

    /// <summary>Every set, the map's first (a map set hides a library set of the same key).</summary>
    public static List<PropertySet> All(string? mapFolder)
    {
        var byKey = new Dictionary<string, PropertySet>(StringComparer.OrdinalIgnoreCase);
        foreach ((string? file, PropertySetSource source) in new[] { (MapFile(mapFolder), PropertySetSource.Map), (LibraryFile, PropertySetSource.Library) })
        {
            if (file is not null && File.Exists(file))
            {
                foreach (PropertySet s in Read(file, source))
                {
                    byKey.TryAdd(s.Key, s);
                }
            }
        }

        return byKey.Values.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The sets that fit an actor's class (sets for the class or one of its parents).</summary>
    public static List<PropertySet> For(string className, string? mapFolder) =>
        All(mapFolder).Where(s => ClassDefaults.Catalog.IsA(className, s.Class) || string.Equals(className, s.Class, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>A set by its key (<c>SCamera.blinde</c>, ignoring case), or null.</summary>
    public static PropertySet? Find(string key, string? mapFolder) =>
        All(mapFolder).FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    public static List<PropertySet> Read(string file, PropertySetSource source)
    {
        SvJsonNode root = SvJson.Parse(File.ReadAllBytes(file), file);
        if (root is not SvJsonObject o || (o["format"] as SvJsonString)?.Value != Format)
        {
            throw new InvalidDataException($"{file} is not a property set file (format {Format})");
        }

        var sets = new List<PropertySet>();
        foreach (SvJsonObject s in (o["sets"] as SvJsonArray ?? []).OfType<SvJsonObject>())
        {
            if (s["class"] is SvJsonString cls && s["name"] is SvJsonString name)
            {
                sets.Add(new PropertySet(cls.Value, name.Value, PropSet.FromJson(s["props"]), source, (s["note"] as SvJsonString)?.Value));
            }
        }

        return sets;
    }

    /// <summary>Adds or replaces sets in a file (same key replaces), creating the file and its folder.</summary>
    public static void Save(string file, IEnumerable<PropertySet> sets)
    {
        List<PropertySet> all = File.Exists(file) ? Read(file, PropertySetSource.Library) : [];
        foreach (PropertySet s in sets)
        {
            all.RemoveAll(x => string.Equals(x.Key, s.Key, StringComparison.OrdinalIgnoreCase));
            all.Add(s);
        }

        Write(file, all);
    }

    /// <summary>Removes a set by key from a file; false when it was not there.</summary>
    public static bool Delete(string file, string key)
    {
        if (!File.Exists(file))
        {
            return false;
        }

        List<PropertySet> all = Read(file, PropertySetSource.Library);
        int removed = all.RemoveAll(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            Write(file, all);
        }

        return removed > 0;
    }

    /// <summary>
    /// Reads the stock editor's <c>PropertiesSets.ini</c> (<c>[Class.Set]</c> sections of <c>Key=Value</c> lines in T3D
    /// text) into typed sets, using the class schema to type the values.
    /// </summary>
    public static List<PropertySet> ImportIni(string iniFile)
    {
        var types = new T3dTypes(Interop.UeSchema.Load(EditorServices.ContentRoot), T3dTypes.LevelPackage);
        var sets = new List<PropertySet>();
        string? cls = null, name = null;
        var fields = new List<T3dField>();

        void Flush()
        {
            if (cls is not null && name is not null)
            {
                sets.Add(new PropertySet(cls, name, types.ToProps(cls, fields), PropertySetSource.Library, $"from {Path.GetFileName(iniFile)}"));
            }

            fields.Clear();
        }

        foreach (string raw in File.ReadAllLines(iniFile))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            if (line[0] == '[' && line.EndsWith(']'))
            {
                Flush();
                string section = line[1..^1].Trim();
                int dot = section.IndexOf('.');
                (cls, name) = dot > 0 ? (InteropValues.ResolveClass(types.Schema, section[..dot]), section[(dot + 1)..]) : (null, null);
                continue;
            }

            if (cls is not null && T3dValueParser.ParseField(line) is { } field)
            {
                fields.Add(field);
            }
        }

        Flush();
        return sets;
    }

    /// <summary>
    /// Applies a set to actors, one undo step: each actor of the set's class (or a subclass) takes every value of the
    /// set its class has. Actors of other classes are listed in <paramref name="skipped"/>.
    /// </summary>
    public static ReplaceObjectsCommand Apply(MapProject p, PropertySet set, IEnumerable<string> ids, List<string> skipped)
    {
        var cmd = new ReplaceObjectsCommand($"Use property set {set.Key}");
        foreach (Actor a in ids.Select(p.FindActor).OfType<Actor>())
        {
            if (!ClassDefaults.Catalog.IsA(a.Class, set.Class) && !string.Equals(a.Class, set.Class, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(a.Id);
                continue;
            }

            Actor after = a.Clone();
            foreach (KeyValuePair<string, SvJsonNode> kv in set.Props)
            {
                after.Props.Set(kv.Key, kv.Value);
            }

            cmd.Actor(a.Clone(), after);
        }

        return cmd;
    }

    /// <summary>A set made from an actor's stored values (only the chosen ones when <paramref name="keys"/> is given).</summary>
    public static PropertySet FromActor(Actor a, string name, IEnumerable<string>? keys = null, PropertySetSource source = PropertySetSource.Library)
    {
        var props = new PropSet();
        HashSet<string>? only = keys?.ToHashSet(StringComparer.Ordinal);
        foreach (KeyValuePair<string, SvJsonNode> kv in a.Props.Where(kv => only?.Contains(kv.Key) ?? !PropertyCopy.UntickedByDefault(kv.Key, kv.Value)))
        {
            props.Set(kv.Key, kv.Value);
        }

        return new PropertySet(a.Class, name, props, source);
    }

    private static void Write(string file, IEnumerable<PropertySet> sets)
    {
        var arr = new SvJsonArray();
        foreach (PropertySet s in sets.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
        {
            var o = new SvJsonObject();
            o.Add("class", SvJsonNode.String(s.Class));
            o.Add("name", SvJsonNode.String(s.Name));
            if (s.Note is not null)
            {
                o.Add("note", SvJsonNode.String(s.Note));
            }

            o.Add("props", s.Props.ToJson());
            arr.Add(o);
        }

        var root = new SvJsonObject();
        root.Add("format", SvJsonNode.String(Format));
        root.Add("sets", arr);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllBytes(file, CanonicalJson.ToFileBytes(root));
    }
}
