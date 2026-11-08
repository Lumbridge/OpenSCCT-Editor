using System.Text.Json.Nodes;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Compile;

public sealed partial class RenderBuild
{
    /// <summary>Authored brush movers have their own local mesh, so their look follows the simulated actor.</summary>
    private void AddBrushMovers()
    {
        foreach (Actor a in _ctx.Gameplay.Where(a => MoverBase.IsMover(a) && a.Shape is not null && !_instanced.Contains(a.Id)))
        {
            var brush = new Brush(a.Id) { Shape = a.Shape!, Oper = CsgOper.Add };
            var parts = new List<Output.Glb.MeshPart>();
            foreach (var group in CsgBrush.Prepare(brush, 0).Faces.Where(p => (p.Surface.Flags & PolyFlags.Invisible) == 0)
                .GroupBy(p => Material(p.Surface.Material, p.Surface.Flags)))
            {
                var part = new WorldPart(group.Key);
                foreach (var face in group) part.Add(face, null);
                parts.Add(part.ToPart());
            }
            if (parts.Count == 0) continue;
            string asset = $"{_ctx.MapId}.Mover.{a.Id}";
            _library.Add(_glb.AddNode(new JsonObject { ["name"] = asset,
                ["mesh"] = _glb.AddMesh(asset, parts),
                ["extras"] = new JsonObject { ["svmap"] = new JsonObject { ["asset"] = asset, ["role"] = "libraryMesh" } } }));
            _instances.Add(Instance(a.Id, a.ShortClass, true, _ctx.Rules.Flag(a, "bHidden"), asset, a.Location, a.Rotation,
                _ctx.Rules.Scale(a), _ctx.Rules.PrePivot(a), false, "mover"));
            _instanced.Add(a.Id);
        }
    }
}
