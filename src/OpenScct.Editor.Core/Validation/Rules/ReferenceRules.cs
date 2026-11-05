using System.Text.RegularExpressions;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Validation.Rules;

/// <summary>Actor references and the Event/Tag wiring.</summary>
internal sealed class ReferenceRules : MapRule
{
    public const string ActorMissing = "ref.actor-missing";
    public const string EventUnheard = "ref.event-unheard";
    public const string TagShared = "ref.tag-shared";

    /// <summary>Engine bookkeeping references (the level, the actor's zone) that point at objects the project keeps
    /// outside its actor list.</summary>
    private static readonly HashSet<string> s_engineProps = new(StringComparer.OrdinalIgnoreCase) { "Level", "Region", "XLevel" };

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(ActorMissing, Severity.Error, "A property refers to an actor id that is not in the map"),
        new(EventUnheard, Severity.Warning, "An Event that no actor's Tag receives (it fires into nothing)"),
        new(TagShared, Severity.Info, "One explicit Tag set on actors of different classes (an Event reaches all of them)"),
    ];

    public override void Run(CheckContext c)
    {
        var ids = new HashSet<string>(c.Project.Actors.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        var tags = new HashSet<string>(c.Project.Actors.Select(a => a.Tag), StringComparer.OrdinalIgnoreCase);
        foreach (Actor a in c.Project.Actors)
        {
            foreach ((string name, SvJsonNode value) in a.Props)
            {
                if (s_engineProps.Contains(name))
                {
                    continue;
                }

                foreach (string target in ActorIds(value))
                {
                    if (!ids.Contains(target) && !target.StartsWith("LevelInfo", StringComparison.OrdinalIgnoreCase))
                    {
                        c.Report(Severity.Error, ActorMissing, $"{a.Id}.{name} refers to missing actor {target}", a.Location, ObjectRef.Actor(a.Id));
                    }
                }
            }

            string ev = c.Name(a, "Event");
            if (ev.Length > 0 && !tags.Contains(ev))
            {
                c.Report(Severity.Warning, EventUnheard, $"{a.Id} fires Event '{ev}' but no actor has that Tag", a.Location, ObjectRef.Actor(a.Id));
            }
        }

        foreach (IGrouping<string, Actor> g in c.Project.Actors
            .Where(a => a.Props.GetString("Tag") is { Length: > 0 } t && !t.Equals("None", StringComparison.OrdinalIgnoreCase))
            .GroupBy(a => a.Tag, StringComparer.OrdinalIgnoreCase))
        {
            List<string> classes = g.Select(a => a.ShortClass).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
            if (classes.Count > 1 && !g.Any(a => string.Equals(a.ShortClass, g.Key, StringComparison.OrdinalIgnoreCase)))
            {
                c.Report(Severity.Info, TagShared, $"Tag '{g.Key}' is on {g.Count()} actors of {classes.Count} classes ({string.Join(", ", classes.Take(4))})",
                    null, g.Select(a => ObjectRef.Actor(a.Id)).ToArray());
            }
        }
    }

    /// <summary>Every <c>{"$actor": id}</c> inside a value (structs and arrays included).</summary>
    internal static IEnumerable<string> ActorIds(SvJsonNode node)
    {
        switch (node)
        {
            case SvJsonObject o when o["$actor"] is SvJsonString s:
                yield return s.Value;
                break;
            case SvJsonObject o:
                foreach (KeyValuePair<string, SvJsonNode> kv in o)
                {
                    foreach (string id in ActorIds(kv.Value))
                    {
                        yield return id;
                    }
                }

                break;
            case SvJsonArray arr:
                foreach (SvJsonNode item in arr)
                {
                    foreach (string id in ActorIds(item))
                    {
                        yield return id;
                    }
                }

                break;
        }
    }
}

/// <summary>Object ids: unique, and valid UE2 names.</summary>
internal sealed partial class IdRules : MapRule
{
    public const string Duplicate = "id.duplicate";
    public const string Invalid = "id.invalid";

    public override IReadOnlyList<RuleInfo> Rules { get; } =
    [
        new(Duplicate, Severity.Error, "Two objects share an id (ids are unique ignoring case)"),
        new(Invalid, Severity.Error, "An id that is not a UE2 name (letters, digits and _, not starting with a digit, at most 63)"),
    ];

    public override void Run(CheckContext c)
    {
        foreach (IGrouping<string, ObjectRef> g in c.Project.AllRefs().GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (g.Count() > 1)
            {
                c.Report(Severity.Error, Duplicate, $"{g.Count()} objects use the id '{g.Key}'", null, g.ToArray());
            }

            if (!NameRegex().IsMatch(g.Key))
            {
                c.Report(Severity.Error, Invalid, $"'{g.Key}' is not a valid UE2 name", null, g.First());
            }
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    internal static partial Regex NameRegex();
}
