using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>What a link of the gameplay graph is.</summary>
public enum LinkKind
{
    /// <summary>An event property (<c>Event</c>, <c>Events</c>, <c>...Event</c>) fires every actor with that Tag.</summary>
    Fire,

    /// <summary>A sender filter (<c>AlertOnEventFrom</c>, <c>EventSender</c>...): the actor only accepts events from it.</summary>
    Accept,

    /// <summary>Any other actor reference in a property (an alarm's movers to lock, a mission's objectives...).</summary>
    Ref,
}

/// <summary>One link: <paramref name="From"/>'s property <paramref name="Via"/> reaches <paramref name="To"/>.
/// <paramref name="Event"/> is the event name of a fire link.</summary>
public sealed record GameplayLink(string From, string Via, string To, LinkKind Kind, string? Event = null);

/// <summary>An event that no actor's Tag receives.</summary>
public sealed record DanglingEvent(string From, string Via, string Event);

/// <summary>
/// The wiring of a map with OpenSCCT's SVMap 1.1 event-edge rules (GameplayDoc.ComputeEventEdges): every property at
/// any depth whose key is an event key fires the actors whose effective Tag matches (ignoring case); sender-filter
/// keys make accept links; other actor references are plain links. Render-only actors (lights, static meshes,
/// sounds) are not part of the gameplay map, so they neither send nor receive.
/// </summary>
public sealed class GameplayGraph
{
    private static readonly HashSet<string> s_bookkeeping = new(StringComparer.OrdinalIgnoreCase) { "Level", "Region", "Base", "Owner", "XLevel" };

    private readonly Dictionary<string, List<GameplayLink>> _out = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<GameplayLink>> _in = new(StringComparer.OrdinalIgnoreCase);

    private GameplayGraph(List<GameplayLink> links, List<DanglingEvent> dangling, Dictionary<string, List<string>> tags)
    {
        Links = links;
        Dangling = dangling;
        TagIndex = tags;
        foreach (GameplayLink l in links)
        {
            Add(_out, l.From, l);
            Add(_in, l.To, l);
        }
    }

    public IReadOnlyList<GameplayLink> Links { get; }

    public IReadOnlyList<DanglingEvent> Dangling { get; }

    /// <summary>Effective Tag to the gameplay actors carrying it, in level order.</summary>
    public IReadOnlyDictionary<string, List<string>> TagIndex { get; }

    public IReadOnlyList<GameplayLink> Out(string id) => _out.TryGetValue(id, out List<GameplayLink>? l) ? l : [];

    public IReadOnlyList<GameplayLink> In(string id) => _in.TryGetValue(id, out List<GameplayLink>? l) ? l : [];

    /// <summary>Ids with at least one link or dangling event.</summary>
    public IEnumerable<string> Nodes => _out.Keys.Concat(_in.Keys).Concat(Dangling.Select(d => d.From)).Distinct(StringComparer.OrdinalIgnoreCase);

    public static bool IsGameplay(Actor a) => !ActorRules.RenderClasses.Contains(a.Class) && !ActorRules.EditorClasses.Contains(a.Class);

    public static GameplayGraph Build(MapProject p)
    {
        List<Actor> actors = p.Actors.Where(IsGameplay).ToList();
        var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Actor a in actors)
        {
            Add(tags, a.Tag, a.Id);
        }

