using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;

namespace OpenScctEditor.Panels;

/// <summary>One property bag a grid shows: its class (for the schema and defaults; empty when unknown), its stored
/// values and a key that names the same bag in every read and command (an object's kind and id).</summary>
internal readonly record struct PropOwner(string Class, PropSet Props, string Key = "");

/// <summary>Where a <see cref="PropertyGrid"/> reads values and how it writes them back (always as one command).</summary>
internal abstract class PropSource
{
    /// <summary>The bags being edited, read fresh from the project.</summary>
    public abstract IReadOnlyList<PropOwner> Owners(MapProject project);

    /// <summary>A command that gives every owner the value <paramref name="value"/> computes for it (null removes the
    /// stored value, so the class default applies).</summary>
    public abstract IEditCommand Set(MapProject project, string key, Func<PropOwner, SvJsonNode?> value);

    /// <summary>One command that gives every owner the values <paramref name="value"/> computes for several keys (a
    /// colour's hue, saturation and brightness), so they undo as one step. <paramref name="name"/> names the step.</summary>
    public abstract IEditCommand SetMany(MapProject project, string name, IReadOnlyList<string> keys, Func<PropOwner, string, SvJsonNode?> value);

    /// <summary>True when edits may apply while typing (the command merges into one undo step).</summary>
    public virtual bool Live => true;

    /// <summary>True when the grid offers an "add property" row (bags without a schema).</summary>
    public virtual bool AllowAdd => false;

    /// <summary>The fields to show when the bag is a struct the class catalog does not describe (a game mode's
    /// settings); null to use the owners' class schema.</summary>
    public virtual IReadOnlyList<PropInfo>? Schema => null;

    /// <summary>Properties the grid leaves out (the inspector edits the typed transform itself).</summary>
    public virtual IReadOnlySet<string> Skip { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The props of selected actors and brushes (brushes use the <c>Engine.Brush</c> schema).</summary>
internal sealed class ObjectPropSource : PropSource
{
    public const string BrushClass = "Engine.Brush";

    private static readonly HashSet<string> s_skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Location", "Rotation", "DrawScale", "DrawScale3D", "PrePivot", "CsgOper", "PolyFlags", "MainScale", "PostScale", "Brush",
    };

    private readonly IReadOnlyList<ObjectRef> _refs;

    public ObjectPropSource(IEnumerable<ObjectRef> refs) =>
        _refs = refs.Where(r => r.Kind is ObjectKind.Actor or ObjectKind.Brush or ObjectKind.Stair).ToList();

    public IReadOnlyList<ObjectRef> Refs => _refs;

    public override IReadOnlySet<string> Skip => s_skip;

    public override bool AllowAdd => true;

    public override IReadOnlyList<PropOwner> Owners(MapProject project)
    {
        var list = new List<PropOwner>(_refs.Count);
        var index = new ObjectIndex(project);
        foreach (ObjectRef r in _refs)
        {
            if (index.Actor(r) is { } a)
            {
                list.Add(new PropOwner(a.Class, a.Props, ActorKey(a.Id)));
            }
            else if (index.Brush(r) is { } b)
            {
                list.Add(new PropOwner(BrushClass, b.Props, BrushKey(b.Id)));
            }
        }

        return list;
    }

    public override IEditCommand Set(MapProject project, string key, Func<PropOwner, SvJsonNode?> value) =>
        ReplaceObjectsCommand.Edit(project, _refs, $"Set {key}",
            actor: a => a.Props[key] = value(new PropOwner(a.Class, a.Props, ActorKey(a.Id))),
            brush: b => b.Props[key] = value(new PropOwner(BrushClass, b.Props, BrushKey(b.Id))),
            mergeKey: "prop:" + key);

    public override IEditCommand SetMany(MapProject project, string name, IReadOnlyList<string> keys, Func<PropOwner, string, SvJsonNode?> value) =>
        ReplaceObjectsCommand.Edit(project, _refs, name,
            actor: a => SetAll(new PropOwner(a.Class, a.Props, ActorKey(a.Id)), keys, value),
            brush: b => SetAll(new PropOwner(BrushClass, b.Props, BrushKey(b.Id)), keys, value),
            mergeKey: "props:" + string.Join('+', keys));

    /// <summary>Sets every key of one owner, computing them all from the values it had before.</summary>
    internal static void SetAll(PropOwner owner, IReadOnlyList<string> keys, Func<PropOwner, string, SvJsonNode?> value)
    {
        SvJsonNode?[] values = keys.Select(k => value(owner, k)).ToArray();
        for (int i = 0; i < keys.Count; i++)
        {
            owner.Props[keys[i]] = values[i];
        }
    }

    private static string ActorKey(string id) => "actor:" + id;

    private static string BrushKey(string id) => "brush:" + id;
}

/// <summary>A project-level bag (LevelInfo, a game mode's map settings, map info strings). The selector must pick the
/// same bag every time (it also runs again on redo).</summary>
internal sealed class ProjectPropSource : PropSource
{
    private readonly string _class;
    private readonly string _label;
    private readonly Func<MapProject, PropSet?> _select;
    private readonly IReadOnlyList<PropInfo>? _schema;

    public ProjectPropSource(string className, string label, Func<MapProject, PropSet?> select, IReadOnlyList<PropInfo>? schema = null)
    {
        _class = className;
        _label = label;
        _select = select;
        _schema = schema;
    }

    public override IReadOnlyList<PropInfo>? Schema => _schema;

    public override bool Live => false;

    public override bool AllowAdd => true;

    public override IReadOnlyList<PropOwner> Owners(MapProject project) =>
        _select(project) is { } props ? [new PropOwner(_class, props)] : [];

    public override IEditCommand Set(MapProject project, string key, Func<PropOwner, SvJsonNode?> value) =>
        new ProjectSettingsCommand($"Set {_label} {key}", p =>
        {
            if (_select(p) is { } props)
            {
                props[key] = value(new PropOwner(_class, props));
            }
        });

    public override IEditCommand SetMany(MapProject project, string name, IReadOnlyList<string> keys, Func<PropOwner, string, SvJsonNode?> value) =>
        new ProjectSettingsCommand(name, p =>
        {
            if (_select(p) is { } props)
            {
                ObjectPropSource.SetAll(new PropOwner(_class, props), keys, value);
            }
        });
}
