using System.Globalization;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Geometry.Builders;

/// <summary>How a builder parameter is stored and shown.</summary>
public enum ParamType
{
    /// <summary>A number (Unreal units, degrees...), stored in <see cref="BuilderSpec.Numbers"/>.</summary>
    Number,

    /// <summary>A whole number, stored as a number.</summary>
    Integer,

    /// <summary>On/off, stored as 0 or 1.</summary>
    Flag,

    /// <summary>One of a fixed list of words, stored in <see cref="BuilderSpec.Strings"/>.</summary>
    Choice,

    /// <summary>Free text (a material path), stored in <see cref="BuilderSpec.Strings"/>.</summary>
    Text,
}

/// <summary>One parameter of a builder: its name as stored in the spec, range and default (for the UI and scripts).</summary>
public sealed record BuilderParam(string Name, ParamType Type, string Summary)
{
    public double Default { get; init; }

    public double Min { get; init; } = double.NegativeInfinity;

    public double Max { get; init; } = double.PositiveInfinity;

    public IReadOnlyList<string> Choices { get; init; } = [];

    public string? DefaultText { get; init; }

    public bool IsText => Type is ParamType.Choice or ParamType.Text;

    public static BuilderParam Number(string name, double def, double min, double max, string summary) =>
        new(name, ParamType.Number, summary) { Default = def, Min = min, Max = max };

    public static BuilderParam Int(string name, int def, int min, int max, string summary) =>
        new(name, ParamType.Integer, summary) { Default = def, Min = min, Max = max };

    public static BuilderParam Flag(string name, bool def, string summary) =>
        new(name, ParamType.Flag, summary) { Default = def ? 1 : 0, Min = 0, Max = 1 };

    public static BuilderParam Choice(string name, string def, IReadOnlyList<string> choices, string summary) =>
        new(name, ParamType.Choice, summary) { DefaultText = def, Choices = choices };

    public static BuilderParam Text(string name, string def, string summary) =>
        new(name, ParamType.Text, summary) { DefaultText = def };

    /// <summary>The default as text, for help output.</summary>
    public string DefaultString => IsText ? DefaultText ?? "" : Default.ToString("0.###", CultureInfo.InvariantCulture);
}
