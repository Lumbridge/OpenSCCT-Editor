using Godot;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Tools;

/// <summary>
/// The place tool's class list: a search box over the placeable classes, grouped by package and shown by their short
/// names (the full name in the tooltip), in a palette box under the button that opened it, capped at
/// <see cref="EditorTheme.PopupMaxHeight"/>. It is a layer over the main window, not a window of its own, so it sits
/// in the same theme, UI scale and coordinates as the button. Typing filters (every word must match), Down moves into the list,
/// Enter or a click picks, Escape or a click elsewhere closes.
/// </summary>
public sealed partial class ClassPicker : Control
{
    private const int Width = 300;

    private readonly IReadOnlyList<string> _classes;
    private readonly Action<string> _picked;
    private readonly PanelContainer _box = new() { ThemeTypeVariation = "Palette" };
    private readonly LineEdit _search;
    private readonly Tree _tree = new()
    {
        HideRoot = true,
        SelectMode = Tree.SelectModeEnum.Row,
        SizeFlagsVertical = SizeFlags.ExpandFill,
        ScrollHorizontalEnabled = false,
    };

    /// <summary>For Godot (script instancing needs a parameterless constructor).</summary>
    public ClassPicker()
        : this([], _ => { })
    {
    }

    public ClassPicker(IReadOnlyList<string> classes, Action<string> picked)
    {
        _classes = classes;
        _picked = picked;
        Name = "ClassPicker";
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.FullRect);

        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        _box.AddChild(stack);
        _search = PanelUi.Search(stack, "Search classes", Fill, "Every word must appear in the class name (package included)");
        _search.GuiInput += SearchKey;
        _search.TextSubmitted += _ => PickFirst();
        stack.AddChild(_tree);
        _tree.ItemMouseSelected += (_, _) => PickSelected();
        _tree.ItemActivated += PickSelected;
        AddChild(_box);
    }

    /// <summary>Opens the list under <paramref name="anchor"/>, with <paramref name="current"/> selected.</summary>
    public static void Open(Control anchor, IReadOnlyList<string> classes, string current, Action<string> picked)
    {
        var picker = new ClassPicker(classes, picked);
        // On the top control (the main window, which carries the theme), over everything in it.
        Control top = anchor;
        while (top.GetParent() is Control parent)
        {
            top = parent;
        }

        top.AddChild(picker);
        picker.Place(anchor);
        picker.Fill("");
        picker.Reveal(current);
        picker._search.GrabFocus();
    }

    public override void _GuiInput(InputEvent e)
    {
        // A click outside the box closes the list (the layer covers the window, so it gets those clicks).
        if (e is InputEventMouseButton { Pressed: true })
        {
            Close();
            AcceptEvent();
        }
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e.IsActionPressed("ui_cancel"))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private void Place(Control anchor)
    {
        var host = GetParent<Control>();
        Rect2 at = anchor.GetGlobalRect();
        at.Position -= host.GlobalPosition;
        Vector2 room = host.Size;
        float top = at.End.Y + EditorTheme.SpaceXxs;
        float height = Math.Min(EditorTheme.PopupMaxHeight, room.Y - top - EditorTheme.SpaceM);
        _box.Position = new Vector2(Mathf.Clamp(at.Position.X, EditorTheme.SpaceM, Math.Max(EditorTheme.SpaceM, room.X - Width - EditorTheme.SpaceM)), top);
        _box.Size = new Vector2(Width, Math.Max(height, EditorTheme.RowHeight * 4));
    }

    private void Fill(string filter)
    {
        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        string[] words = TextMatch.Words(filter);
        TreeItem? package = null;
        string? packageName = null;
        foreach (string name in _classes.Where(c => TextMatch.AllWords(words, c)).OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            int dot = name.IndexOf('.', StringComparison.Ordinal);
            string pkg = dot > 0 ? name[..dot] : "";
            if (package is null || !string.Equals(pkg, packageName, StringComparison.OrdinalIgnoreCase))
            {
                package = _tree.CreateItem(root);
                packageName = pkg;
                package.SetText(0, pkg.Length > 0 ? pkg : "(no package)");
                package.SetSelectable(0, false);
                package.SetCustomColor(0, EditorTheme.TextDim);
            }

            TreeItem item = _tree.CreateItem(package);
            item.SetText(0, dot > 0 ? name[(dot + 1)..] : name);
            item.SetTooltipText(0, name);
            item.SetMetadata(0, name);
            item.SetIcon(0, PanelIcons.Type(name));
        }

        if (root.GetChildCount() == 0)
        {
            TreeItem none = _tree.CreateItem(root);
            none.SetText(0, "No class matches");
            none.SetSelectable(0, false);
            none.SetCustomColor(0, EditorTheme.TextDim);
        }
    }

    /// <summary>Selects and shows the class placed now.</summary>
    private void Reveal(string current)
    {
        foreach (TreeItem package in _tree.GetRoot().GetChildren())
        {
            foreach (TreeItem item in package.GetChildren())
            {
                if (string.Equals(item.GetMetadata(0).AsString(), current, StringComparison.OrdinalIgnoreCase))
                {
                    item.Select(0);
                    _tree.ScrollToItem(item, centerOnItem: true);
                    return;
                }
            }
        }
    }

    private void SearchKey(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Down } && FirstLeaf() is { } first)
        {
            first.Select(0);
            _tree.GrabFocus();
            _search.AcceptEvent();
        }
        else if (e.IsActionPressed("ui_cancel"))
        {
            Close();
            _search.AcceptEvent();
        }
    }

    private TreeItem? FirstLeaf() => _tree.GetRoot().GetChildren().SelectMany(p => p.GetChildren()).FirstOrDefault();

    private void PickFirst()
    {
        if (FirstLeaf() is { } first)
        {
            Pick(first);
        }
    }

    private void PickSelected()
    {
        if (_tree.GetSelected() is { } item && item.GetMetadata(0).VariantType == Variant.Type.String)
        {
            Pick(item);
        }
    }

    private void Pick(TreeItem item)
    {
        _picked(item.GetMetadata(0).AsString());
        Close();
    }

    private void Close()
    {
        if (!IsQueuedForDeletion())
        {
            QueueFree();
        }
    }
}
