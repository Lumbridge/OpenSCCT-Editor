using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop.T3d;

public static partial class T3dExport
{
    /// <summary>A conveyor lowered to RE+ belts, with the names the export gives them.</summary>
    private sealed record LoweredConveyor(ReConveyorChain Chain, List<string> Names);

    /// <summary>
    /// The written conveyors lowered to RE+ belts (<see cref="ReConveyors"/>), named before any is written, so a
    /// conveyor's last belt can lead on (<c>NextBelt</c>) to the first belt of the conveyor its <c>Next</c> names.
    /// </summary>
    private static Dictionary<string, LoweredConveyor> LowerConveyors(MapProject p, IEnumerable<Actor> written, HashSet<string> names) =>
        written.Where(ConveyorDesign.Is).ToDictionary(a => a.Id, a =>
        {
            ReConveyorChain chain = ReConveyors.Lower(p, a, []);
            return new LoweredConveyor(chain, chain.Belts.Select(b => Unique(b.Name, names)).ToList());
        }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The first RE+ belt the export writes for a conveyor: its first lowered belt, or for a converted conveyor written
    /// as its imported belts, the first of those; null when it writes none.
    /// </summary>
    private static string? FirstBelt(MapProject p, string id, Dictionary<string, LoweredConveyor> lowered, ConveyorHandoff handoff,
        HashSet<string> writtenIds)
    {
        if (lowered.TryGetValue(id, out LoweredConveyor? l))
        {
            return l.Names.FirstOrDefault();
        }

        return handoff.Carried.Contains(id) && p.FindActor(id) is { } c && BakedContent.LiftedFrom(c).FirstOrDefault() is { } belt
            && writtenIds.Contains(belt) ? belt : null;
    }

    /// <summary>
    /// A conveyor as RE+ 2.8.0 <c>ConveyorBelt</c> actors linked by <c>NextBelt</c> (<see cref="ReConveyors"/>); the
    /// last of an open chain leads on to <paramref name="nextBelt"/>, the first belt of the conveyor that takes its
    /// items, so RE+ runs them as one chain. The frame's DrawScale3D is (Length / 100, Width / 100, 1), as RE+'s editor
    /// keeps it; a driver is hidden and does not collide, as AeroD's are.
    /// </summary>
    private static IEnumerable<T3dActor> ConveyorBelts(MapProject p, LoweredConveyor lowered, string? nextBelt)
    {
        (ReConveyorChain chain, List<string> named) = (lowered.Chain, lowered.Names);
        for (int i = 0; i < chain.Belts.Count; i++)
        {
            ReBelt b = chain.Belts[i];
            var a = new T3dActor("ConveyorBelt", named[i]);
            AddTransform(a, b.Start, b.Rotation, 1, new Vec3(b.Length / 100, chain.Width / 100, 1), Vec3.Zero);
            Add(a, "Length", T3dAtom.Float(b.Length));
            Add(a, "Width", T3dAtom.Float(chain.Width));
            Add(a, "Speed", T3dAtom.Float(chain.Speed));
            string? next = i + 1 < chain.Belts.Count ? named[i + 1] : chain.Loop ? named[0] : nextBelt;
            if (next is not null)
            {
                Add(a, "NextBelt", new T3dRef("ConveyorBelt", T3dTypes.LevelPackage + "." + next));
            }

            Add(a, "ItemSpacing", T3dAtom.Float(chain.ItemSpacing));
            Add(a, "FadeDistance", T3dAtom.Float(chain.FadeDistance));
            Add(a, "ItemScale", T3dAtom.Float(chain.ItemScale));
            Add(a, "bCarryPlayers", Bool(chain.CarryPlayers));
            Add(a, "bShowItems", Bool(chain.ItemMeshes.Count > 0 && chain.ItemSpacing > 0));
            Add(a, "bShowRollers", Bool(chain.ShowRollers));
            Add(a, "bLeftWall", Bool(b.LeftWall));
            Add(a, "bRightWall", Bool(b.RightWall));
            for (int m = 0; m < chain.ItemMeshes.Count; m++)
            {
                string mesh = chain.ItemMeshes[m];
                string path = mesh.StartsWith(p.Id + ".", StringComparison.OrdinalIgnoreCase) ? T3dTypes.LevelPackage + mesh[p.Id.Length..] : mesh;
                a.Props.Add(new T3dField("ItemMeshes", m, new T3dRef("StaticMesh", path)));
            }

            if (chain.Driver)
            {
                Add(a, "bHidden", T3dAtom.True);
                Add(a, "bCollideActors", T3dAtom.False);
                Add(a, "bBlockActors", T3dAtom.False);
                Add(a, "bBlockPlayers", T3dAtom.False);
            }

            yield return a;
        }
    }

    private static void Add(T3dActor a, string name, T3dValue value) => a.Props.Add(new T3dField(name, null, value));

    private static T3dAtom Bool(bool value) => value ? T3dAtom.True : T3dAtom.False;
}
