using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>One link of the event graph: an Event firing a Tag, or an actor reference property.</summary>
public sealed record GraphEdge(string From, string Via, string To, bool IsEvent);

/// <summary>
/// The map's wiring: <c>Event</c> → actors whose effective <c>Tag</c> matches (UE2 TriggerEvent), plus actor
/// references inside properties (<c>{"$actor": id}</c>). Events nothing listens to are reported as unheard.
/// </summary>
public static class EventGraph
{
    public static (List<GraphEdge> Edges, List<(string From, string Event)> Unheard) Build(MapProject p)
    {
        var byTag = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Actor a in p.Actors)
        {
            if (!byTag.TryGetValue(a.Tag, out List<string>? list))
            {
                byTag[a.Tag] = list = new List<string>();
            }

            list.Add(a.Id);
        }

        var edges = new List<GraphEdge>();
        var unheard = new List<(string, string)>();
        foreach (Actor a in p.Actors)
        {
            if (a.Props.GetString("Event") is { Length: > 0 } ev && ev != "None")
            {
                if (byTag.TryGetValue(ev, out List<string>? targets))
                {
                    edges.AddRange(targets.Where(t => !string.Equals(t, a.Id, StringComparison.OrdinalIgnoreCase)).Select(t => new GraphEdge(a.Id, ev, t, true)));
                }
                else
                {
                    unheard.Add((a.Id, ev));
                }
            }

            foreach (KeyValuePair<string, SvJsonNode> kv in a.Props)
            {
                if (kv.Key is "Level" or "Region" or "Base" or "Owner")
                {
                    continue; // engine bookkeeping, not design wiring
                }

                foreach (string to in ChangeApplier.References(kv.Value).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    edges.Add(new GraphEdge(a.Id, kv.Key, to, false));
                }
            }
        }

        return (edges, unheard);
    }

    /// <summary><c>{"q":"edges","id":"Button0","depth":1}</c>: the graph, or the part within depth links of id.</summary>
    public static SvJsonNode Query(MapProject p, SvJsonObject q)
    {
        (List<GraphEdge> edges, List<(string From, string Event)> unheard) = Build(p);
        if (ApiJson.GetString(q, "id") is { } id)
        {
            int depth = (int)ApiJson.Number(q, "depth", 1);
            var reach = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
            for (int i = 0; i < depth; i++)
            {
                foreach (GraphEdge e in edges.Where(e => reach.Contains(e.From) || reach.Contains(e.To)).ToList())
                {
                    reach.Add(e.From);
                    reach.Add(e.To);
                }
            }

            edges = edges.Where(e => reach.Contains(e.From) && reach.Contains(e.To)).ToList();
            unheard = unheard.Where(u => reach.Contains(u.From)).ToList();
        }

        int limit = (int)ApiJson.Number(q, "limit", 300);
        return ApiJson.Obj(
            ("cols", ApiJson.Strings(["from", "via", "to", "kind"])),
            ("rows", ApiJson.Arr(edges.Take(limit).Select(e => (SvJsonNode)ApiJson.Arr([
                ApiJson.Str(e.From), ApiJson.Str(e.Via), ApiJson.Str(e.To), ApiJson.Str(e.IsEvent ? "event" : "ref"),
            ])))),
            ("count", SvJsonNode.Integer(edges.Count)),
            ("unheard", ApiJson.Arr(unheard.Select(u => (SvJsonNode)ApiJson.Arr([ApiJson.Str(u.From), ApiJson.Str(u.Event)])))));
    }
}
