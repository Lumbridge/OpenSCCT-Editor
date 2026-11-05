namespace OpenScct.Editor.Core.Validation;

/// <summary>
/// Every engine, body and design number the editor's builders and checks use, in one place (Unreal units: 100 =
/// 1 m; rotators 65536 per turn). Body numbers are decoded from the game (spec/movement in OpenSCCT); design numbers
/// come from design guidelines and RE+ Map Design; original-engine caps are what the stock game survives.
/// </summary>
public static class Limits
{
    // ---- body (spec/movement/walking.md, SBasePawn / SPAWNDEFENSE defaults) -------------------------------------

    /// <summary>Collision radius of both teams, standing and crouched.</summary>
    public const double BodyRadius = 35;

    /// <summary>Standing collision half height (both teams).</summary>
    public const double StandHalfHeight = 87.5;

    /// <summary>Crouched collision half height of a spy.</summary>
    public const double SpyCrouchHalfHeight = 50;

    /// <summary>Crouched collision half height of a merc.</summary>
    public const double MercCrouchHalfHeight = 68;

    public const double StandHeight = StandHalfHeight * 2;

    public const double SpyCrouchHeight = SpyCrouchHalfHeight * 2;

    public const double MercCrouchHeight = MercCrouchHalfHeight * 2;

    /// <summary>Highest step the walking physics climbs (decoded 34; 32 walked and 36 refused in tests).</summary>
    public const double MaxStepHeight = 34;

    /// <summary>Step height designs should use (design guideline: step 32).</summary>
    public const double DesignStepHeight = 32;

    /// <summary>Walkable floors have a normal Z of at least this (45.6 degrees).</summary>
    public const double WalkableNormalZ = 0.7;

    /// <summary>Jump apex over the take-off floor: spy 124 (JumpZ 610), merc 81.7 (JumpZ 495).</summary>
    public const double SpyJumpPeak = 124;

    public const double MercJumpPeak = 81.7;

    // ---- design (design guidelines, RE+ Map Design) -----------------------------------------------------------------

    /// <summary>Clear height a standing player of either team needs, with margin (design "stand height").</summary>
    public const double StandClearance = 192;

    /// <summary>Narrowest doorway both teams pass.</summary>
    public const double NarrowestDoor = 72;

    /// <summary>Gap a crouched spy passes (design guideline); mercs need <see cref="MercCrouchGap"/>.</summary>
    public const double SpyCrouchGap = 104;

    public const double MercCrouchGap = 144;

    /// <summary>Design vent heights: a regular (crouch) vent and a slow-walk crawlway.</summary>
    public const double VentHeight = 125;

    public const double CrawlwayHeight = 105;

    /// <summary>Highest top a spy / merc jumps onto.</summary>
    public const double SpyJumpOnto = 112;

    public const double MercJumpOnto = 80;

    /// <summary>Running jump across a gap.</summary>
    public const double SpyJumpAcross = 320;

    public const double MercJumpAcross = 256;

    /// <summary>Drops taken without damage.</summary>
    public const double SpyUnhurtDrop = 600;

    public const double MercUnhurtDrop = 450;

    /// <summary>Ledge grab: tops this far over the floor are reachable.</summary>
    public const double LedgeReachMin = 208;

    public const double LedgeReachMax = 320;

    /// <summary>A ledge grab should be at least this long.</summary>
    public const double LedgeMinLength = 80;

    /// <summary>Hang side of a ledge: clear this far out and this far down.</summary>
    public const double LedgeClearOut = 62;

    public const double LedgeClearDown = 140;

    /// <summary>Ladder and pipe tops sit about this far over their floor, or climbers fall through.</summary>
    public const double LadderTopOverFloor = 40;

    /// <summary>Zip line: feet hang this far under the line.</summary>
    public const double ZipLineHang = 202;

    /// <summary>Default storey height (RE+ floor step).</summary>
    public const double StoreyHeight = 272;

    /// <summary>Use spot distance in front of a terminal.</summary>
    public const double UseSpotDistance = 64;

    /// <summary>Design flow rules (RE+ Check Design): a spy start this close to an objective, team starts this close
    /// to each other, and an objective this far from every merc start are reported.</summary>
    public const double SpyStartNearObjective = 512;

    public const double TeamStartsNear = 768;

    public const double ObjectiveFarFromMercs = 4096;

    // ---- the original engine (errors only when exporting to the original game) ---------------------------------

    /// <summary>Collision triangles per static mesh; past it the game's fixed trace table overflows and it crashes.</summary>
    public const int CollisionTrianglesPerMesh = 5512;

    /// <summary>Mesh collision BSP nodes (16-bit node index).</summary>
    public const int CollisionBspNodes = 32767;

    /// <summary>BSP nodes of the level (16-bit indexes).</summary>
    public const int BspNodes = 32767;

    /// <summary>Per-polygon collision triangles the importer keeps.</summary>
    public const int PerPolyCollisionTriangles = 15000;

    /// <summary>Zones of a level, the default zone included.</summary>
    public const int MaxZones = 64;

    /// <summary>Mover keyframes.</summary>
    public const int MaxMoverKeys = 8;

    /// <summary>The original game's map names are exactly 5 characters (4 plus a mode letter).</summary>
    public const int MapNameLength = 5;

    /// <summary>A map id the original game's map lists take: <see cref="MapNameLength"/> ASCII letters or digits.</summary>
    public static bool IsMapName(string id) => id.Length == MapNameLength && id.All(char.IsAsciiLetterOrDigit);

    /// <summary>More in-game lights than this is a warning (the cap was raised to 128, 89 tested in AeroD).</summary>
    public const int TriggerLightWarning = 40;

    public const int TriggerLightCap = 128;

    /// <summary>Corners per collision hull face.</summary>
    public const int HullFaceCorners = 16;

    /// <summary>
    /// Corners per collision hull the original tools (and the Reloaded fork's) build at most. The game's own limit is not
    /// known, so a hull with more is a warning for either target, not an error.
    /// </summary>
    public const int MaxHullCorners = 128;

    /// <summary>A hull with more triangles than 2V - 4 has overlapping faces and the game ignores it.</summary>
    public static int MaxHullTriangles(int corners) => 2 * corners - 4;

    /// <summary>Half the UE2 world size: actors and brushes past it are outside the world.</summary>
    public const double HalfWorld = 262144;

    /// <summary>GE activation bits: a GE mask is 0 (always on) or one of 1, 2, 4 ... 128.</summary>
    public const int GeMaskBits = 8;
}
