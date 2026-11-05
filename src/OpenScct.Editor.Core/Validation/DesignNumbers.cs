using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Validation;

/// <summary>
/// The body and clearance numbers Map Check and the blockout pieces use, from a project's movement limits
/// (<see cref="Workspace.Movement"/>). The clearances keep the design margins over the body, so with the default
/// limits every number equals its <see cref="Limits"/> constant, and a taller or wider body moves them all together.
/// </summary>
public sealed record DesignNumbers
{
    /// <summary>Margins of the design clearances over the decoded body (StandClearance 192 = 175 + 17...).</summary>
    public const double StandMargin = Limits.StandClearance - Limits.StandHeight;

    public const double SpyCrouchMargin = Limits.SpyCrouchGap - Limits.SpyCrouchHeight;

    public const double MercCrouchMargin = Limits.MercCrouchGap - Limits.MercCrouchHeight;

    public const double DoorMargin = Limits.NarrowestDoor - Limits.BodyRadius * 2;

    /// <summary>The design vent and crawlway heights over the spy crouch gap.</summary>
    public const double VentOverGap = Limits.VentHeight - Limits.SpyCrouchGap;

    public const double CrawlwayOverGap = Limits.CrawlwayHeight - Limits.SpyCrouchGap;

    /// <summary>The numbers of the decoded SCCT body (the defaults of a new map).</summary>
    public static DesignNumbers Default { get; } = From(new MovementLimits());

    public double BodyRadius { get; init; }

    public double StandHeight { get; init; }

    public double SpyCrouchHeight { get; init; }

    public double MercCrouchHeight { get; init; }

    /// <summary>Highest step players walk up.</summary>
    public double MaxStepHeight { get; init; }

    /// <summary>Walkable floors have a normal Z of at least this.</summary>
    public double WalkableNormalZ { get; init; }

    public double StandClearance { get; init; }

    public double SpyCrouchGap { get; init; }

    public double MercCrouchGap { get; init; }

    public double NarrowestDoor { get; init; }

    public double VentHeight { get; init; }

    public double CrawlwayHeight { get; init; }

    public double StandHalfHeight => StandHeight / 2;

    /// <summary>The steepest walkable slope in degrees.</summary>
    public double WalkableSlopeDegrees => Math.Acos(WalkableNormalZ) * 180 / Math.PI;

    public static DesignNumbers From(MovementLimits m)
    {
        double spyGap = m.SpyCrouchHeight + SpyCrouchMargin;
        return new DesignNumbers
        {
            BodyRadius = m.Radius,
            StandHeight = m.StandHeight,
            SpyCrouchHeight = m.SpyCrouchHeight,
            MercCrouchHeight = m.MercCrouchHeight,
            MaxStepHeight = m.StepHeight,
            // A hair of slack so a ramp at exactly the limit counts as walkable.
            WalkableNormalZ = Math.Round(Math.Cos(m.WalkableSlopeDegrees * Math.PI / 180), 4),
            StandClearance = m.StandHeight + StandMargin,
            SpyCrouchGap = spyGap,
            MercCrouchGap = m.MercCrouchHeight + MercCrouchMargin,
            NarrowestDoor = m.Radius * 2 + DoorMargin,
            VentHeight = spyGap + VentOverGap,
            CrawlwayHeight = spyGap + CrawlwayOverGap,
        };
    }

    public static DesignNumbers Of(MapProject project) => From(project.Workspace.Movement);
}
