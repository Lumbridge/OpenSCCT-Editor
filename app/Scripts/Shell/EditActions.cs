using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Geometry.Builders;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Project;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell.Background;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Shell;

/// <summary>The Edit menu: undo/redo, clipboard, duplicate, delete and selection commands.</summary>
[ShellPart(Order = -40)]
public sealed class EditActions : IShellPart
{
    /// <summary>Format id of the clipboard JSON (scripts and other editor instances can read and write it).</summary>
    public const string ClipboardFormat = "osmap.clipboard";

    private EditorContext _ctx = null!;
    private string _localClipboard = "";

    public void Attach(MainWindow window, EditorContext context)
    {
        _ctx = context;
        bool HasSelection() => context.Selection.Count > 0;
        Add("edit.undo", "Undo", "Ctrl+Z", -110, Undo, () => context.Session.Commands.CanUndo);
        Add("edit.redo", "Redo", "Ctrl+Y", -102, Redo, () => context.Session.Commands.CanRedo);
        Add("edit.redoAlt", "Redo", "Ctrl+Shift+Z", -101, Redo, () => context.Session.Commands.CanRedo, hidden: true);
        Add("edit.cut", "Cut", "Ctrl+X", 100, Cut, HasSelection);
        Add("edit.copy", "Copy", "Ctrl+C", 110, Copy, HasSelection);
        Add("edit.paste", "Paste", "Ctrl+V", 120, () => Paste(null));
        Add("edit.pasteHere", "Paste Here", "Ctrl+Shift+V", 130, () => Paste(ViewportLayout.Instance?.CursorWorld));
        Add("edit.duplicate", "Duplicate", "Ctrl+D", 140, Duplicate, HasSelection);
        Add("edit.delete", "Delete", "Delete", 150, Delete, HasSelection);
        Add("edit.selectAll", "Select All", "Ctrl+A", 200, () => context.Selection.Set(ObjectFilter.Pickable(context.Project)));
        Add("edit.selectNone", "Select None", "Escape", 210, context.Selection.Clear, HasSelection);
        Add("edit.selectInvert", "Invert Selection", "Ctrl+Shift+I", 220, () =>
            context.Selection.Set(ObjectFilter.Pickable(context.Project).Where(r => !context.Selection.Contains(r)).ToList()));
        Add("edit.selectSameClass", "Select All of Same Class", "Ctrl+Shift+A", 230, SelectSameClass,
            () => context.Selection.Primary is { Kind: ObjectKind.Actor });
        Add("edit.selectSameType", "Select All of Same Kind", "", 240, SelectSameKind, HasSelection);
    }

    private void Add(string id, string title, string shortcut, int order, Action run, Func<bool>? enabled = null, bool hidden = false) =>
        _ctx.AddAction(new EditorAction(id, title, hidden ? "" : "Edit", run)
        {
            Shortcut = shortcut.Length > 0 ? shortcut : null,
            Order = order,
            IsEnabled = enabled ?? (() => true),
        });

    private void Undo()
    {
        string? name = _ctx.Session.Commands.UndoName;
        if (_ctx.Session.Commands.Undo())
        {
            _ctx.SetStatus($"Undid {name}");
        }
    }

    private void Redo()
    {
        string? name = _ctx.Session.Commands.RedoName;
        if (_ctx.Session.Commands.Redo())
        {
            _ctx.SetStatus($"Redid {name}");
        }
    }

    private void Copy()
    {
        string json = ToClipboardJson(_ctx.Project, _ctx.Selection.Items);
        _localClipboard = json;
        EditorClipboard.Set(json);

        _ctx.SetStatus($"Copied {_ctx.Selection.Count} {(_ctx.Selection.Count == 1 ? "object" : "objects")}");
    }

    private void Cut()
    {
        Copy();
        Delete();
    }

    private void Delete()
    {
        List<ObjectRef> refs = _ctx.Selection.Items.ToList();
        if (refs.Count > 0)
        {
            _ctx.Execute(PieceKeys.Delete(refs));
        }
    }

    /// <summary>Pastes the clipboard with fresh ids; at <paramref name="at"/> (cursor, grid snapped) when given, else
    /// where the objects were copied from.</summary>
    private void Paste(Vec3? at)
    {
        string text = EditorClipboard.Get();
        if (!text.Contains(ClipboardFormat, StringComparison.Ordinal))
        {
            text = _localClipboard;
        }

        if (text.Length == 0)
        {
            return;
        }

        try
        {
            (List<Brush> brushes, List<Actor> actors, List<Ge> ges) = FromClipboardJson(text, _ctx.Project, out List<string> copiedIds);
            if (at is { } target)
            {
                Vec3 delta = target - Anchor(brushes, actors, ges);
                Offset(brushes, actors, ges, delta);
            }

            // Copies of brushes imported with the baked world are new brushes; ones cut from this map are moved.
            AddAndSelect(at is null ? "Paste" : "Paste Here", brushes, actors, ges, BakedContent.Paste(_ctx.Project, brushes, copiedIds));
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or InvalidOperationException or ArgumentException
            or OpenScct.Content.SvMapFormatException)
        {
            _ctx.Log($"Paste: the clipboard holds no map objects ({ex.Message})", Severity.Warning);
        }
    }

    private void Duplicate()
    {
        (List<Brush> brushes, List<Actor> actors, List<Ge> ges) = FromClipboardJson(ToClipboardJson(_ctx.Project, _ctx.Selection.Items), _ctx.Project, out _);
        double step = Math.Max(_ctx.Project.Workspace.GridSize, 1);
        Offset(brushes, actors, ges, new Vec3(step, step, 0));
        // Copies of brushes imported with the baked world are new brushes: the baked world does not hold them.
        (int baked, int carving) = BakedContent.Unmark(brushes);
        AddAndSelect("Duplicate", brushes, actors, ges, BakedContent.CopyNote(_ctx.Project, baked, carving));
    }

