using Godot;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>A Tree that hands drag and drop to its owner (the outliner and layers panels move objects by dragging rows
/// onto a layer, folder or group). It paints its background, the selected rows and the row under the pointer itself,
/// behind the cells, as one band per row across every column (the tree's own per-cell styles stop short of a cell's
/// buttons), and brightens the hovered row's eye and lock buttons (<see cref="RowStateButtons"/>).</summary>
public partial class OutlinerTree : Tree
{
    private TreeItem? _hot;
    private Backdrop? _band;

    public OutlinerTree()
    {
        ThemeTypeVariation = EditorTheme.RowTree;
    }

    /// <summary>Starts a drag at a position: returns the drag payload and its preview text, or null for no drag.</summary>
    public Func<Vector2, (Variant Data, string Preview)?>? DragStarting { get; set; }

    public Func<Vector2, Variant, bool>? CanDrop { get; set; }

    public Action<Vector2, Variant>? Dropped { get; set; }

    /// <summary>The column holding the rows' eye and lock buttons, brightened while the pointer is over the row; -1 for
    /// none.</summary>
    public int StateColumn { get; set; } = -1;

    /// <summary>Whether only the first column takes selection clicks in a multi-select tree: a click beside a row's
    /// buttons in another column selects nothing, so a row is selected through its first cell alone.</summary>
    public bool RowBands { get; set; }

    /// <summary>The row under the pointer, or null.</summary>
    public TreeItem? Hot => _hot is not null && IsInstanceValid(_hot) ? _hot : null;

    public override void _Ready()
    {
        _band = new Backdrop(this);
        AddChild(_band, @internal: InternalMode.Front);
        _band.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // Rows move whenever the tree redraws (scroll, expand, selection): the bands follow in the same frame.
        Draw += _band.QueueRedraw;
        GuiInput += e =>
        {
            if (RowBands && e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click
                && GetColumnAtPosition(click.Position) > 0 && GetButtonIdAtPosition(click.Position) < 0)
            {
                AcceptEvent();
                return;
            }

            if (e is InputEventMouse m)
            {
                // After the tree has handled the event: a wheel scroll moves the rows under the pointer.
                Vector2 at = m.Position;
                EditorLifetime.Defer(() =>
                {
                    if (IsInstanceValid(this))
                    {
                        SetHot(GetItemAtPosition(at));
                    }
                });
            }
        };
        MouseExited += () => SetHot(null);
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (DragStarting?.Invoke(atPosition) is not { } drag)
        {
            return default;
        }

        var preview = new Label { Text = drag.Preview };
        preview.AddThemeColorOverride("font_color", EditorTheme.Accent);
        SetDragPreview(preview);
        DropModeFlags = (int)DropModeFlagsEnum.OnItem;
        return drag.Data;
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) => CanDrop?.Invoke(atPosition, data) ?? false;

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        DropModeFlags = (int)DropModeFlagsEnum.Disabled;
        Dropped?.Invoke(atPosition, data);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationDragEnd)
        {
            DropModeFlags = (int)DropModeFlagsEnum.Disabled;
        }
    }

    /// <summary>Forgets the row under the pointer; call before <see cref="Tree.Clear"/> frees the rows.</summary>
    public void ForgetHot()
    {
        _hot = null;
        _band?.QueueRedraw();
    }

    private void SetHot(TreeItem? item)
    {
        TreeItem? old = Hot;
        _band?.QueueRedraw();
        if (old == item)
        {
            return;
        }

        _hot = item;
        if (StateColumn >= 0)
        {
            if (old is not null)
            {
                RowStateButtons.Refresh(old, StateColumn, hot: false);
            }

            if (item is not null)
            {
                RowStateButtons.Refresh(item, StateColumn, hot: true);
            }
        }

        _band?.QueueRedraw();
    }

    /// <summary>Every item below <paramref name="from"/> (depth first), or of the whole tree.</summary>
    public IEnumerable<TreeItem> Items(TreeItem? from = null)
    {
        TreeItem? start = from ?? GetRoot();
        if (start is null)
        {
            yield break;
        }

        var stack = new Stack<TreeItem>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            TreeItem item = stack.Pop();
            if (item != start || from is not null)
            {
                yield return item;
            }

            for (TreeItem? c = item.GetFirstChild(); c is not null; c = c.GetNext())
            {
                stack.Push(c);
            }
        }
    }

    /// <summary>The selected items, top to bottom.</summary>
    public List<TreeItem> SelectedItems()
    {
        var list = new List<TreeItem>();
        for (TreeItem? i = GetNextSelected(null); i is not null; i = GetNextSelected(i))
        {
            list.Add(i);
        }

        return list;
    }

    /// <summary>Draws behind the tree's cells: the tree's panel, then a band for each selected row and a wash over the
    /// row under the pointer, clipped to the panel's content area. It lets every click through.</summary>
    private sealed partial class Backdrop : Control
    {
        private readonly OutlinerTree _tree;

        public Backdrop()
            : this(null!)
        {
        }

        public Backdrop(OutlinerTree tree)
        {
            _tree = tree;
            MouseFilter = MouseFilterEnum.Ignore;
            ShowBehindParent = true;
        }

        public override void _Draw()
        {
            if (_tree is null)
            {
                return;
            }

            StyleBox panel = _tree.GetThemeStylebox(EditorTheme.RowTreePanel);
            var all = new Rect2(Vector2.Zero, Size);
            DrawStyleBox(panel, all);
            var content = new Rect2(
                panel.GetMargin(Side.Left), panel.GetMargin(Side.Top),
                Size.X - panel.GetMargin(Side.Left) - panel.GetMargin(Side.Right),
                Size.Y - panel.GetMargin(Side.Top) - panel.GetMargin(Side.Bottom));
            StyleBox selected = _tree.GetThemeStylebox(EditorTheme.RowTreeSelected);
            for (TreeItem? item = _tree.GetNextSelected(null); item is not null; item = _tree.GetNextSelected(item))
            {
                Band(item, content, r => DrawStyleBox(selected, r));
            }

            if (_tree.Hot is { } hot)
            {
                Band(hot, content, r => DrawRect(r, EditorTheme.HoverWash));
            }
        }

        private void Band(TreeItem item, Rect2 content, Action<Rect2> draw)
        {
            if (!item.IsVisibleInTree())
            {
                return;
            }

            // The rect spans every column: one band, not a highlighted cell.
            Rect2 row = _tree.GetItemAreaRect(item).Intersection(content);
            if (row.Size.X > 0 && row.Size.Y > 0)
            {
                draw(row);
            }
        }
    }
}
