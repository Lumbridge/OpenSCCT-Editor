using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Classes;

/// <summary>Properties copied from an actor: its class and stored values (<c>osmap.props/1</c>, the text the clipboard
/// carries, so a copy pastes into another map too).</summary>
public sealed record CopiedProperties(string Class, string From, PropSet Props)
{
    public const string Format = "osmap.props/1";

    public SvJsonObject ToJson()
    {
        var o = new SvJsonObject();
        o.Add("format", SvJsonNode.String(Format));
        o.Add("class", SvJsonNode.String(Class));
        o.Add("from", SvJsonNode.String(From));
        o.Add("props", Props.ToJson());
        return o;
    }

    /// <summary>Reads copied properties from clipboard text, or null when the text is something else.</summary>
    public static CopiedProperties? TryParse(string text)
    {
        if (!text.TrimStart().StartsWith('{'))
        {
            return null;
        }

        try
        {
            return FromJson(SvJson.Parse(text, "clipboard"));
        }
        catch (OpenScct.Content.SvMapFormatException)
        {
            return null;
        }
    }

    /// <summary>Reads an <c>osmap.props/1</c> document, or null when the value is something else.</summary>
    public static CopiedProperties? FromJson(SvJsonNode? node) =>
        node is SvJsonObject o && (o["format"] as SvJsonString)?.Value == Format && o["class"] is SvJsonString cls
            ? new CopiedProperties(cls.Value, (o["from"] as SvJsonString)?.Value ?? "", PropSet.FromJson(o["props"]))
            : null;
}

/// <summary>
/// Copy and paste of actor properties (RE+ 2.7): copy an actor's stored values, tick which to paste, paste them onto
/// other actors (of any class that has those properties), and paste the same choice again. Values that tie an actor
/// to its place or its wiring start unticked.
/// </summary>
public static class PropertyCopy
{
    /// <summary>Stored values that belong to where the actor is or what it is wired to, not to how it behaves.</summary>
    private static readonly HashSet<string> s_placeBound = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tag", "Event", "Events", "Group", "Level", "Region", "StaticMeshInstance", "BasePos", "BaseRot", "KeyPos", "KeyRot", "KeyNum",
        "Name", "CamName", "ObjectiveName", "NextCam", "PrevCam", "bFirstCam",
    };

    public static CopiedProperties Copy(Actor a) => new(a.Class, a.Id, a.Props.Clone());

    /// <summary>True for values a paste leaves unticked unless asked: identity, placement and links to other actors.</summary>
    public static bool UntickedByDefault(string key, SvJsonNode value) => s_placeBound.Contains(key) || HasActorRef(value);

    /// <summary>The keys ticked by default for a copy.</summary>
    public static List<string> DefaultKeys(CopiedProperties copy) =>
        copy.Props.Where(kv => !UntickedByDefault(kv.Key, kv.Value)).Select(kv => kv.Key).ToList();

    /// <summary>
    /// Pastes the chosen values onto actors, one undo step. An actor takes each value its class has (by the class
    /// catalog; the copied class's own properties always fit actors of that class); the values it has no property for
    /// are listed in <paramref name="skipped"/> as <c>id.Prop</c>.
    /// </summary>
    public static ReplaceObjectsCommand Paste(MapProject p, CopiedProperties copy, IReadOnlyCollection<string> keys, IEnumerable<string> ids, List<string> skipped)
    {
        var cmd = new ReplaceObjectsCommand(keys.Count == 1 ? $"Paste {keys.First()}" : $"Paste {keys.Count} properties");
        foreach (Actor a in ids.Select(p.FindActor).OfType<Actor>())
        {
            HashSet<string> has = ClassDefaults.Catalog.AllProperties(a.Class).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool sameClass = string.Equals(a.Class, copy.Class, StringComparison.OrdinalIgnoreCase);
            Actor after = a.Clone();
            foreach (string key in keys)
            {
                if (!copy.Props.TryGet(key, out SvJsonNode? value))
                {
                    continue;
                }

                if (sameClass || has.Contains(key))
                {
                    after.Props.Set(key, value);
                }
                else
                {
                    skipped.Add($"{a.Id}.{key}");
                }
            }

            cmd.Actor(a.Clone(), after);
        }

        return cmd;
    }

    private static bool HasActorRef(SvJsonNode node) => node switch
    {
        SvJsonObject o when PropValues.ActorId(o) is not null => true,
        SvJsonObject o => o.Any(kv => HasActorRef(kv.Value)),
        SvJsonArray arr => arr.Any(HasActorRef),
        _ => false,
    };
}
