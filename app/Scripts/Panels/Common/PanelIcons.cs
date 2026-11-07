using System.Text.RegularExpressions;
using Godot;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>The panels' small state and type icons (severity, eye, lock, object types), all from <see cref="Icons"/>.
/// Passive glyphs are dim at rest so the names next to them stay the brightest thing in a row; hidden and locked
/// states stand out by shape (and the lock by colour).</summary>
internal static partial class PanelIcons
{
    private static readonly Dictionary<string, string> s_typeIconNames = new(StringComparer.Ordinal);

    public static Texture2D Dot(Color color) => Icons.Get("dot", color)!;

    /// <summary>A finding's severity by shape and colour: a circled ×, a triangle with !, a circled i.</summary>
    public static Texture2D Severity(Severity s) => Icons.Get(s switch
    {
        OpenScct.Editor.Core.Services.Severity.Error => "error",
        OpenScct.Editor.Core.Services.Severity.Warning => "warning",
        _ => "info",
    }, PanelUi.SeverityColor(s))!;

    /// <summary>An eye: open (visible, dim) or crossed out (hidden).</summary>
    public static Texture2D Eye(bool open) =>
        open ? Icons.Get("eye", new Color(EditorTheme.TextDim, 0.6f))! : Icons.Get("eye-off", new Color(EditorTheme.TextDim, 0.45f))!;

    /// <summary>A padlock: closed (locked, in the warning colour) or open (unlocked, faint).</summary>
    public static Texture2D Lock(bool locked) =>
        locked ? Icons.Get("lock", EditorTheme.Warning)! : Icons.Get("lock-open", new Color(EditorTheme.TextDim, 0.35f))!;

    /// <summary>The type icon of an outliner row: brush, GE, or the actor's class.</summary>
    public static Texture2D Type(ObjectRow row) => row.Ref.Kind switch
    {
        ObjectKind.Brush or ObjectKind.Stair => TypeIcon("type-brush"),
        ObjectKind.Ge => TypeIcon(GeIconName(row.Class.StartsWith("GE ", StringComparison.Ordinal) ? row.Class[3..] : row.Class)),
        _ => Type(row.Class),
    };

    /// <summary>The <see cref="Icons"/> name of a GE kind's icon: a zip line or hand-over-hand line, a ledge, or a
    /// climbable (ladder, pipe, pole, fence).</summary>
    public static string GeIconName(string kind) => kind.ToUpperInvariant() switch
    {
        GeKinds.ZipLine or GeKinds.HandOverHand => "type-zipline",
        GeKinds.LedgeGrab => "traversal",
        _ => "type-ge",
    };

    /// <summary>The type icon of a class ("Engine.Light", "SBase.SCamera"), by what its name says it is; a generic
    /// actor mark for anything else.</summary>
    public static Texture2D Type(string className) => TypeIcon(TypeIconName(className));

    /// <summary>The <see cref="Icons"/> name of a class's type icon. Parts match whole CamelCase words of the class
    /// name (the last word of a part may start a longer word: "Tip" matches "SZoneTips"), so "Tip" never matches the
    /// "tiP" inside "AntiPortal" and "Pawn" never matches "Spawn".</summary>
    public static string TypeIconName(string className)
    {
        if (s_typeIconNames.TryGetValue(className, out string? known))
        {
            return known;
        }

        string[] words = Words(className[(className.LastIndexOf('.') + 1)..]);
        foreach ((string part, string icon) in s_typeNames)
        {
            if (HasWords(words, Words(part)))
            {
                return s_typeIconNames[className] = icon;
            }
        }

        return s_typeIconNames[className] = "type-actor";
    }

    /// <summary>The CamelCase words of a name: "SZoneTips" is S, Zone, Tips; "HUDText" is HUD, Text.</summary>
    private static string[] Words(string name) => [.. WordPattern().Matches(name).Select(m => m.Value)];

    [GeneratedRegex("[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+|[0-9]+")]
    private static partial Regex WordPattern();

    /// <summary>True when <paramref name="part"/> is a run of whole words of <paramref name="words"/>, its last word
    /// allowed to start a longer one.</summary>
    private static bool HasWords(string[] words, string[] part)
    {
        for (int start = 0; start + part.Length <= words.Length; start++)
        {
            bool match = true;
            for (int i = 0; i < part.Length && match; i++)
            {
                string word = words[start + i];
                match = i == part.Length - 1
                    ? word.StartsWith(part[i], StringComparison.OrdinalIgnoreCase)
                    : word.Equals(part[i], StringComparison.OrdinalIgnoreCase);
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Class-name parts and their icons, most specific first.</summary>
    private static readonly (string Part, string Icon)[] s_typeNames =
    [
        ("Brush", "type-brush"),
        ("PlayerStart", "type-start"),
        ("ZoneTip", "type-tip"),
        ("Tip", "type-tip"),
        ("HelpPath", "type-path"),
        ("MagicEvent", "type-event"),

        // Gameplay actors are often triggers or volumes by class (SLaserTrigger, SComputerObjectiveTrigger): what they
        // are wins over how they fire.
        ("Laser", "type-laser"),
        ("Computer", "type-computer"),
        ("Objective", "type-objective"),
        ("Mission", "type-objective"),
        ("Alarm", "type-alarm"),
        ("Camera", "type-camera"),
        ("Door", "type-door"),
        ("Conveyor", "type-conveyor"),
        ("Projector", "type-projector"),
        ("Trigger", "type-trigger"),
        ("Volume", "type-volume"),
        ("Mover", "type-mover"),
        ("Light", "type-light"),
        ("Spotlight", "type-light"),
        ("Sunlight", "type-light"),
        ("Sound", "type-sound"),
        ("Emitter", "type-emitter"),
        ("Particle", "type-emitter"),
        ("StaticMesh", "type-mesh"),
        ("Mesh", "type-mesh"),
        ("Portal", "type-zone"),
        ("Zone", "type-zone"),
        ("Ladder", "type-ge"),
        ("Path", "type-path"),
        ("Event", "type-event"),
        ("Note", "type-note"),
        ("Keypoint", "pin"),
        ("Pawn", "type-pawn"),
    ];

    private static Texture2D TypeIcon(string name) => Icons.Get(name, EditorTheme.TextDim)!;
}
