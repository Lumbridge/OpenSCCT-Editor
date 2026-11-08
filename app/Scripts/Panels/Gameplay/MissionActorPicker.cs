using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>A searchable actor chooser. Checked items survive filtering so a batch is one link/undo step.</summary>
internal sealed partial class MissionActorPicker : Control
{
    private readonly PanelContainer _box = new() { ThemeTypeVariation = "Palette" };
    private readonly Tree _list = new() { HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill, ScrollHorizontalEnabled = false };
    private readonly HashSet<string> _chosen = new(StringComparer.OrdinalIgnoreCase);
    private readonly LineEdit _search;
    private readonly Button _apply;
    private readonly IReadOnlyList<Actor> _actors;
    private readonly Action<IReadOnlyList<string>> _picked;
    private readonly Func<Actor, string> _detail;
    private readonly bool _multiple;
    private bool _filling;

    public MissionActorPicker() : this([], "Choose actor", false, _ => "", _ => { }) { }

    private MissionActorPicker(IReadOnlyList<Actor> actors, string title, bool multiple, Func<Actor, string> detail, Action<IReadOnlyList<string>> picked)
    {
        _actors = actors;
        _multiple = multiple;
        _detail = detail;
        _picked = picked;
        Name = "MissionActorPicker";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(_box);
        var stack = new VBoxContainer();
        _box.AddChild(stack);
        PanelUi.Header(stack, title);
        _search = PanelUi.Search(stack, "Search by name or type", Fill, "Search the map's existing actors. Every word must match.");
        _search.TextSubmitted += _ => Submit();
        _search.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Down } && _list.GetRoot()?.GetFirstChild() is { } first)
            { first.Select(0); _list.GrabFocus(); _search.AcceptEvent(); }
        };
        _list.Columns = 2;
        _list.SetColumnExpand(0, true);
        _list.SetColumnExpand(1, true);
        stack.AddChild(_list);
        _list.ItemEdited += () =>
        {
            if (_filling || _list.GetEdited() is not { } item) return;
            string id = item.GetMetadata(0).AsString();
            if (item.IsChecked(0)) _chosen.Add(id); else _chosen.Remove(id);
            RefreshApply();
        };
        _list.ItemActivated += Submit;
        _list.ItemSelected += () => { if (!_multiple) RefreshApply(); };
        var footer = PanelUi.Toolbar(stack);
        _apply = PanelUi.Primary(footer, multiple ? "Link chosen" : "Choose", "Apply the chosen actors", Submit);
        PanelUi.Button(footer, "Cancel", "Close without changing links", Close);
    }

    public static void Open(Control anchor, IReadOnlyList<Actor> actors, string title, bool multiple,
        Func<Actor, string> detail, Action<IReadOnlyList<string>> picked)
    {
        var picker = new MissionActorPicker(actors, title, multiple, detail, picked);
        Control host = anchor;
        while (host.GetParent() is Control parent) host = parent;
        host.AddChild(picker);
        float width = Math.Min(540, host.Size.X - EditorTheme.SpaceM * 2);
        float height = Math.Min(EditorTheme.PopupMaxHeight, host.Size.Y - EditorTheme.SpaceM * 2);
        Rect2 at = anchor.GetGlobalRect();
        Vector2 origin = at.Position - host.GlobalPosition;
        picker._box.Position = new Vector2(Mathf.Clamp(origin.X, EditorTheme.SpaceM, Math.Max(EditorTheme.SpaceM, host.Size.X - width - EditorTheme.SpaceM)),
            Mathf.Clamp(origin.Y - height - EditorTheme.SpaceXs, EditorTheme.SpaceM, host.Size.Y - height - EditorTheme.SpaceM));
        picker._box.Size = new Vector2(width, height);
        picker.Fill("");
        picker._search.GrabFocus();
    }

    private void Fill(string filter)
    {
        _filling = true;
        _list.Clear();
        TreeItem root = _list.CreateItem();
        string[] words = TextMatch.Words(filter);
        foreach (Actor actor in _actors.Where(a => TextMatch.AllWords(words, MissionLinks.Label(a) + " " + a.ShortClass + " " + _detail(a)))
            .OrderBy(MissionLinks.Name, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase))
        {
            TreeItem item = _list.CreateItem(root);
            if (_multiple)
            {
                item.SetCellMode(0, TreeItem.TreeCellMode.Check);
                item.SetEditable(0, true);
                item.SetChecked(0, _chosen.Contains(actor.Id));
            }
            item.SetText(0, MissionLinks.Label(actor));
            item.SetTooltipText(0, MissionLinks.Label(actor) + "\n" + actor.ShortClass);
            item.SetText(1, _detail(actor));
            item.SetTooltipText(1, _detail(actor));
            item.SetMetadata(0, actor.Id);
            if (!_multiple) item.SetIcon(0, PanelIcons.Type(actor.Class));
        }
        if (root.GetChildCount() == 0)
        {
            TreeItem none = _list.CreateItem(root);
            none.SetText(0, _actors.Count == 0 ? "No actors available to link" : "No matches");
            none.SetSelectable(0, false);
            none.SetSelectable(1, false);
        }
        _filling = false;
        RefreshApply();
    }

    private void RefreshApply()
    {
        _apply.Text = _multiple ? $"Link chosen ({_chosen.Count})" : "Choose";
        _apply.Disabled = _multiple ? _chosen.Count == 0 : _list.GetSelected()?.GetMetadata(0).VariantType != Variant.Type.String;
    }

    private void Submit()
    {
        IReadOnlyList<string> ids = _multiple ? _actors.Where(a => _chosen.Contains(a.Id)).Select(a => a.Id).ToList()
            : _list.GetSelected()?.GetMetadata(0).AsString() is { Length: > 0 } id ? [id] : [];
        if (ids.Count == 0) return;
        _picked(ids);
        Close();
    }

    private void Close() { if (!IsQueuedForDeletion()) QueueFree(); }
    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true }) { Close(); AcceptEvent(); }
    }
    public override void _Input(InputEvent e)
    {
        if (e.IsActionPressed("ui_cancel")) { Close(); GetViewport().SetInputAsHandled(); }
    }
}