    private void AddAndSelect(string name, List<Brush> brushes, List<Actor> actors, List<Ge> ges, Finding? note)
    {
        if (brushes.Count + actors.Count + ges.Count == 0)
        {
            return;
        }

        if (note is not null)
        {
            _ctx.Log(note.Message, note.Severity);
        }

        int count = brushes.Count + actors.Count + ges.Count;
        var cmd = new AddObjectsCommand($"{name} {count} {(count == 1 ? "object" : "objects")}", brushes, actors, ges);
        _ctx.Execute(cmd);
        _ctx.Selection.Set(cmd.Added);
    }

    private void SelectSameClass()
    {
        if (_ctx.Selection.Primary is not { Kind: ObjectKind.Actor } r || _ctx.Project.FindActor(r.Id) is not { } a)
        {
            return;
        }

        _ctx.Selection.Set(_ctx.Project.Actors
            .Where(x => string.Equals(x.Class, a.Class, StringComparison.OrdinalIgnoreCase) && ObjectFilter.IsPickable(_ctx.Project, x.Editor))
            .Select(x => ObjectRef.Actor(x.Id)));
    }

    /// <summary>Selects every object of the selection's kinds (all brushes, all actors or all GEs).</summary>
    private void SelectSameKind()
    {
        var kinds = _ctx.Selection.Items.Select(r => r.Kind).ToHashSet();
        _ctx.Selection.Set(ObjectFilter.Pickable(_ctx.Project).Where(r => kinds.Contains(r.Kind)));
    }

    /// <summary>The selection as clipboard JSON: <c>{"format":"osmap.clipboard","brushes":[..],"actors":[..],"ges":[..]}</c>.</summary>
    public static string ToClipboardJson(MapProject p, IEnumerable<ObjectRef> refs)
    {
        var brushes = new SvJsonArray();
        var actors = new SvJsonArray();
        var ges = new SvJsonArray();
        foreach (ObjectRef r in refs)
        {
            switch (r.Kind)
            {
                case ObjectKind.Brush or ObjectKind.Stair when p.FindBrush(r.Id) is { } b:
                    brushes.Add(ModelJson.BrushToJson(b));
                    break;
                case ObjectKind.Actor when p.FindActor(r.Id) is { } a:
                    actors.Add(ModelJson.ActorToJson(a));
                    break;
                case ObjectKind.Ge when p.FindGe(r.Id) is { } g:
                    ges.Add(ModelJson.GeToJson(g));
                    break;
            }
        }

        var root = new SvJsonObject();
        root.Add("format", SvJsonNode.String(ClipboardFormat));
        root.Add("brushes", brushes);
        root.Add("actors", actors);
        root.Add("ges", ges);
        return CanonicalJson.ToCanonicalString(root);
    }

    /// <summary>Reads clipboard JSON, giving every object an id no object of <paramref name="p"/> uses;
    /// <paramref name="copiedIds"/> are the brushes' ids when they were copied, in order.</summary>
    public static (List<Brush> Brushes, List<Actor> Actors, List<Ge> Ges) FromClipboardJson(string json, MapProject p, out List<string> copiedIds)
    {
        if (SvJson.Parse(json) is not SvJsonObject root || ModelJson.Str(root, "format") != ClipboardFormat)
        {
            throw new InvalidDataException("not osmap clipboard JSON");
        }

        var ids = new IdAllocator(p);
        string Fresh(string id) => ids.Next(id);

        IEnumerable<SvJsonObject> Items(string key) => root[key] is SvJsonArray a ? a.OfType<SvJsonObject>() : [];
        List<Brush> brushes = Items("brushes").Select(ModelJson.BrushFromJson).ToList();
        List<Actor> actors = Items("actors").Select(ModelJson.ActorFromJson).ToList();
        List<Ge> ges = Items("ges").Select(ModelJson.GeFromJson).ToList();
        copiedIds = brushes.Select(b => b.Id).ToList();
        var copyOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        brushes.ForEach(b => b.Id = copyOf[b.Id] = Fresh(b.Id));
        actors.ForEach(a => a.Id = copyOf[a.Id] = Fresh(a.Id));
        ges.ForEach(g => g.Id = Fresh(g.Id));

        // A pasted prefab part is a plain object, not a second copy of the part in its instance.
        foreach (EditorMeta m in brushes.Select(b => b.Editor).Concat(actors.Select(a => a.Editor)).Concat(ges.Select(g => g.Editor)))
        {
            m.UnlinkPrefab();
        }

        // Pasted parts of a multi-brush piece form a piece of their own, so rebuilding one never takes the other.
        PiecePlacer.RepointPieces(brushes, copyOf);
        return (brushes, actors, ges);
    }

    private static Vec3 Anchor(List<Brush> brushes, List<Actor> actors, List<Ge> ges) =>
        actors.Select(a => a.Location).Concat(brushes.Select(b => b.Location)).Concat(ges.SelectMany(g => g.Points.Take(1)))
            .DefaultIfEmpty(Vec3.Zero).First();

    private static void Offset(List<Brush> brushes, List<Actor> actors, List<Ge> ges, Vec3 delta)
    {
        brushes.ForEach(b => b.Location += delta);
        actors.ForEach(a =>
        {
            a.Location += delta;
            MoverBase.Shift(a, delta);
        });
        ges.ForEach(g => g.Points = g.Points.Select(x => x + delta).ToList());
    }
}
