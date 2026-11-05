using System.Globalization;
using System.Text;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.Original;

/// <summary>
/// The map's texts as the original game reads them from <c>System\_PC_</c> (a language file per package, over the text
/// the package stores): <c>&lt;Map&gt;.int</c> holds a section per actor with its localized properties,
/// <c>&lt;Map&gt;-i.int</c> the <c>[MapSettings]</c> section of the map's settings object: the dossier
/// (<c>LegendMap</c>), the places (<c>LegendMapInBrief</c>), the briefings and <c>ModesStrings</c>, one struct per mode in
/// the order of the settings' modes. ANSI, CRLF, after the stock header.
/// </summary>
public static class InterfaceTexts
{
    private const string Header = "\r\nSection\r\nTag=\"ENG\"\r\n\r\n";

    /// <summary>The members of a mode's texts, with the string ids the stock files write after each name.</summary>
    private static readonly (string Key, int Id)[] s_modeMembers =
    [
        ("ExplicationModeSpy", 6553), ("ExplicationModeSentry", 6550), ("ExpliModeInBriefSpy", 6554),
        ("LegendModeInBriefSpy", 6555), ("ExpliModeInBriefSentry", 6551), ("LegendModeInBriefSentry", 6552),
    ];

    private static readonly string[] s_mapFirst =
        [SideFiles.LegendMap, SideFiles.LegendMapInBrief, "ExpliMapInBriefSpy", "ExpliMapInBriefSentry"];

    /// <summary>The modes in the order <c>ModesStrings</c> lists them: the map's settings by game mode id, then any
    /// other mode the texts name. The map's settings in the original editor must list the modes in this order.</summary>
    public static List<string> ModeOrder(MapProject p)
    {
        var order = p.MapSettings.OrderBy(kv => kv.Value.GetInt("GameModeId", int.MaxValue)).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key).ToList();
        if (p.MapInfo["modes"] is SvJsonObject modes)
        {
            order.AddRange(modes.Select(kv => kv.Key).Where(m => !order.Contains(m)).Order(StringComparer.Ordinal));
        }

        return order;
    }

    /// <summary><c>&lt;Map&gt;-i.int</c>: the map's settings texts, the dossier and places from its identity when it
    /// has them (as the compiled map's briefing takes them); null when the map has none.</summary>
    public static byte[]? SettingsFile(MapProject p)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, SvJsonNode value) in p.MapInfo)
        {
            if (value is SvJsonString s)
            {
                values[key] = s.Value;
            }
        }

        if (!p.Identity.Dossier.IsEmpty)
        {
            values[SideFiles.LegendMap] = SideFiles.LegendOf(p.Identity.Dossier);
        }

        if (p.Identity.Places.Count > 0)
        {
            values[SideFiles.LegendMapInBrief] = SideFiles.LegendOf(p.Identity.Places);
        }

        var modes = p.MapInfo["modes"] as SvJsonObject;
        if (values.Count == 0 && modes is not { Count: > 0 })
        {
            return null;
        }

        var sb = new StringBuilder(Header).Append("[MapSettings]\r\n");
        foreach (string key in s_mapFirst.Where(values.ContainsKey).Concat(values.Keys.Where(k => !s_mapFirst.Contains(k)).Order(StringComparer.Ordinal)))
        {
            sb.Append(key).Append('=').Append(Quote(values[key])).Append("\r\n");
        }

        List<string> order = ModeOrder(p);
        if (order.Count > 0 && modes is { Count: > 0 })
        {
            sb.Append("ModesStrings=(");
            for (int i = 0; i < order.Count; i++)
            {
                var texts = modes[order[i]] as SvJsonObject;
                sb.Append(i > 0 ? ",(" : "(");
                sb.Append(string.Join(',', s_modeMembers.Select(m => string.Create(CultureInfo.InvariantCulture,
                    $"{m.Key}\u00a7({m.Id})={Quote((texts?[m.Key] as SvJsonString)?.Value ?? "")}"))));
                sb.Append(')');
            }

            sb.Append(")\r\n");
        }

        return Ansi(sb.ToString());
    }

    /// <summary><c>&lt;Map&gt;.int</c>: a section per object (the LevelInfo and each actor written) with its localized
    /// text properties; null when there are none.</summary>
    public static byte[]? ActorFile(MapProject p, UeSchema schema, IEnumerable<Actor> actors, string levelInfoName, string title)
    {
        var sections = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        PropSet levelInfo = p.LevelInfo.Clone();
        if (title.Length > 0)
        {
            levelInfo.Set("Title", SvJsonNode.String(title));
        }

        Add(sections, levelInfoName, Localized(schema, "Engine.LevelInfo", levelInfo));
        foreach (Actor a in actors)
        {
            Add(sections, a.Id, Localized(schema, a.Class, a.Props));
        }

        if (sections.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder(Header);
        foreach ((string name, List<string> lines) in sections)
        {
            sb.Append('[').Append(name).Append("]\r\n");
            foreach (string line in lines)
            {
                sb.Append(line).Append("\r\n");
            }

            sb.Append("\r\n");
        }

        return Ansi(sb.ToString());
    }

    private static void Add(SortedDictionary<string, List<string>> sections, string name, List<string> lines)
    {
        if (lines.Count > 0)
        {
            sections[name] = lines;
        }
    }

    /// <summary>An object's localized text properties as language-file lines (a static array's elements as Name[i]).</summary>
    private static List<string> Localized(UeSchema schema, string className, PropSet props)
    {
        var lines = new List<string>();
        foreach ((string key, SvJsonNode value) in props)
        {
            if (schema.FindProperty(className, key) is not { Localized: true, Type: "string" } prop)
            {
                continue;
            }

            if (value is SvJsonString s)
            {
                lines.Add($"{prop.Name}={Quote(s.Value)}");
            }
            else if (value is SvJsonObject o && o[T3d.T3dTypes.ElementsKey] is SvJsonObject elements)
            {
                lines.AddRange(elements.Where(kv => kv.Value is SvJsonString).Select(kv => $"{prop.Name}[{kv.Key}]={Quote(((SvJsonString)kv.Value).Value)}"));
            }
        }

        return lines;
    }

    /// <summary>A quoted value: the files have no escapes, so a double quote becomes a single one and a line break the
    /// <c>|</c> the menus break lines at.</summary>
    private static string Quote(string text) =>
        "\"" + text.Replace("\r\n", "|", StringComparison.Ordinal).Replace('\n', '|').Replace('\r', '|').Replace('"', '\'') + "\"";

    /// <summary>The text in the Windows code page the original game reads its language files in (typographic quotes and
    /// dashes as their plain forms, anything else outside Latin-1 as '?').</summary>
    private static byte[] Ansi(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(c switch
            {
                '\u2018' or '\u2019' => "'",
                '\u201c' or '\u201d' => "'",
                '\u2013' or '\u2014' => "-",
                '\u2026' => "...",
                > '\u00ff' => "?",
                _ => c.ToString(),
            });
        }

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
