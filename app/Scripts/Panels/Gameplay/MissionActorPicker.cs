using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>A searchable actor chooser. Checked items survive filtering so a batch is one link/undo step.</summary>
internal sealed partial class MissionActorPicker : Control
{
    private static int s_openCount;
    internal static bool IsOpen => s_openCount > 0;
    private readonly PanelContainer _box = new() { ThemeTypeVariation = "Palette" };
    private readonly Tree _list = new() { HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill, ScrollHorizontalEnabled = false };
    private readonly EmptyStateView _empty;
    private readonly HashSet<string> _chosen = new(StringComparer.OrdinalIgnoreCase);
    private readonly LineEdit _search;
    private readonly Button _apply;
    private readonly IReadOnlyList<Actor> _actors;
    private readonly Action<IReadOnlyList<string>> _picked;
    private readonly Func<Actor, string> _detail;
    private readonly bool _multiple;
    private readonly Func<IReadOnlyList<string>, string>? _review;
    private readonly string _emptyHint;
    private readonly Label _title;
    private readonly TextEdit _reviewText;
    private readonly Button _back;
    private bool _reviewing;
    private IReadOnlyList<string> _pending = [];
    private bool _filling;

    public MissionActorPicker() : this([], "Choose actor", false, _ => "", _ => { }) { }

    private MissionActorPicker(IReadOnlyList<Actor> actors, string title, bool multiple, Func<Actor, string> detail, Action<IReadOnlyList<string>> picked,
        Func<IReadOnlyList<string>, string>? review = null, string emptyHint = "No actors available to link")
    {
        _actors = actors;
        _multiple = multiple;
        _detail = detail;
        _picked = picked;
        _review = review;
        _emptyHint = emptyHint;
        Name = "MissionActorPicker";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(_box);
        var stack = new VBoxContainer();
        _box.AddChild(stack);
        _title = PanelUi.Caption(stack, title);
        _title.MaxLinesVisible = 3;
        _title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _title.TooltipText = title;
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
        _empty = (EmptyStateView)PanelUi.EmptyState(_list, "link", "", "");
        _empty.Visible = false;
        _reviewText = new TextEdit { Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary,
            SizeFlagsVertical = SizeFlags.ExpandFill, Visible = false };
        stack.AddChild(_reviewText);
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
        _back = PanelUi.Button(footer, "Back", "Choose different actors", () =>
        {
            _reviewing = false;
            _search.Visible = _list.Visible = true;
            _reviewText.Visible = _back!.Visible = false;
            _title.Text = title;
            RefreshApply();
        });
        _back.Visible = false;
        PanelUi.Button(footer, "Cancel", "Close without changing links", Close);
    }

    public static void Open(Control anchor, IReadOnlyList<Actor> actors, string title, bool multiple,
        Func<Actor, string> detail, Action<IReadOnlyList<string>> picked,
        Func<IReadOnlyList<string>, string>? review = null, string emptyHint = "No actors available to link")
    {
        var picker = new MissionActorPicker(actors, title, multiple, detail, picked, review, emptyHint);
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
        if (anchor == host) picker._box.Position = (host.Size - picker._box.Size) * 0.5f;
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
        _empty.Visible = root.GetChildCount() == 0;
        if (_empty.Visible) _empty.SetText(_actors.Count == 0 ? "Nothing available to link" : "No matches",
            _actors.Count == 0 ? _emptyHint : "Try another name or type.");
        _filling = false;
        RefreshApply();
    }

    private void RefreshApply()
    {
        _apply.Text = _review is not null ? (_multiple ? $"Review links ({_chosen.Count})" : "Review link") : _multiple ? $"Link chosen ({_chosen.Count})" : "Choose";
        _apply.Disabled = _multiple ? _chosen.Count == 0 : _list.GetSelected()?.GetMetadata(0).VariantType != Variant.Type.String;
    }

    private void Submit()
    {
        if (_reviewing) { _picked(_pending); Close(); return; }
        IReadOnlyList<string> ids = _multiple ? _actors.Where(a => _chosen.Contains(a.Id)).Select(a => a.Id).ToList()
            : _list.GetSelected()?.GetMetadata(0).AsString() is { Length: > 0 } id ? [id] : [];
        if (ids.Count == 0) return;
        if (_review is not null)
        {
            _pending = ids;
            _reviewing = true;
            _search.Visible = _list.Visible = false;
            _reviewText.Text = _review(ids);
            _reviewText.Visible = _back.Visible = true;
            _title.Text = "2. Review links";
            _apply.Text = "Create links";
            _apply.Disabled = false;
            return;
        }
        _picked(ids);
        Close();
    }

    private void Close() { if (!IsQueuedForDeletion()) QueueFree(); }
    public override void _EnterTree() => s_openCount++;
    public override void _ExitTree() => s_openCount--;
    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true }) { Close(); AcceptEvent(); }
    }
    public override void _Input(InputEvent e)
    {
        if (e.IsActionPressed("ui_cancel")) { Close(); GetViewport().SetInputAsHandled(); }
        else if (!_reviewing && _multiple && _list.HasFocus() && e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Space }
            && _list.GetSelected() is { } item && item.GetMetadata(0).AsString() is { Length: > 0 } id)
        {
            bool check = !_chosen.Remove(id);
            if (check) _chosen.Add(id);
            item.SetChecked(0, check);
            RefreshApply();
            GetViewport().SetInputAsHandled();
        }
    }
}
