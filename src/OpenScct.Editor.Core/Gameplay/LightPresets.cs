using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>Editable native lights for the editor palette. Animated lights illuminate the world at runtime.</summary>
public sealed record LightPreset(string Title, string Summary, string Icon, int Hue = 0, int Saturation = 255,
    int Effect = 0, int Cone = 128, int Type = 1, int Brightness = 128, int Radius = 24)
{
    public Actor Create(MapProject project, Vec3 location)
    {
        var actor = new Actor(project.NewId("Light"), "Engine.Light") { Location = location };
        actor.Props["LightHue"] = SvJsonNode.Integer(Hue);
        actor.Props["LightSaturation"] = SvJsonNode.Integer(Saturation);
        actor.Props["LightBrightness"] = SvJsonNode.Integer(Brightness);
        actor.Props["LightRadius"] = SvJsonNode.Integer(Radius);
        actor.Props["LightEffect"] = SvJsonNode.Integer(Effect);
        actor.Props["LightCone"] = SvJsonNode.Integer(Cone);
        actor.Props["LightType"] = SvJsonNode.Integer(Type);
        actor.Props["bApplyToInGameLighting"] = SvJsonNode.True;
        actor.Props["bApplyToStaticLighting"] = Type == 1 ? SvJsonNode.True : SvJsonNode.False;
        actor.Props["bDynamicLight"] = Type == 1 ? SvJsonNode.False : SvJsonNode.True;
        if (Type != 1) actor.Props["LightPeriod"] = SvJsonNode.Integer(32);
        if (Effect is 8 or 19) actor.Rotation = new Rot(-16384, 0, 0);
        return actor;
    }
}

public static class LightPresets
{
    public static IReadOnlyList<LightPreset> All { get; } = Array.AsReadOnly<LightPreset>(
    [
        new("Point", "White light in every direction.", "type-light"),
        new("Ambient Fill", "Local non-incidence fill light independent of surface direction. Radius limits its reach.", "type-light", Effect: 13, Brightness: 48),
        new("Warm Point", "Warm amber light for lamps and interiors.", "type-light", Hue: 28, Saturation: 160),
        new("Cool Point", "Cool blue light for industrial spaces.", "type-light", Hue: 150, Saturation: 185),
        new("Spot", "A focused cone, initially pointing down. Rotate to aim.", "type-light-spot", Effect: 8, Cone: 48),
        new("Wide Spot", "A broad cone, initially pointing down. Rotate to aim.", "type-light-spot", Effect: 8, Cone: 128),
        new("Sunlight", "Directional sunlight, initially pointing down. Rotate to aim; radius does not limit its reach.", "type-light-sun", Effect: 19, Brightness: 64),
        new("Red Accent", "Red light for warning areas and accents.", "type-light", Saturation: 40),
        new("Pulse", "A pulsing red runtime light. Edit LightPeriod to change its speed.", "type-light-trigger", Saturation: 40, Type: 2),
        new("Flicker", "A flickering warm runtime light for faulty lamps.", "type-light-trigger", Hue: 28, Saturation: 160, Type: 4),
        new("Blue Accent", "Saturated blue light for accents and night scenes.", "type-light", Hue: 170, Saturation: 40),
        new("Green Accent", "Green light for status indicators and stylized spaces.", "type-light", Hue: 85, Saturation: 40),
        new("Blink", "An alternating runtime warning light. Edit LightPeriod to change its speed.", "type-light-trigger", Hue: 28, Saturation: 80, Type: 3),
    ]);
}
