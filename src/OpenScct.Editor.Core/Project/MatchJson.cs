using OpenScct.Content.Json;
using OpenScct.Core.Match;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Project;

/// <summary>
/// The JSON of an actor's match-size settings (<c>"match"</c> in <c>world/actors.json</c>), in the forms of SVMap 1.2
/// <c>sim/match.json</c>: <c>{"players": [min, max], "outside": "hidden" | {"key": n}, "inside": "normal" | "hidden" |
/// {"key": n}, "win": [12 counts]}</c>, members at their default left out.
/// </summary>
public static class MatchJson
{
    /// <summary>The keys a state may name: a mover has at most 8 (Engine.Mover KeyPos[8]).</summary>
    public const int MostKeys = OpenScct.Core.Engine.MoverPath.MaxKeys;

    public static SvJsonObject ToJson(ActorMatch m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var o = new SvJsonObject();
        if (!m.Players.IsAny)
        {
            o.Add("players", new SvJsonArray([SvJsonNode.Integer(m.Players.Min), SvJsonNode.Integer(m.Players.Max)]));
        }

        if (m.Outside is { } outside)
        {
            o.Add("outside", HoldToJson(outside));
        }

        if (m.Inside is { } inside)
        {
            o.Add("inside", HoldToJson(inside));
        }

        if (m.HasWin)
        {
            o.Add("win", new SvJsonArray(m.WinBySize().Select(t => (SvJsonNode)SvJsonNode.Integer(t))));
        }

        return o;
    }

    /// <summary>
    /// Reads the settings as a project stores them; a value of the wrong shape throws <see cref="FormatException"/>
    /// naming the member. Values of the right shape that cannot work (a minimum above the maximum, a key past the
    /// last) load as they are: an edit in between can store them, and Map Check reports them (<c>match.range</c>).
    /// </summary>
    public static ActorMatch? FromJson(SvJsonNode? node)
    {
        if (node is not SvJsonObject o)
        {
            return null;
        }

        PlayerRange players = o["players"] is { } p ? ReadRange(p, strict: false) : PlayerRange.Any;
        Hold? outside = o["outside"] is { } out1 ? ReadHold(out1, "outside", strict: false) : null;
        Hold? inside = o["inside"] is { } in1 ? ReadHold(in1, "inside", strict: false) : null;
        if (outside is { Kind: HoldKind.Normal })
        {
            throw new FormatException("outside: outside its range an actor cannot work as normal");
        }

        int[]? win = o["win"] is { } w ? ReadWin(w) : null;
        return new ActorMatch(players, outside, inside, win).OrNull();
    }

    /// <summary>Reads <c>[min, max]</c>; <paramref name="strict"/> also rejects a range that cannot work.</summary>
    public static PlayerRange ReadRange(SvJsonNode node, bool strict = true)
    {
        if (node is not SvJsonArray { Count: 2 } a || !Int(a[0], out int min) || !Int(a[1], out int max))
        {
            throw new FormatException("players: expected [min, max] (0 for no limit)");
        }

        var range = new PlayerRange(min, max);
        return strict && range.Problem() is { } problem ? throw new FormatException($"players: {problem}") : range;
    }

    /// <summary>
    /// Reads <c>"normal"</c>, <c>"hidden"</c>, <c>{"key": n}</c> or a bare key number; <paramref name="strict"/> also
    /// rejects a key no mover has.
    /// </summary>
    public static Hold ReadHold(SvJsonNode node, string field, bool strict = true) => node switch
    {
        SvJsonString { Value: "normal" } => Hold.Normal,
        SvJsonString { Value: "hidden" } => Hold.Hidden,
        SvJsonObject o when Int(o["key"], out int key) => Key(key, field, strict),
        SvJsonNumber when Int(node, out int key) => Key(key, field, strict),
        _ => throw new FormatException($"{field}: expected \"normal\", \"hidden\" or {{\"key\": n}}"),
    };

    public static SvJsonNode HoldToJson(Hold hold) => hold.Kind switch
    {
        HoldKind.Normal => SvJsonNode.String("normal"),
        HoldKind.Hidden => SvJsonNode.String("hidden"),
        _ => new SvJsonObject { { "key", SvJsonNode.Integer(hold.Key) } },
    };

    /// <summary>Reads win targets: up to 12 counts (1v1 first), 0 to 255 each.</summary>
    public static int[] ReadWin(SvJsonNode node)
    {
        if (node is not SvJsonArray a || a.Count > MatchRuleSet.TeamSizes)
        {
            throw new FormatException($"win: expected up to {MatchRuleSet.TeamSizes} counts, 1v1 first");
        }

        var win = new int[MatchRuleSet.TeamSizes];
        for (int i = 0; i < a.Count; i++)
        {
            if (!Int(a[i], out win[i]) || win[i] is < 0 or > 255)
            {
                throw new FormatException($"win[{i}]: a count from 0 (the mission's own) to 255");
            }
        }

        return win;
    }

    private static Hold Key(int key, string field, bool strict) =>
        key >= 0 && (!strict || key < MostKeys) ? Hold.AtKey(key) : throw new FormatException($"{field}: mover keys run 0 to {MostKeys - 1}");

    private static bool Int(SvJsonNode? node, out int value)
    {
        value = 0;
        return node is SvJsonNumber n && n.TryGetInt32(out value);
    }
}
