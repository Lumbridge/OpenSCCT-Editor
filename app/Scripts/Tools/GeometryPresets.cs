using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Validation;

namespace OpenScctEditor.Tools;

public sealed record GeometryPreset(string Title, string Category, string Kind, string Summary,
    IReadOnlyDictionary<string, double> Numbers, IReadOnlyDictionary<string, string> Strings)
{
    public BuilderSpec Build(DesignNumbers? body = null)
    {
        BuilderSpec spec = BuilderBrush.DefaultSpec(Kind, body);
        foreach (var (key, value) in Numbers) spec.Numbers[key] = value;
        foreach (var (key, value) in Strings) spec.Strings[key] = value;
        return spec;
    }
}

/// <summary>Small, editable starting points. Floor slabs are solid geometry; sheets are explicitly non-solid.</summary>
public static class GeometryPresets
{
    public static IReadOnlyList<GeometryPreset> All { get; } = Create();
    private static IReadOnlyList<GeometryPreset> Create()
    {
        var all = new List<GeometryPreset>();
        void Add(string title, string category, string kind, string summary, (string, double)[]? numbers = null, (string, string)[]? strings = null) =>
            all.Add(new(title, category, kind, summary, (numbers ?? []).ToDictionary(x => x.Item1, x => x.Item2), (strings ?? []).ToDictionary(x => x.Item1, x => x.Item2)));
        foreach (int size in new[] { 128, 256, 512, 1024, 2048 })
            Add($"Square {size}", "Floors", "cube", $"Solid floor slab: {size} x {size} x 32 units", [("breadth", size), ("width", size), ("height", 32)]);
        foreach (var (x, y) in new[] { (256, 128), (512, 256), (1024, 512), (2048, 1024) })
            Add($"Rectangle {x} x {y}", "Floors", "cube", $"Solid rectangular floor, 32 units thick", [("breadth", x), ("width", y), ("height", 32)]);
        foreach (int radius in new[] { 128, 256, 512 })
        {
            Add($"Round {radius * 2}", "Floors", "cylinder", "Solid 32-sided circular floor, 32 units thick; title is diameter", [("outerRadius", radius), ("height", 32), ("sides", 32)]);
            Add($"Octagon {radius * 2}", "Floors", "cylinder", "Solid octagonal floor, 32 units thick; title is diameter", [("outerRadius", radius), ("height", 32), ("sides", 8)]);
        }
        foreach (int width in new[] { 256, 512, 1024 })
            Add($"Wall {width}", "Walls", "cube", $"Wall section: {width} long, 32 thick, 256 high", [("breadth", width), ("width", 32), ("height", 256)]);
        Add("Low wall", "Walls", "cube", "512 long, 32 thick, 128 high", [("breadth", 512), ("width", 32), ("height", 128)]);
        foreach (int size in new[] { 512, 1024, 2048 })
        {
            Add($"Carved room {size}", "Rooms", "room", "Carve an interior from solid world", [("length", size), ("width", size)]);
            Add($"Room shell {size}", "Rooms", "room", "Walls, floor and ceiling for existing open space", [("length", size), ("width", size)], [("mode", "shell")]);
        }
        Add("Corridor", "Passages", "corridor", "Standing-height connection between rooms");
        Add("Long corridor", "Passages", "corridor", "1024-unit standing-height connection", [("length", 1024)]);
        Add("Spy vent", "Passages", "vent", "Vent sized from the map's spy movement limits");
        Add("Long vent", "Passages", "vent", "1024-unit spy vent", [("length", 1024)]);
        Add("Crawlway", "Passages", "crawlway", "Low crawlspace using the map's body dimensions");
        Add("Wide crawlway", "Passages", "crawlway", "256-unit wide crawlspace", [("width", 256)]);
        foreach (string kind in new[] { "doorway", "window" })
        {
            Add(kind == "doorway" ? "Door opening" : "Window opening", "Openings", kind, "Carve an opening through an existing wall");
            Add(kind == "doorway" ? "Door in wall" : "Window in wall", "Openings", kind, "Build a wall shell around the opening", strings: [("mode", "shell")]);
        }
        Add("Gentle ramp", "Ramps", "ramp", "512 run, 128 rise", [("length", 512), ("rise", 128)]);
        Add("Storey ramp", "Ramps", "ramp", "1024 run, 256 rise", [("length", 1024), ("rise", 256)]);
        Add("Wide ramp", "Ramps", "ramp", "512 run, 384 wide, 128 rise", [("length", 512), ("width", 384), ("rise", 128)]);
        Add("Low platform", "Platforms", "platform", "256 square, 128 high");
        Add("Wide platform", "Platforms", "platform", "512 square, 128 high", [("length", 512), ("width", 512)]);
        Add("Raised slab", "Platforms", "platform", "512 square, top at 256, slab 32 thick", [("length", 512), ("width", 512), ("height", 256), ("thickness", 32)]);
        Add("Short ledge", "Platforms", "ledge", "256 long ledge at 256 high");
        Add("Long ledge", "Platforms", "ledge", "512 long ledge at 256 high", [("length", 512)]);
        Add("Block", "Primitives", "cube", "256-unit solid cube");
        Add("Large block", "Primitives", "cube", "512-unit solid cube", [("breadth", 512), ("width", 512), ("height", 512)]);
        Add("Column", "Primitives", "cylinder", "64 radius, 256 high", [("outerRadius", 64), ("height", 256), ("sides", 16)]);
        Add("Wide column", "Primitives", "cylinder", "128 radius, 384 high", [("outerRadius", 128), ("height", 384), ("sides", 16)]);
        Add("Cone", "Primitives", "cone", "128 radius, 256 high", [("outerRadius", 128)]);
        Add("Low cone", "Primitives", "cone", "256 radius, 128 high", [("outerRadius", 256), ("height", 128)]);
        Add("Sphere", "Primitives", "tetrahedron", "256-radius sphere approximation");
        Add("Small sphere", "Primitives", "tetrahedron", "64-radius sphere approximation", [("radius", 64)]);
        Add("Crossed sheets", "Sheets", "volumetric", "Non-solid crossed sheets for decorative geometry");
        Add("Four crossed sheets", "Sheets", "volumetric", "Four non-solid decorative sheets", [("numSheets", 4)]);
        foreach (string axis in new[] { "horizontal", "x", "y" })
            Add($"Sheet {axis}", "Sheets", "sheet", "Flat non-solid sheet; does not provide a walkable floor", strings: [("axis", axis)]);
        foreach (string kind in new[] { "linearStair", "curvedStair", "spiralStair", "stairs" })
            Add(kind == "stairs" ? "Blockout stairs" : kind.Replace("Stair", " stairs"), "Stair brushes", kind, "Parametric CSG stair brush; use the Stairs palette for editable stair assemblies");
        return all;
    }
}
