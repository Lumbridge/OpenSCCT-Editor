using System.Globalization;
using System.Security.Cryptography;
using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Core.Match;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Validation.Rules;

namespace OpenScct.Editor.Core.Interop.T3d;

/// <summary>A texture the original editor needs in the map's own package for an outfit: where it goes
/// (<c>&lt;Map&gt;.CharacterSkins.X</c>) and the PNG it comes from (as the outfits name it).</summary>
public sealed record OutfitTexture(string Path, string File);

/// <summary>
/// Writes the project's outfits and match rules back into the RE+ actors an .sdc import lifted them from
/// (<c>SCharacterOutfits</c> and its older names, <c>SMatchSizeRules</c> and its older names), so the original editor
/// gets the map as it is now. Unchanged data (the hash stored at import still matches) keeps the actor's properties as
/// imported, byte for byte; edited data is lowered the way RE+ stores it. What RE+ cannot hold is reported. Also makes
/// those actors for a map that has none (<see cref="Made"/>) and reads player counts back from one
/// (<see cref="ReadMatch"/>), as the converter does.
/// </summary>
public static class LiftedWriteBack
{
    /// <summary>RE+'s SMatchSizeRules caps (ObjectivePlayersModel.h): rows, players, team sizes and win missions.</summary>
    public const int Rows = 64, Players = 16, TeamSizes = 8, WinMissions = 16;

    /// <summary>The group RE+'s Tools &gt; Character Skins imports outfit textures into.</summary>
    public const string SkinsGroup = "CharacterSkins";

    /// <summary>The classes RE+'s current tools make (Character Skins, player-count rules).</summary>
    public const string OutfitsClass = "SCharacterOutfits", MatchClass = "SMatchSizeRules";

    private static readonly (bool Spy, string Slot, string Prop)[] s_slots =
        [(true, "body", "SpyBody"), (true, "head", "SpyHead"), (false, "body", "MercBody"), (false, "head", "MercHead")];

    private static readonly string[] s_outfitProps =
        ["SpyBody", "SpyHead", "MercBody", "MercHead", "SpyModel", "MercModel", "SpyGoggleOffset", "MercGoggleOffset"];

    private static readonly string[] s_matchProps = ["Subject", "Objective", "MinPlayers", "MaxPlayers", "OpenInRange", "WinMission", "WinTarget"];

    /// <summary>True when the data a lifted actor stands for changed since import (or the import kept no hash).</summary>
    public static bool Changed(MapProject p, string kind)
    {
        string now = kind == "outfits" ? BakedContent.OutfitsHash(p) : BakedContent.MatchHash(p);
        return (p.Extra[BakedContent.LiftedKey] as SvJsonObject)?[kind] is not SvJsonString h || h.Value != now;
    }

    /// <summary>The properties to write for a lifted actor: as imported when its data is unchanged, else rewritten.
    /// <paramref name="notes"/> gets what could not be written; <paramref name="projectFolder"/> is where the outfit
    /// files the project names are (their content names the textures).</summary>
    public static PropSet Props(MapProject p, Actor a, List<Finding>? notes = null, string? projectFolder = null)
    {
        string? kind = BakedContent.LiftedData(a);
        return kind is null || !Changed(p, kind) ? a.Props : Rewrite(p, a, kind, notes, projectFolder);
    }

    private static PropSet Rewrite(MapProject p, Actor a, string kind, List<Finding>? notes, string? projectFolder)
    {
        PropSet props = a.Props.Clone();
        if (kind == "outfits")
        {
            WriteOutfits(p, a, props, notes, projectFolder);
        }
        else
        {
            WriteMatch(p, a, props, notes);
        }

        return props;
    }

    /// <summary>
    /// RE+ actors for data the map has but no lifted actor carries (an authored map): an <c>SCharacterOutfits</c> for
    /// its outfits and an <c>SMatchSizeRules</c> for its player counts, as RE+'s tools would place them, marked lifted
    /// and made (the handoff file names them, so an import of the export drops them again while they are unchanged).
    /// </summary>
    public static List<Actor> Made(MapProject p, List<Finding>? notes = null, string? projectFolder = null)
    {
        var made = new List<Actor>();
        var used = new HashSet<string>(p.Actors.Select(a => a.Id).Concat(p.Brushes.Select(b => b.Id)), StringComparer.OrdinalIgnoreCase);
        foreach ((string kind, string cls, bool has) in new[]
                 {
                     ("outfits", OutfitsClass, !p.Outfits.IsEmpty),
                     ("match", MatchClass, p.Actors.Any(a => a.Match is not null && !BakedContent.IsLifted(a))),
                 })
        {
            if (!has || p.Actors.Any(a => BakedContent.LiftedData(a) == kind))
            {
                continue;
            }

            string id = cls + "0";
            for (int n = 1; used.Contains(id); n++)
            {
                id = cls + n.ToString(CultureInfo.InvariantCulture);
            }

            used.Add(id);
            var src = new SvJsonObject();
            src.Add("lifted", SvJsonNode.True);
            src.Add("made", SvJsonNode.True);
            var actor = new Actor(id, p.Id + "." + cls) { Source = InteropValues.Compact(src) };
            actor.Editor.Folder = Sdc.SdcOverlay.LiftedFolder;
            actor.Props = Rewrite(p, actor, kind, notes, projectFolder);
            made.Add(actor);
        }

        return made;
    }

