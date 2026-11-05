using OpenScct.Core.Match;

namespace OpenScct.Editor.Core.Model;

/// <summary>
/// An actor's match-size settings (compiled to SVMap 1.2 <c>sim/match.json</c>): the player counts it is in the match
/// for, what it does outside and inside that range (null for the defaults: a mover closes at key 0 and anything else
/// is left out; inside it works as built), and for a mission its win targets by team size.
/// </summary>
/// <param name="Players">The range; <see cref="PlayerRange.Any"/> keeps the actor in every match.</param>
/// <param name="Outside">The state outside the range, null for the default.</param>
/// <param name="Inside">The state inside the range, null for normal.</param>
/// <param name="Win">A mission's objectives to win for 1v1 to 12v12 (0 keeps its own number), or null.</param>
public sealed record ActorMatch(PlayerRange Players, Hold? Outside = null, Hold? Inside = null, IReadOnlyList<int>? Win = null)
{
    /// <summary>True when the actor has a player-count rule (a range or a stored state).</summary>
    public bool HasRule => !Players.IsAny || Outside is not null || Inside is not null;

    /// <summary>True when a win target is set for some team size.</summary>
    public bool HasWin => Win is { } w && w.Any(t => t > 0);

    public bool IsEmpty => !HasRule && !HasWin;

    /// <summary>The rule as <c>sim/match.json</c> stores it.</summary>
    public MatchRule Rule(string actor) => new(actor, Players, Outside, Inside);

    /// <summary>The win targets padded to the 12 team sizes.</summary>
    public int[] WinBySize()
    {
        var sizes = new int[MatchRuleSet.TeamSizes];
        for (int i = 0; i < sizes.Length && Win is not null && i < Win.Count; i++)
        {
            sizes[i] = Win[i];
        }

        return sizes;
    }

    /// <summary>Null when nothing is set, so an actor without settings stores none.</summary>
    public ActorMatch? OrNull() => IsEmpty ? null : this;
}
