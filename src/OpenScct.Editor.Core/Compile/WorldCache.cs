using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

/// <summary>
/// What the build's world step can keep between runs (the Lit Preview runs it on every change): the render world's
/// CSG, and the collision world of a map that keeps one baked layer (built from every brush, so slow on a big map),
/// whole while its brushes, zone actors and numbering are unchanged, else rebuilt on its own CSG cache. Not thread
/// safe: one run at a time.
/// </summary>
public sealed class WorldCache
{
    private readonly CsgWorld _collisionCsg = new();
    private WorldModel? _collision;
    private ulong _collisionKey;

    public CsgWorld Render { get; } = new();

    /// <summary>The collision world of the brushes; the one built last when nothing it is built from changed.</summary>
    public WorldModel CollisionWorld(IReadOnlyList<Brush> brushes, IReadOnlyList<ZoneSeed> seeds, bool worldSolid, ZoneNumbering numbering)
    {
        var h = new Hash64();
        h.Add(worldSolid);
        h.Add(numbering.MaxZone);
        foreach (Brush b in brushes)
        {
            h.Add(b.Id);
            h.Add(CsgBrush.FingerprintOf(b));
        }

        foreach (ZoneSeed s in seeds)
        {
            h.Add(s.ActorId);
            h.Add(s.Location);
            h.Add(s.Sky);
        }

        if (numbering.Source is { } source)
        {
            // The source's zones only change with the source map, read afresh each run.
            foreach (string? id in source.Actors)
            {
                h.Add(id);
            }
        }

        if (_collision is null || h.Value != _collisionKey)
        {
            _collision = WorldModel.Build(brushes, seeds, worldSolid, numbering, _collisionCsg);
            _collisionKey = h.Value;
        }

        return _collision;
    }
}
