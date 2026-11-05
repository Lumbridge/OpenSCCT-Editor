using OpenScct.Core.Engine;
using OpenScct.Core.Uc;
using OpenScct.Game.AI;
using OpenScct.Game.SBase;

namespace OpenScct.Editor.Core.Analysis;

/// <summary>A side of the match: spies (team 0) or mercs (team 1).</summary>
public enum TeamSide
{
    Spy,
    Merc,
}

/// <summary>Which drops a reach map takes.</summary>
public enum DropRule
{
    /// <summary>Any drop a pawn at full health survives: up to just under Fell_MaxHeightForDmg (spy 1500, merc 1000).</summary>
    Survivable,

    /// <summary>Only drops that cost no health: up to Fell_MinHeightForDmg (spy 600, merc 450).</summary>
    Unhurt,

    /// <summary>The drops OpenSCCT's bots plan (spy 700, merc 400): what a bot match will use.</summary>
    Bot,
}

/// <summary>
/// What a pawn of one side may use in a reach map: OpenSCCT's bot <see cref="NavProfile"/> for that side (body, crouch
/// height, step, slope, GE links: ladders for both sides, ledges, pipes, zip lines, hand over hand and fences for spies)
/// with the drop limit of <see cref="Drops"/>. The fall heights are the pawn classes' own defaults (OpenSCCT decodes
/// spy 600 to 1500 and merc 450 to 1000; damage grows linearly between them and is the whole health at the top).
/// </summary>
public sealed record ReachProfile(TeamSide Side, DropRule Drops = DropRule.Survivable)
{
    private static readonly Lazy<SBasePawn> s_spy = new(() => new SPawnAttaque());
    private static readonly Lazy<SBasePawn> s_merc = new(() => new SPAWNDEFENSE());

    private SBasePawn Pawn => Side == TeamSide.Spy ? s_spy.Value : s_merc.Value;

    /// <summary>The side's pawn class, for sensors that detect a class (SPresenceTrigger.DetectedClass).</summary>
    public UcClassInfo PawnClass => Pawn.GetUcClass();

    /// <summary>The side's pawn type, for the Use menu's UsableBy.</summary>
    public PawnType PawnType => Side == TeamSide.Spy ? PawnType.SPAWNOFFENSE : PawnType.SPAWNDEFENSE;

    /// <summary>A crouched pawn's half height.</summary>
    public float CrouchHeight => BotProfile.CrouchHeight;

    /// <summary>The fall heights of the side: damage starts above Min and kills at Max.</summary>
    public (float Min, float Max) FallHeights => (Pawn.Fell_MinHeightForDmg, Pawn.Fell_MaxHeightForDmg);

    /// <summary>The longest drop this profile takes.</summary>
    public float MaxDrop => Drops switch
    {
        DropRule.Unhurt => FallHeights.Min,
        DropRule.Bot => BotProfile.MaxDrop,
        _ => FallHeights.Max - 1f,
    };

    public NavProfile Nav => BotProfile with { MaxDrop = MaxDrop, SafeDrop = FallHeights.Min };

    /// <summary>Health lost (0 to 100) falling <paramref name="height"/>, by the game's fall damage rule.</summary>
    public float Damage(float height)
    {
        (float min, float max) = FallHeights;
        return height <= min ? 0f : MathF.Min(100f, (height - min) / (max - min) * 100f);
    }

    public string Key => $"{Side}-{Drops}".ToLowerInvariant();

    private NavProfile BotProfile => Side == TeamSide.Spy ? NavProfile.Spy : NavProfile.Merc;

    /// <summary>The team number of a side's player starts.</summary>
    public static byte Team(TeamSide side) => side == TeamSide.Spy ? (byte)0 : (byte)1;

    public static TeamSide ParseSide(string text) => text.ToLowerInvariant() switch
    {
        "spy" or "spies" or "0" => TeamSide.Spy,
        "merc" or "mercs" or "1" => TeamSide.Merc,
        _ => throw new ArgumentException($"side is spy or merc, not '{text}'"),
    };

    public static DropRule ParseDrops(string text) => text.ToLowerInvariant() switch
    {
        "survivable" => DropRule.Survivable,
        "unhurt" or "safe" => DropRule.Unhurt,
        "bot" or "bots" => DropRule.Bot,
        _ => throw new ArgumentException($"drops is survivable, unhurt or bot, not '{text}'"),
    };
}
