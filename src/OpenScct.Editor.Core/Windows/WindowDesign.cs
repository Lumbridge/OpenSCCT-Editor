using System.Text.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Windows;

public readonly record struct WindowPoint(double Y, double Z);

/// <summary>Source-local control points keep fitted glass attached without accumulating transform errors.</summary>
public sealed class WindowBinding
{
    public string[] Sources { get; set; } = [];
    public double[][] Points { get; set; } = [];
    public string GeometryKey { get; set; } = "";
    public double[][][] SourcePoints { get; set; } = [];
    public bool NeedsRefit { get; set; }
    public double NormalScale { get; set; } = 1;
    public WindowBinding Clone() => new() { Sources = [.. Sources], Points = Points.Select(p => p.ToArray()).ToArray(), SourcePoints = SourcePoints.Select(s => s.Select(p => p.ToArray()).ToArray()).ToArray(), GeometryKey = GeometryKey, NeedsRefit = NeedsRefit, NormalScale = NormalScale };
}

/// <summary>Editor authoring data; lowered to an ordinary SVitre and permanent frame geometry at build time.</summary>
public sealed class WindowDesign
{
    internal bool IsLowered { get; set; }
    public static readonly string[] Shapes = ["rectangle", "circle", "oval", "arch", "triangle", "polygon"];
    public static readonly string[] Finishes = ["clear", "tinted", "frosted"];
    public string Shape { get; set; } = "rectangle";
    public double Width { get; set; } = 160;
    public double Height { get; set; } = 128;
    public double Thickness { get; set; } = 2;
    public int Sides { get; set; } = 6;
    public bool Framed { get; set; }
    public double FrameWidth { get; set; } = 8;
    public double FrameDepth { get; set; } = 16;
    public string FrameMaterial { get; set; } = "Editor.WindowFrame";
    public string GlassMaterial { get; set; } = "";
    public string Finish { get; set; } = "clear";
    public double[] Tint { get; set; } = [.72, .88, .95];
    public double Clearance { get; set; } = .5;
    public double DepthOffset { get; set; }
    public double CutDepth { get; set; }
    public string? Wall { get; set; }
    public List<WindowPoint> Outline { get; set; } = [];
    public WindowBinding? Binding { get; set; }
    /// <summary>Frame meshes whose visible holes must also be holes in collision, retained on Detach.</summary>
    public string[] FrameCollisionSources { get; set; } = [];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public static WindowDesign FromJson(string json) => JsonSerializer.Deserialize<WindowDesign>(json, JsonOptions) ?? throw new InvalidDataException("Missing window design");
    public WindowDesign Clone() { var c = (WindowDesign)MemberwiseClone(); c.Tint = [.. Tint]; c.Outline = [.. Outline]; c.FrameCollisionSources = [.. FrameCollisionSources]; c.Binding = Binding?.Clone(); return c; }
    public void Validate()
    {
        if (!Shapes.Contains(Shape) || !Finishes.Contains(Finish)) throw new ArgumentException("Choose a window shape and glass finish from the palette");
        if (new[] { Width, Height, Thickness, FrameWidth, FrameDepth, Clearance, DepthOffset, CutDepth }.Any(n => !double.IsFinite(n) || Math.Abs(n) > 262144))
            throw new ArgumentException("Window dimensions must be finite and within 262144 units");
        if (Width < 2 || Height < 2 || Thickness < .125 || FrameWidth < .125 || FrameDepth < .125 || Clearance < 0 || CutDepth < 0 || Sides < 3 || Sides > 32)
            throw new ArgumentException("Window dimensions must be positive; polygons need 3–32 sides");
        if (Tint is not { Length: 3 } || Tint.Any(n => !double.IsFinite(n) || n < 0 || n > 1)) throw new ArgumentException("Glass tint needs three colour values between 0 and 1");
        if (Shape == "circle") Height = Width;
        if (Outline.Count > 512 || Outline.Any(p => !double.IsFinite(p.Y) || !double.IsFinite(p.Z) || Math.Abs(p.Y) > 262144 || Math.Abs(p.Z) > 262144))
            throw new ArgumentException("Window outline is too large or contains invalid points");
        if (Outline.Count is 1 or 2) throw new ArgumentException("A fitted outline needs at least three points");
        if (FrameMaterial is null || GlassMaterial is null) throw new ArgumentException("Use a material path or an empty string for automatic glass");
        if (Binding is { } b && (!double.IsFinite(b.NormalScale) || b.NormalScale <= 0 || b.Sources.Length == 0 || b.Points.Length < 7 || b.Points.Any(p => p.Length != 3 || p.Any(n => !double.IsFinite(n)))
            || b.SourcePoints.Any(s => s.Any(p => p.Length != 3 || p.Any(n => !double.IsFinite(n))))))
            throw new ArgumentException("Invalid source-frame binding");
    }
}
