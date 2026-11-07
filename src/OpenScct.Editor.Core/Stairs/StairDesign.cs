using System.Text.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Stairs;

/// <summary>The authored staircase. Its owning brush supplies identity, CSG order and transform;
/// its polygons are a disposable preview generated from these dimensions.</summary>
public sealed class StairDesign
{
    public string Layout { get; set; } = "straight";
    public double Width { get; set; } = 192;
    public double Rise { get; set; } = 256;
    public double Run { get; set; } = 512;
    public double TargetStepHeight { get; set; } = 16;
    public int StepCount { get; set; }
    public double LandingLength { get; set; } = 192;
    public double InnerRadius { get; set; } = 240;
    public double TurnDegrees { get; set; } = 90;
    public bool Left { get; set; }
    public double TreadThickness { get; set; } = 8;
    public double Overhang { get; set; }
    public double EdgeRadius { get; set; }
    public bool SlopedUnderside { get; set; }
    public bool LegacyOpenRisers { get; set; }
    public bool ClampUndersideToFloor { get; set; }
    public double CentralColumnRadius { get; set; }
    public bool RoundStairwell { get; set; }
    public bool Smooth { get; set; } = true;
    public bool Carve { get; set; } = true;
    public double Headroom { get; set; } = 224;
    public double ClearanceMargin { get; set; }
    public double ClearanceStart { get; set; }
    public double ClearanceEnd { get; set; }
    public string TreadMaterial { get; set; } = "Editor.Grid";
    public string RiserMaterial { get; set; } = "Editor.Grid";
    public string SideMaterial { get; set; } = "Editor.Grid";
    public double TreadTextureScale { get; set; } = 1;
    public double RiserTextureScale { get; set; } = 1;
    public double SideTextureScale { get; set; } = 1;
    public double TreadTextureRotation { get; set; }
    public double RiserTextureRotation { get; set; }
    public double SideTextureRotation { get; set; }
    public double TreadPanU { get; set; }
    public double TreadPanV { get; set; }
    public double RiserPanU { get; set; }
    public double RiserPanV { get; set; }
    public double SidePanU { get; set; }
    public double SidePanV { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int Count => StepCount == 0 ? Math.Max(1, (int)Math.Ceiling(Rise / TargetStepHeight - 1e-9)) : StepCount;
    public StairDesign Clone() => (StairDesign)MemberwiseClone();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public static StairDesign FromJson(string json) => JsonSerializer.Deserialize<StairDesign>(json, JsonOptions)
        ?? throw new InvalidDataException("Missing stair dimensions");
    public static readonly string[] Layouts = ["straight", "l", "u", "curved", "spiral"];

    public void Validate()
    {
        if (new[] { ClearanceMargin, ClearanceStart, ClearanceEnd }.Any(v => !double.IsFinite(v) || v < 0 || v > 262144))
            throw new ArgumentException("Clearance extensions must be between 0 and 262144 units");
        if (Layout is "curved" or "spiral" && ClearanceMargin >= InnerRadius)
            throw new ArgumentException("Clearance side margin must stay smaller than the inner radius");
        if (!Layouts.Contains(Layout)) throw new ArgumentException("Choose straight, l, u, curved or spiral stairs");
        foreach (double n in new[] { Width, Rise, Run, TargetStepHeight, LandingLength, InnerRadius,
            TurnDegrees, CentralColumnRadius, TreadThickness, Overhang, EdgeRadius, Headroom, TreadTextureScale, RiserTextureScale, SideTextureScale, TreadTextureRotation, RiserTextureRotation, SideTextureRotation, TreadPanU, TreadPanV, RiserPanU, RiserPanV, SidePanU, SidePanV })
            if (!double.IsFinite(n)) throw new ArgumentException("Stair dimensions must be finite");
        if (new[] { Width, Rise, Run, LandingLength, InnerRadius, TreadThickness, Overhang, Headroom }.Any(n => Math.Abs(n) > 262144) || TargetStepHeight < .25)
            throw new ArgumentException("Stair dimensions must stay within 262144 units; target step height must be at least 0.25");
        if (CentralColumnRadius < 0 || CentralColumnRadius > InnerRadius) throw new ArgumentException("Central column radius must fit inside the stair well");
        if (TreadMaterial is null || RiserMaterial is null || SideMaterial is null)
            throw new ArgumentException("Use a material path or an empty string for the default material");
        if (Width < 32 || Rise <= 0 || Run < 32 || TargetStepHeight <= 0 || Headroom < 1 || (Layout is "l" or "u" && LandingLength < Width))
            throw new ArgumentException("Width must be at least 32, run at least 32, rise and step height positive, and landing length at least the width");
        if (StepCount < 0 || Count > 256 || (Layout is "l" or "u" && Count < 2))
            throw new ArgumentException("Use 1–256 steps (at least two for L and U stairs)");
        if (TreadThickness <= 0 || TreadThickness > Rise)
            throw new ArgumentException($"Tread thickness must be greater than zero and at most {Rise:0.##}");
        if (Overhang < 0 || EdgeRadius < 0 || EdgeRadius > Math.Min(TreadThickness, Rise / Count) / 2)
            throw new ArgumentException($"Overhang cannot be negative; edge radius must be between 0 and {Math.Min(TreadThickness, Rise / Count) / 2:0.##}");
        double depth = Layout is "curved" or "spiral" ? InnerRadius * Math.Sin(TurnDegrees / Count * Math.PI / 180) : Run / Count;
        if (Layout is "curved" or "spiral" && (InnerRadius < 16 || TurnDegrees <= 0 || TurnDegrees > 1440 || TurnDegrees / Count > 45))
            throw new ArgumentException("Curved stairs need an inner radius of at least 16 and no more than 45 degrees per tread (total turn up to 1440 degrees)");
        if (Overhang + EdgeRadius > depth / 2)
            throw new ArgumentException($"Overhang plus edge radius must not exceed half the narrowest tread depth ({depth / 2:0.##})");
        if (TreadTextureScale <= 0 || RiserTextureScale <= 0 || SideTextureScale <= 0)
            throw new ArgumentException("Texture scales must be positive");
        if (Smooth && Rise / Run > .75 && Layout is not ("curved" or "spiral"))
            throw new ArgumentException("This flight is too steep to smooth; increase run or disable smoothing");
        if (Smooth && Layout is "curved" or "spiral" && Rise / ((InnerRadius + Math.Min(35, Width / 4)) * TurnDegrees * Math.PI / 180) > .75)
            throw new ArgumentException("The inside of this curved flight is too steep; increase inner radius, increase turn, reduce rise or disable smoothing");
        if (Layout == "spiral" && TurnDegrees >= 360 && Rise * 360 / TurnDegrees - TreadThickness - (LegacyOpenRisers ? 0 : Rise / Count) < Headroom)
            throw new ArgumentException("Spiral turns overlap the requested headroom; increase rise or reduce total turn");
    }
}
