using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Scripting;

namespace OpenScct.Editor.Core.Stairs;

public static class StairStore
{
    public static SvJsonObject Write(MapProject p) => ApiJson.Obj(("stairs", ApiJson.Arr(p.Brushes.Select((b, i) => (b, i))
        .Where(x => x.b.Stair is not null).Select(x => (SvJsonNode)ApiJson.Obj(
            ("order", SvJsonNode.Integer(x.i)), ("object", OwnerJson(x.b)),
            ("design", ApiJson.Parse(x.b.Stair!.ToJson(), "stair")))))));

    private static SvJsonObject OwnerJson(Brush b)
    {
        Brush copy = b.Clone(); copy.Shape = new BrushShape(); copy.Stair = null;
        return ModelJson.BrushToJson(copy);
    }

    public static void Read(MapProject p, SvJsonObject? json)
    {
        foreach (SvJsonObject entry in (json?["stairs"] as SvJsonArray ?? []).OfType<SvJsonObject>().OrderBy(e => (int)ModelJson.Num(e["order"])))
        {
            if (entry["object"] is not SvJsonObject obj || entry["design"] is not SvJsonObject design)
                throw new InvalidDataException("A stair needs an object and design");
            Brush b = ModelJson.BrushFromJson(obj);
            if (p.Find(b.Id) is not null) throw new InvalidDataException($"Duplicate stair id {b.Id}");
            b.Stair = StairDesign.FromJson(ApiJson.Write(design));
            StairGeometry.Rebuild(b);
            p.Brushes.Insert(Math.Clamp((int)ModelJson.Num(entry["order"]), 0, p.Brushes.Count), b);
        }
    }
}
