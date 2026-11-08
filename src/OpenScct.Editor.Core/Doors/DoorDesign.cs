using System.Text.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Doors;

/// <summary>Authoring data for a generated mover door. Dimensions describe the clear doorway.</summary>
public sealed class DoorDesign
{
    internal bool IsLowered { get; set; }
    public static readonly string[] Styles = ["plain", "panelled", "industrial", "glazed", "louvred", "security"];
    public static readonly string[] Motions = ["swing left", "swing right", "slide left", "slide right", "raise"];
    public static readonly string[] Behaviours = ["BumpOpenTimed", "TriggerOpenTimed", "TriggerToggle", "TriggerControl", "TriggerOpenOnceOnly"];
    public string Style { get; set; } = "panelled";
    public string Motion { get; set; } = "swing left";
    public string Behaviour { get; set; } = "BumpOpenTimed";
    public double Width { get; set; } = 128;
    public double Height { get; set; } = 224;
    public double Thickness { get; set; } = 8;
    public bool Framed { get; set; } = true;
    public double FrameWidth { get; set; } = 8;
    public double FrameDepth { get; set; } = 24;
    public double Clearance { get; set; } = 1;
    public double OpenAngle { get; set; } = 90;
    public double Travel { get; set; } // zero means the full width/height
    public bool Reverse { get; set; }
    public double MoveTime { get; set; } = 1;
    public double StayOpen { get; set; } = 4;
    public string Material { get; set; } = "";
    public string FrameMaterial { get; set; } = "Editor.DoorFrame";
    public double CutDepth { get; set; }
    public string? Wall { get; set; }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public static DoorDesign FromJson(string json) => JsonSerializer.Deserialize<DoorDesign>(json, JsonOptions) ?? throw new InvalidDataException("Missing door design");
    public DoorDesign Clone() => (DoorDesign)MemberwiseClone();
    public void Validate()
    {
        if (!Styles.Contains(Style) || !Motions.Contains(Motion) || !Behaviours.Contains(Behaviour)) throw new ArgumentException("Choose a door style, movement and activation from the palette");
        if (new[] { Width, Height, Thickness, FrameWidth, FrameDepth, Clearance, OpenAngle, Travel, MoveTime, StayOpen, CutDepth }.Any(v => !double.IsFinite(v) || Math.Abs(v) > 262144)) throw new ArgumentException("Door dimensions and times must be finite and within 262144");
        if (Width < 48 || Height < 64 || Thickness < 1 || FrameWidth < 1 || FrameDepth < 1 || Clearance < 0 || Clearance * 2 >= Math.Min(Width, Height) || CutDepth < 0 || Travel < 0 || MoveTime < .1 || StayOpen < 0 || OpenAngle < 1 || OpenAngle > 175)
            throw new ArgumentException("Use positive door dimensions, an opening angle of 1–175°, and at least 0.1 seconds to open");
        if (Material is null || FrameMaterial is null) throw new ArgumentException("Use a material path, or leave the door material empty for its preset finish");
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public Vec3 Pivot => Motion == "swing left" ? new(0, -Width / 2, 0) : Motion == "swing right" ? new(0, Width / 2, 0) : Vec3.Zero;
}