    /// <summary>True for an actor <see cref="Made"/> made.</summary>
    public static bool IsMade(Actor a) =>
        a.Source is { Length: > 0 } s && s[0] == '{' && s.Contains("\"made\"", StringComparison.Ordinal)
        && SvJson.Parse(s) is SvJsonObject o && o["made"]?.Kind == SvJsonKind.True;

    /// <summary>The outfit textures the original editor must import into MyLevel for the outfit actors, with the PNG
    /// each comes from (as the outfits name it, relative to the project folder or full).</summary>
    public static List<OutfitTexture> Textures(MapProject p, string? projectFolder = null)
    {
        var list = new List<OutfitTexture>();
        foreach ((bool spy, string slot, _) in s_slots)
        {
            if (Slot(p.Outfits.For(spy), slot) is { } s && LocalPath(p, spy, slot, s, projectFolder) is { } path)
            {
                list.Add(new OutfitTexture(path, s.Texture));
            }
        }

        return list;
    }

    private static OutfitSlot? Slot(TeamOutfit? team, string slot) => slot == "body" ? team?.Body : team?.Head;

    /// <summary>Where a slot's texture lives for the original editor: the map's own object it came from, or a texture
    /// of the map's package named after its content (null for a stock source, which needs no import). Paths are in
    /// the map's package (<c>&lt;Map&gt;.X</c>), which the T3D writes as MyLevel and reads back the same.</summary>
    private static string? LocalPath(MapProject p, bool spy, string slot, OutfitSlot s, string? projectFolder)
    {
        if (s.Source is { Length: > 0 } source)
        {
            int dot = source.IndexOf('.', StringComparison.Ordinal);
            string package = dot > 0 ? source[..dot] : "";
            bool own = package.Equals(p.Id, StringComparison.OrdinalIgnoreCase) || package.Equals(T3dTypes.LevelPackage, StringComparison.OrdinalIgnoreCase)
                || (p.Source.File is { } file && package.Equals(Path.GetFileNameWithoutExtension(file), StringComparison.OrdinalIgnoreCase));
            return own ? p.Id + source[dot..] : null;
        }

        // A texture the editor set: named by team, slot and content (as RE+'s Character Skins names them), in RE+'s group.
        string png = Path.IsPathFullyQualified(s.Texture) || projectFolder is null ? s.Texture : Path.Combine(projectFolder, s.Texture);
        string hash = File.Exists(png)
            ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(png)).AsSpan(0, 4))
            : Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s.Texture)).AsSpan(0, 4));
        string name = (spy ? "Spy" : "Merc") + (slot == "body" ? "Body" : "Head") + "_" + hash;
        return $"{p.Id}.{SkinsGroup}.{name}";
    }

    private static void WriteOutfits(MapProject p, Actor a, PropSet props, List<Finding>? notes, string? projectFolder)
    {
        PropSet imported = a.Props;
        foreach (string key in s_outfitProps)
        {
            props.Remove(key);
        }

        foreach ((bool spy, string slot, string prop) in s_slots)
        {
            if (Slot(p.Outfits.For(spy), slot) is { } s)
            {
                props.Set(prop, PropValues.AssetRef(LocalPath(p, spy, slot, s, projectFolder) ?? s.Source!));
            }
        }

        foreach (bool spy in new[] { true, false })
        {
            string team = spy ? "Spy" : "Merc";
            if (p.Outfits.For(spy)?.Model is not { } model)
            {
                continue;
            }

            string? mesh = model.Stock;
            if (model.Glb is { } glb)
            {
                // A model the map brought: the original keeps its skeletal mesh only under the name it was imported with.
                string stem = Path.GetFileNameWithoutExtension(glb);
                mesh = PropValues.AssetPath(imported[team + "Model"]) is { } old && AssetPaths.Short(old).EndsWith("." + stem, StringComparison.OrdinalIgnoreCase)
                    ? old : null;
                if (mesh is null)
                {
                    notes?.Add(Finding.Warning("roundtrip.outfitModel",
                        $"the {team.ToLowerInvariant()} outfit's model {Path.GetFileName(glb)} is a glTF: the original game needs a skeletal mesh, which the export "
                        + $"cannot write; RE+'s outfit actor gets no {team}Model (import the model as a PSK in RE+ Tools > Character Skins)"));
                }
            }

            if (mesh is not null)
            {
                props.Set(team + "Model", PropValues.AssetRef(mesh));
                props.Set(team + "GoggleOffset", PropValues.Vector(new Vec3(model.GoggleOffset.X, model.GoggleOffset.Y, model.GoggleOffset.Z)));
            }
        }
    }

    private static void WriteMatch(MapProject p, Actor a, PropSet props, List<Finding>? notes)
    {
        foreach (string key in s_matchProps)
        {
            props.Remove(key);
        }

        // SObjectivePlayers (RE+'s first version) names its rows Objective and has no movers or win targets.
        bool v1 = a.ShortClass.Equals("SObjectivePlayers", StringComparison.OrdinalIgnoreCase);
        bool wins = a.ShortClass.Equals(MatchClass, StringComparison.OrdinalIgnoreCase);
        var subjects = new SvJsonObject();
        var mins = new SvJsonObject();
        var maxes = new SvJsonObject();
        var opens = new SvJsonObject();
        void Note(Actor s, string text) => notes?.Add(Finding.Warning("roundtrip.matchRows", $"{s.Id}: {text}", ObjectRef.Actor(s.Id)));
        int row = 0;
        foreach (Actor s in p.Actors.Where(x => x.Match is { HasRule: true } && !BakedContent.IsLifted(x)).OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
        {
            ActorMatch m = s.Match!;
            bool mover = MatchPlan.IsMover(s);
            int keys = (int)Gameplay.ClassDefaults.Number(s, "NumKeys", 2);
            if (!mover && !MatchPlan.IsObjective(s) && !MatchPlan.IsMission(s))
            {
                Note(s, "RE+'s rules gate only objectives, missions and movers, so the original game has it in every match");
                continue;
            }

            if (v1 && mover)
            {
                Note(s, $"{a.ShortClass} (RE+'s first player-count class) has no mover rows, so the original game leaves the mover as built in every match");
                continue;
            }

            if (row >= Rows)
            {
                Note(s, $"past RE+'s {Rows} rows, so the original game has it in every match");
                continue;
            }

            if (m.Players.Min > Players)
            {
                // RE+ counts at most 16 players: no row it holds means "never", and a missing row means "always".
                Note(s, $"in the match only from {m.Players.Min} players; the original game has at most {Players}, and RE+'s rules cannot leave it out of "
                    + "every match, so there it is in every match");
                continue;
            }

            if (mover ? !MatchRules.ExportableMover(m, keys) : m.Outside is { Kind: not HoldKind.Hidden } || m.Inside is { Kind: not HoldKind.Normal })
            {
                Note(s, mover
                    ? "RE+ closes a mover (key 0) outside its range and leaves it or opens it inside; its other states are written as the nearest of those"
                    : "RE+ removes an objective outside its range and keeps it inside; its other states are written as those");
            }

            string i = row.ToString(CultureInfo.InvariantCulture);
            subjects.Add(i, PropValues.ActorRef(s.Id));
            mins.Add(i, SvJsonNode.Integer(m.Players.Min));
            // No limit (0) stays 0; a maximum past RE+'s 16 is the same range in a game of 16 at most.
            maxes.Add(i, SvJsonNode.Integer(Math.Min(m.Players.Max, Players)));
            if (mover && m.Inside is { Kind: HoldKind.Key })
            {
                opens.Add(i, SvJsonNode.Integer(1));
            }

            row++;
        }

        var missions = new SvJsonObject();
        var targets = new SvJsonObject();
        int mission = 0;
        foreach (Actor m in p.Actors.Where(x => x.Match is { HasWin: true } && MatchPlan.IsMission(x)).OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!wins || mission >= WinMissions)
            {
                Note(m, wins ? $"past RE+'s {WinMissions} missions with win targets, so the original game keeps its own target"
                    : $"{a.ShortClass} has no win targets (RE+'s {MatchClass} has), so the original game keeps the mission's own target");
                continue;
            }

            missions.Add(mission.ToString(CultureInfo.InvariantCulture), PropValues.ActorRef(m.Id));
            int[] bySize = m.Match!.WinBySize();
            for (int size = 0; size < TeamSizes; size++)
            {
                if (bySize[size] > 0)
                {
                    targets.Add((mission * TeamSizes + size).ToString(CultureInfo.InvariantCulture), SvJsonNode.Integer(Math.Min(bySize[size], 255)));
                }
            }

            if (bySize.Skip(TeamSizes).Any(t => t > 0))
            {
                Note(m, $"RE+ has win targets for 1v1 to {TeamSizes}v{TeamSizes} only; its targets for 9v9 to 12v12 are left out");
            }

            mission++;
        }

        Set(props, v1 ? "Objective" : "Subject", subjects);
        Set(props, "MinPlayers", mins);
        Set(props, "MaxPlayers", maxes);
        Set(props, "OpenInRange", opens);
        Set(props, "WinMission", missions);
        Set(props, "WinTarget", targets);
    }

    /// <summary>
    /// The player counts an RE+ rules actor holds, as the converter reads them (svmbuild mapconv/mapclasses.py
    /// build_match): per actor its range, a mover's states (closed at key 0 outside, at its last key inside when it
    /// opens), and per mission its win targets for 1v1 to 8v8. Rows naming no actor of the map are skipped.
    /// </summary>
    public static (Dictionary<string, ActorMatch> Rules, Dictionary<string, int[]> Wins) ReadMatch(MapProject p, PropSet props)
    {
        var actors = new Dictionary<string, Actor>(StringComparer.OrdinalIgnoreCase);
        foreach (Actor x in p.Actors)
        {
            actors.TryAdd(x.Id, x);
        }

        var rules = new Dictionary<string, ActorMatch>(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, SvJsonNode> subjects = Elements(props["Subject"] ?? props["Objective"]);
        Dictionary<int, SvJsonNode> lows = Elements(props["MinPlayers"]), highs = Elements(props["MaxPlayers"]), opens = Elements(props["OpenInRange"]);
        foreach ((int i, SvJsonNode node) in subjects.OrderBy(kv => kv.Key))
        {
            if (PropValues.ActorId(node) is not { } id || !actors.TryGetValue(id, out Actor? s))
            {
                continue;
            }

            var range = new PlayerRange(Int(lows.GetValueOrDefault(i)), Int(highs.GetValueOrDefault(i)));
            Hold? outside = null, inside = null;
            if (MatchPlan.IsMover(s))
            {
                outside = Hold.AtKey(0);
                if (Int(opens.GetValueOrDefault(i)) != 0)
                {
                    int keys = (int)Gameplay.ClassDefaults.Number(s, "NumKeys", 2);
                    inside = Hold.AtKey(Math.Min(Math.Max(keys, 1), MatchJson.MostKeys) - 1);
                }
            }

            rules[s.Id] = new ActorMatch(range, outside, inside);
        }

        var wins = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, SvJsonNode> targets = Elements(props["WinTarget"]);
        foreach ((int j, SvJsonNode node) in Elements(props["WinMission"]).OrderBy(kv => kv.Key))
        {
            if (PropValues.ActorId(node) is not { } id || !actors.TryGetValue(id, out Actor? m))
            {
                continue;
            }

            var bySize = new int[MatchRuleSet.TeamSizes];
            for (int size = 0; size < TeamSizes; size++)
            {
                bySize[size] = Int(targets.GetValueOrDefault(j * TeamSizes + size));
            }

            if (bySize.Any(t => t > 0))
            {
                wins[m.Id] = bySize;
            }
        }

        return (rules, wins);
    }

    /// <summary>The match rules <see cref="WriteMatch"/> writes for <paramref name="rules"/> (an RE+ rules actor), read
    /// back: what RE+ got of the project's player counts.</summary>
    public static (Dictionary<string, ActorMatch> Rules, Dictionary<string, int[]> Wins) Written(MapProject p, Actor rules)
    {
        PropSet props = rules.Props.Clone();
        WriteMatch(p, rules, props, null);
        return ReadMatch(p, props);
    }

    private static Dictionary<int, SvJsonNode> Elements(SvJsonNode? node)
    {
        var list = new Dictionary<int, SvJsonNode>();
        if (node is SvJsonObject o && o[T3dTypes.ElementsKey] is SvJsonObject elements)
        {
            foreach ((string key, SvJsonNode value) in elements)
            {
                if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int i))
                {
                    list[i] = value;
                }
            }
        }
        else if (node is SvJsonArray a)
        {
            for (int i = 0; i < a.Count; i++)
            {
                list[i] = a[i];
            }
        }

        return list;
    }

    private static int Int(SvJsonNode? node) => node switch
    {
        SvJsonNumber n when n.TryGetInt32(out int v) => v,
        SvJsonNumber n => (int)Math.Round(n.FloatValue),
        _ => 0,
    };

    /// <summary>A static array as the project stores it: <c>{"$elements": {"i": v}}</c>; none when empty.</summary>
    private static void Set(PropSet props, string key, SvJsonObject elements)
    {
        if (elements.Count == 0)
        {
            return;
        }

        var o = new SvJsonObject();
        o.Add(T3dTypes.ElementsKey, elements);
        props.Set(key, o);
    }
}