        var links = new List<GameplayLink>();
        var dangling = new List<DanglingEvent>();
        foreach (Actor a in actors)
        {
            foreach (KeyValuePair<string, SvJsonNode> kv in a.Props.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!s_bookkeeping.Contains(kv.Key))
                {
                    Walk(a, kv.Value, kv.Key, BareKey(kv.Key), tags, links, dangling);
                }
            }
        }

        return new GameplayGraph(links, dangling, tags);
    }

    /// <summary>The ids within <paramref name="depth"/> links of <paramref name="id"/>, either way.</summary>
    public HashSet<string> Around(string id, int depth)
    {
        var reach = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
        var frontier = new List<string> { id };
        for (int i = 0; i < depth && frontier.Count > 0; i++)
        {
            var next = new List<string>();
            foreach (string n in frontier)
            {
                foreach (GameplayLink l in Out(n).Concat(In(n)))
                {
                    foreach (string m in new[] { l.From, l.To })
                    {
                        if (reach.Add(m))
                        {
                            next.Add(m);
                        }
                    }
                }
            }

            frontier = next;
        }

        return reach;
    }

    /// <summary>Event rings: groups of actors that fire each other round in a loop (strongly connected over fire links,
    /// self-firing actors included).</summary>
    public List<List<string>> Rings()
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var low = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rings = new List<List<string>>();
        int counter = 0;
        IEnumerable<string> Next(string n) => Out(n).Where(l => l.Kind == LinkKind.Fire).Select(l => l.To);

        void Visit(string v)
        {
            // Iterative Tarjan: (node, its successors, position).
            var work = new Stack<(string Node, List<string> Succ, int Pos)>();
            index[v] = low[v] = counter++;
            stack.Push(v);
            onStack.Add(v);
            work.Push((v, Next(v).ToList(), 0));
            while (work.Count > 0)
            {
                (string node, List<string> succ, int pos) = work.Pop();
                if (pos < succ.Count)
                {
                    work.Push((node, succ, pos + 1));
                    string w = succ[pos];
                    if (!index.ContainsKey(w))
                    {
                        index[w] = low[w] = counter++;
                        stack.Push(w);
                        onStack.Add(w);
                        work.Push((w, Next(w).ToList(), 0));
                    }
                    else if (onStack.Contains(w))
                    {
                        low[node] = Math.Min(low[node], index[w]);
                    }

                    continue;
                }

                if (work.Count > 0)
                {
                    string parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }

                if (low[node] == index[node])
                {
                    var ring = new List<string>();
                    string m;
                    do
                    {
                        m = stack.Pop();
                        onStack.Remove(m);
                        ring.Add(m);
                    }
                    while (!string.Equals(m, node, StringComparison.OrdinalIgnoreCase));

                    if (ring.Count > 1 || Next(node).Contains(node, StringComparer.OrdinalIgnoreCase))
                    {
                        ring.Sort(StringComparer.OrdinalIgnoreCase);
                        rings.Add(ring);
                    }
                }
            }
        }

        foreach (string n in _out.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (!index.ContainsKey(n))
            {
                Visit(n);
            }
        }

        return rings;
    }

    private static void Walk(Actor owner, SvJsonNode value, string path, string key, Dictionary<string, List<string>> tags,
        List<GameplayLink> links, List<DanglingEvent> dangling)
    {
        switch (value)
        {
            case SvJsonObject o when PropValues.ActorId(o) is { } target:
                LinkKind kind = GameplayDoc.AcceptKeys.Contains(key) ? LinkKind.Accept : LinkKind.Ref;
                if (!string.Equals(target, owner.Id, StringComparison.OrdinalIgnoreCase) || kind == LinkKind.Accept)
                {
                    links.Add(new GameplayLink(owner.Id, path, target, kind));
                }

                return;
            case SvJsonObject o:
                foreach (KeyValuePair<string, SvJsonNode> m in o.OrderBy(m => m.Key, StringComparer.Ordinal))
                {
                    Walk(owner, m.Value, path + "." + m.Key, BareKey(m.Key), tags, links, dangling);
                }

                return;
            case SvJsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    Walk(owner, arr[i], $"{path}[{i}]", key, tags, links, dangling);
                }

                return;
            case SvJsonString s when GameplayDoc.IsEventKey(key) && IsName(s.Value):
                if (tags.TryGetValue(s.Value, out List<string>? receivers))
                {
                    links.AddRange(receivers.Select(r => new GameplayLink(owner.Id, path, r, LinkKind.Fire, s.Value)));
                }
                else
                {
                    dangling.Add(new DanglingEvent(owner.Id, path, s.Value));
                }

                return;
        }
    }

    /// <summary>A key without a static array index (<c>Events[2]</c> → <c>Events</c>).</summary>
    private static string BareKey(string key) => key.IndexOf('[') is > 0 and int i ? key[..i] : key;

    private static bool IsName(string s) => s.Length > 0 && !s.Equals("None", StringComparison.OrdinalIgnoreCase);

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T item)
    {
        if (!map.TryGetValue(key, out List<T>? list))
        {
            map[key] = list = new List<T>();
        }

        list.Add(item);
    }
}
