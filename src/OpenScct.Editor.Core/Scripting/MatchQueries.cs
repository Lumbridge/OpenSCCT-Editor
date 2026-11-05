using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Core.Match;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Scripting;

/// <summary>The <c>match</c> and <c>outfits</c> queries: player-count rules, their layout at a count, and outfits.</summary>
public static class MatchQueries
{
    /// <summary>
    /// match: the actors with settings (<c>rows</c>), the counts where the layout changes, problems and warnings, and
    /// with <c>players</c> (default: the count a playtest uses) the layout of a match that size, in <c>mode</c> (a game
    /// mode name; default every mode): missions with their objectives and targets, objectives left out, other actors
    /// hidden and where gated movers are held; <c>absent</c>, every actor that match leaves out of play (what the Match
    /// panel greys out in the views: the layout's and, with a mode, the other modes' objects); and <c>sizes</c>, what
    /// each team size plays in that mode at full teams (sizes that play alike joined).
    /// </summary>
    public static SvJsonNode Match(MapProject p, SvJsonObject q)
    {
        int players = q["players"] is { } n ? (int)ApiJson.RequireNumber(n, "players") : p.Workspace.Play.MatchPlayers;
        if (players is < 1 or > MatchRuleSet.MostPlayers)
        {
            throw new ApiException($"players is 1 to {MatchRuleSet.MostPlayers} (everyone in the match, bots included)", "players");
        }

        var rows = p.Actors.Where(a => a.Match is { IsEmpty: false }).Select(a => (SvJsonNode)ApiJson.Obj(
            ("id", ApiJson.Str(a.Id)),
            ("class", ApiJson.Str(a.Class)),
            ("match", MatchJson.ToJson(a.Match!))));
        string? mode = ApiJson.GetString(q, "mode");
        List<string> modes = MatchPlan.Modes(p);
        if (mode is not null && !modes.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            throw new ApiException($"the map has no top mission in {mode} (it has {(modes.Count == 0 ? "none" : string.Join(", ", modes))})", "mode");
        }

        MatchPreview preview = MatchPlan.Preview(p, players, mode);
        List<string> problems = MatchPlan.Problems(p);
        List<string> warnings = MatchPlan.TargetWarnings(p);
        return ApiJson.Obj(
            ("actors", ApiJson.Arr(rows)),
            ("layouts", ApiJson.Arr(MatchPlan.Layouts(p).Select(c => (SvJsonNode)SvJsonNode.Integer(c)))),
            ("modes", ApiJson.Strings(modes)),
            ("preview", Preview(preview)),
            ("absent", ApiJson.Strings(MatchPlan.Absent(p, preview).Order(StringComparer.OrdinalIgnoreCase))),
            ("sizes", ApiJson.Arr(MatchPlan.BySize(p, mode).Select(r => (SvJsonNode)ApiJson.Obj(
                ("from", SvJsonNode.Integer(r.From)),
                ("to", SvJsonNode.Integer(r.To)),
                ("missions", ApiJson.Arr(r.Preview.Missions.Select(m => (SvJsonNode)ApiJson.Obj(
                    ("id", ApiJson.Str(m.Id)),
                    ("objectives", SvJsonNode.Integer(m.Objectives)),
                    ("needed", SvJsonNode.Integer(m.Needed)))))),
                ("leftOut", SvJsonNode.Integer(r.Preview.LeftOut.Count)),
                ("hidden", SvJsonNode.Integer(r.Preview.Hidden.Count)),
                ("held", SvJsonNode.Integer(r.Preview.Movers.Count(m => m.Value.Kind == HoldKind.Key))))))),
            ("problems", problems.Count == 0 ? null : ApiJson.Strings(problems)),
            ("warnings", warnings.Count == 0 ? null : ApiJson.Strings(warnings)));
    }

    public static SvJsonObject Preview(MatchPreview preview) => ApiJson.Obj(
        ("players", SvJsonNode.Integer(preview.Players)),
        ("teamSize", SvJsonNode.Integer(preview.TeamSize)),
        ("mode", preview.Mode is { } mode ? ApiJson.Str(mode) : null),
        ("missions", ApiJson.Arr(preview.Missions.Select(m => (SvJsonNode)ApiJson.Obj(
            ("id", ApiJson.Str(m.Id)),
            ("mode", ApiJson.Str(m.Mode)),
            ("objectives", SvJsonNode.Integer(m.Objectives)),
            ("needed", SvJsonNode.Integer(m.Needed)),
            ("random", m.Random > 0 ? SvJsonNode.Integer(m.Random) : null))))),
        ("leftOut", preview.LeftOut.Count == 0 ? null : ApiJson.Strings(preview.LeftOut)),
        ("hidden", preview.Hidden.Count == 0 ? null : ApiJson.Strings(preview.Hidden)),
        ("movers", preview.Movers.Count == 0 ? null : ApiJson.Obj(preview.Movers.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
            .Select(m => (m.Key, (SvJsonNode?)MatchJson.HoldToJson(m.Value))).ToArray())));

    /// <summary>outfits: the map's outfits as stored, with the model check and file problems.</summary>
    public static SvJsonNode Outfits(MapProject p, string? folder)
    {
        List<OutfitProblem> problems = ProjectOutfits.Problems(p.Outfits, folder, EditorServices.ContentRoot);
        SvJsonObject o = OutfitsJson.ToJson(p.Outfits);
        o.Add("folder", ApiJson.Str(ProjectStore.OutfitsFolder));
        if (problems.Count > 0)
        {
            o.Add("problems", ApiJson.Arr(problems.Select(x => (SvJsonNode)ApiJson.Obj(
                ("sev", ApiJson.Str(x.Error ? "error" : "warning")),
                ("code", ApiJson.Str(x.Code)),
                ("message", ApiJson.Str(x.Message))))));
        }

        return o;
    }
}
