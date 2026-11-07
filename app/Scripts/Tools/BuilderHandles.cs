using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Tools;

/// <summary>Semantic sizing, independent of camera and UI. Values are dimensions, never arbitrary mesh scales.</summary>
public sealed record BuilderSizeHandle(string Key, string Label, int Axis, double Factor = 1,
    double Anchor = .5, double Offset = 0, bool Radial = false, int Sign = 1)
{
    public double Value(BuilderSpec spec) => spec.Get(Key, 0) * Factor;

    public double ReadoutValue(BuilderSpec spec) => Value(spec) + (Key == "stepHeight" ? Offset : 0);

    public Vec3 Point(BuilderSpec spec, Vec3 center) => Radial
        ? Vec3.Zero.With(2, center.Z).With(Axis, Offset + Value(spec))
        : center.With(Axis, Offset + (Sign > 0 ? 1 - Anchor : -Anchor) * Value(spec));

    public (BuilderSpec Spec, Vec3 Location) Candidate(BuilderBrush.State start, double distance)
    {
        BuilderSpec spec = start.Spec.Clone();
        BuilderParam param = BuilderCatalog.Get(spec.Kind).Params.First(p => p.Name == Key);
        double value = Math.Clamp((Value(spec) + distance) / Factor, param.Min, param.Max);
        spec.Numbers[Key] = value;
        double change = value * Factor - Value(start.Spec);
        double shift = Radial ? 0 : (Sign > 0 ? Anchor : -(1 - Anchor)) * change;
        return (spec, start.Location + start.Rotation.Rotate(Vec3.Zero.With(Axis, shift)));
    }
}

public static class BuilderHandles
{
    public static IReadOnlyList<BuilderSizeHandle> For(BuilderSpec s)
    {
        var result = new List<BuilderSizeHandle>();
        void Dim(string key, string label, int axis, double anchor = .5, double factor = 1, double offset = 0, bool both = true)
        {
            result.Add(new(key, label, axis, factor, anchor, offset));
            if (both) result.Add(new(key, label, axis, factor, anchor, offset, Sign: -1));
        }
        void Rad(string key, string label, double offset = 0, bool spatial = false)
        {
            // A radial control remains usable when any one axis is end-on (e.g. the Side view).
            foreach (int axis in spatial ? new[] { 0, 1, 2 } : new[] { 0, 1 })
                result.Add(new(key, label, axis, Offset: offset, Radial: true));
        }
        switch (s.Kind.ToLowerInvariant())
        {
            case "cube":
                Dim("breadth", "Length (X)", 0); Dim("width", "Width (Y)", 1); Dim("height", "Height (Z)", 2); break;
            case "room": case "corridor": case "vent": case "crawlway":
                Dim("length", "Length (X)", 0); Dim("width", "Width (Y)", 1); Dim("height", "Height (Z)", 2, 0); break;
            case "platform":
                Dim("length", "Length (X)", 0); Dim("width", "Width (Y)", 1);
                Dim("height", "Height (Z)", 2, 0, both: s.Get("thickness", 0) == 0 || s.Get("thickness", 0) >= s.Get("height", 0)); break;
            case "ledge":
                Dim("length", "Length (X)", 0); Dim("depth", "Depth (Y)", 1); Dim("height", "Height (Z)", 2, 0); break;
            case "ramp":
                Dim("length", "Run (X)", 0); Dim("width", "Width (Y)", 1); Dim("rise", "Rise (Z)", 2, 0, both: false); break;
            case "doorway": case "window":
                Dim("depth", "Cut depth (X)", 0); Dim("width", "Opening width (Y)", 1);
                Dim("height", "Opening height (Z)", 2, 0, offset: s.Get("sillHeight", 0), both: false); break;
            case "cylinder": case "cone":
                Rad("outerRadius", "Radius"); Dim("height", "Height (Z)", 2, s.Kind == "cone" ? 0 : .5, both: false); break;
            case "tetrahedron": Rad("radius", "Radius", spatial: true); break;
            case "volumetric":
                Rad("radius", "Radius"); Dim("height", "Height (Z)", 2, both: false); break;
            case "sheet":
                string axis = s.Strings.GetValueOrDefault("axis", "horizontal");
                int widthAxis = axis == "x" ? 1 : 0, heightAxis = axis == "horizontal" ? 1 : 2;
                Dim("width", $"Width ({"XYZ"[widthAxis]})", widthAxis);
                Dim("height", $"Height ({"XYZ"[heightAxis]})", heightAxis); break;
            case "linearstair":
                Dim("stepLength", "Flight length (X)", 0, 0, s.Get("numSteps", 8), both: false);
                Dim("stepWidth", "Flight width (Y)", 1);
                Dim("stepHeight", "Total rise (Z)", 2, 0, s.Get("numSteps", 8), s.Get("addToFirstStep", 0), false); break;
            case "curvedstair": case "spiralstair":
                Rad("innerRadius", "Inner radius"); Rad("stepWidth", "Tread width", s.Get("innerRadius", 0));
                Dim("stepHeight", "Total rise (Z)", 2, 0, s.Get("numSteps", 8), s.Get("addToFirstStep", 0), false); break;
            case "stairs":
                Dim("rise", "Total rise (Z)", 2, 0, both: false);
                // Flight width and tread depth are semantic controls: layouts may occupy several axes.
                Dim("width", "Flight width", 1, 0, both: false);
                if (s.Strings.GetValueOrDefault("layout", "straight") == "spiral") Rad("wellRadius", "Well radius");
                else Dim("stepLength", "Tread depth", 0, 0, both: false);
                break;
        }
        return result;
    }
}
