using System.Globalization;

namespace OpenScct.Editor.Core.Model;

/// <summary>
/// Where a test play spawns the player once instead of at a team start: OpenSCCT's <c>--start-at</c> (its
/// spec/additions/test-start.md). The game drops the pawn onto a floor up to 200 uu below and keeps the yaw; the view
/// also gets the pitch. The editor first moves a start onto the floor itself
/// (<see cref="Geometry.Edit.ActorPlacement.OnFloor"/>), since a camera is usually far above it.
/// </summary>
public readonly record struct PlayStart(Vec3 Location, Rot Rotation)
{
    /// <summary>The game's launch option.</summary>
    public string Argument => string.Create(CultureInfo.InvariantCulture,
        $"--start-at={Location.X:0.##},{Location.Y:0.##},{Location.Z:0.##},{Rotation.Yaw},{Rotation.Pitch}");

    /// <summary>The perspective view's camera as saved in the workspace (the open editor keeps it current).</summary>
    public static PlayStart Camera(Workspace w) => new(w.CameraLocation, w.CameraRotation);

    /// <summary>
    /// <c>camera</c>, a bookmark's name or number (1 for the first), or <c>X,Y,Z[,Yaw[,Pitch]]</c> in Unreal units and
    /// rotation units. Throws <see cref="ArgumentException"/> naming what it could not read.
    /// </summary>
    public static PlayStart Parse(string text, Workspace w)
    {
        string t = text.Trim();
        if (t.Equals("camera", StringComparison.OrdinalIgnoreCase))
        {
            return Camera(w);
        }

        if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return number >= 1 && number <= w.Bookmarks.Count
                ? FromBookmark(w.Bookmarks[number - 1])
                : throw new ArgumentException($"there is no bookmark {number} (the map has {w.Bookmarks.Count})");
        }

        if (w.Bookmarks.FirstOrDefault(b => b.Name.Equals(t, StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            return FromBookmark(named);
        }

        string[] parts = t.Split(',', StringSplitOptions.TrimEntries);
        var n = new double[5];
        if (parts.Length is < 3 or > 5 || parts.Where((p, i) => !double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]) || !double.IsFinite(n[i])).Any())
        {
            throw new ArgumentException($"'{text}' is not camera, a bookmark or X,Y,Z[,Yaw[,Pitch]]");
        }

        return new PlayStart(new Vec3(n[0], n[1], n[2]), new Rot((int)Math.Round(n[4]), (int)Math.Round(n[3]), 0));
    }

    private static PlayStart FromBookmark(Bookmark b) => new(b.Location, b.Rotation);
}
