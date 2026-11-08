using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Scripting;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;

namespace OpenScctEditor.Shell;

/// <summary>
/// The command palette (Ctrl+Shift+P): actions, guided tutorials, controls, panels, classes, placeables and assets,
/// with favourites, recent results and navigation to the real UI. Type "@" to find objects by id, class or tag (Enter selects
/// and frames). Paste a change document (text starting with "{" or "[") to preview it as a ghost (Enter) or apply it
/// (Ctrl+Enter). F1 lists every shortcut. Up/Down move, Enter runs, Escape or clicking elsewhere closes; it never
/// blocks the window.
/// </summary>
[ShellPart(Order = 90)]
public sealed partial class CommandPalette : PanelContainer, IShellPart
{
    public static CommandPalette? Instance { get; private set; }
    private const int MaxRecent = 12;
    private const int MaxObjects = 150;
    private const string PreviewKey = "c:preview";
    private const string ApplyKey = "c:apply";

    private EditorContext _ctx = null!;
    private LineEdit _query = null!;
    private Tree _list = null!;
    private Label _hint = null!;
    private bool _shortcutsOnly;

    public void Attach(MainWindow window, EditorContext context)
    {
        Instance = this;
        _ctx = context;
        Name = "CommandPalette";
        ZIndex = 1100;
        Build();
        window.AddChild(this);

        context.AddAction(new EditorAction("view.commandPalette", "Command Palette", "View", () => Toggle(shortcutsOnly: false))
        {
            Shortcut = "Ctrl+Shift+P", Order = 920, Description = "Find commands, tutorials, controls, placeables, classes and assets; @ finds map objects",
        });
        context.AddAction(new EditorAction("help.shortcuts", "Keyboard Shortcuts", "Help", () => Toggle(shortcutsOnly: true))
        {
            Shortcut = "F1", Order = 110, Description = "Every shortcut, searchable",
        });
        context.AddAction(new EditorAction("help.about", "About OpenSCCT Editor", "Help", AboutDialog.Show)
        {
            Order = 900, Description = "Version, commit, Godot and .NET versions, project links and notices",
        });
    }

    private void Build()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.CenterTop);
        CustomMinimumSize = new Vector2(720, 500);
        OffsetLeft = -360;
        OffsetRight = 360;
        OffsetTop = 56;
        GrowHorizontal = GrowDirection.Both;
        ThemeTypeVariation = "Palette";

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", EditorTheme.SpaceS);
        AddChild(box);
        _query = new LineEdit { PlaceholderText = "Search anything… try laptop, tutorial, resize, or how do I add glass", ClearButtonEnabled = true, KeepEditingOnTextSubmit = true };
        _query.FocusEntered += () => _query.Edit();
        _query.TextChanged += _ => Populate();
        _query.GuiInput += OnQueryKey;
        _query.FocusExited += () => EditorLifetime.Defer(CloseIfFocusLeft);
        box.AddChild(_query);
        BuildDiscoveryUi(box);

        _list = new Tree
        {
            Columns = 2,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
        };
        _list.SetColumnExpand(1, false);
        _list.SetColumnCustomMinimumWidth(1, 90);
        _list.SetColumnClipContent(0, true);
        _list.ItemActivated += () => RunSelected(apply: false);
        box.AddChild(_list);
        BuildDiscoveryFooter(box);
        _hint = PanelUi.Caption(box, "", wrap: false);
        _hint.ClipText = true;
        _hint.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
    }

    public override void _Process(double delta)
    {
        if (!Visible || MainWindow.Instance is not { } window) return;
        float width = Math.Min(720, window.Size.X - 24);
        float height = Math.Min(500, window.Size.Y - 80);
        CustomMinimumSize = new Vector2(width, height);
        OffsetLeft = -width / 2;
        OffsetRight = width / 2;
        Size = new Vector2(width, height);
    }

    /// <summary>Opens the palette, or closes it when it is open in the same mode.</summary>
    private void Toggle(bool shortcutsOnly)
    {
        if (Visible && _shortcutsOnly == shortcutsOnly)
        {
            Close();
            return;
        }

        _shortcutsOnly = shortcutsOnly;
        DiscoveryNavigation.Invalidate();
        _favouritesOnly = false;
        _results.Clear();
        Reindex();
        // Last child draws on top and gets the clicks first, whatever the shell added after the palette.
        GetParent()?.MoveChild(this, -1);
        Visible = true;
        _query.Text = "";
        Populate();
        _query.GrabFocus();
    }

    private void Close()
    {
        Visible = false;
        _query.ReleaseFocus();
    }

    public void OpenSearch(string query)
    {
        if (!Visible) Toggle(shortcutsOnly: false);
        else { _shortcutsOnly = false; _favouritesOnly = false; Reindex(); }
        _query.Text = query;
        Populate();
        _query.GrabFocus();
        _query.CaretColumn = query.Length;
    }

    private void CloseIfFocusLeft()
    {
        Control? owner = GetViewport()?.GuiGetFocusOwner();
        if (Visible && (owner is null || !IsAncestorOf(owner)))
        {
            Close();
        }
    }

    private void OnQueryKey(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key)
        {
            return;
        }

        switch (key.Keycode)
        {
            case Key.D when key.IsCommandOrControlPressed():
                ToggleFavourite();
                break;
            case Key.Escape:
                Close();
                break;
            case Key.Enter or Key.KpEnter:
                if (key.AltPressed) LocateSelected();
                else RunSelected(apply: key.IsCommandOrControlPressed());
                break;
            case Key.Down:
                Move(1);
                break;
            case Key.Up:
                Move(-1);
                break;
            case Key.Pagedown:
                Move(10);
                break;
            case Key.Pageup:
                Move(-10);
                break;
            default:
                return;
        }

        _query.AcceptEvent();
    }

    /// <summary>Moves the selection by <paramref name="by"/> rows, over rows that cannot run now.</summary>
    private void Move(int by)
    {
        TreeItem? root = _list.GetRoot();
        if (root is null || root.GetChildCount() == 0)
        {
            return;
        }

        int count = root.GetChildCount();
        int index = _list.GetSelected()?.GetIndex() ?? -1;
        int next = Math.Clamp(index + by, 0, count - 1);
        int step = by > 0 ? 1 : -1;
        // Find a selectable row from the target onwards; failing that, back towards where the selection was.
        for (int i = next; i >= 0 && i < count; i += step)
        {
            if (root.GetChild(i).IsSelectable(0))
            {
                Select(root.GetChild(i));
                return;
            }
        }

        for (int i = next - step; i >= 0 && i < count && i != index; i -= step)
        {
            if (root.GetChild(i).IsSelectable(0))
            {
                Select(root.GetChild(i));
                return;
            }
        }
    }

    private void Select(TreeItem item)
    {
        item.Select(0);
        _list.ScrollToItem(item);
    }

    // ---- listing --------------------------------------------------------------------------------------------------

    private void Populate()
    {
        _list.Clear();
        TreeItem root = _list.CreateItem();
        string q = _query.Text.Trim();
        if (q.StartsWith('{') || q.StartsWith('['))
        {
            PopulateChanges(root);
        }
        else if (q.StartsWith('@'))
        {
            PopulateObjects(root, q[1..].Trim());
        }
        else
        {
            if (_shortcutsOnly) PopulateActions(root, q);
            else PopulateDiscovery(root, q);
        }

        FirstSelectable(root)?.Select(0);
        UpdateDetails();
    }

    private static TreeItem? FirstSelectable(TreeItem root)
    {
        for (TreeItem? item = root.GetFirstChild(); item is not null; item = item.GetNext())
        {
            if (item.IsSelectable(0))
            {
                return item;
            }
        }

        return null;
    }

    private void PopulateActions(TreeItem root, string q)
    {
        string[] recent = AppSettings.GetStrings("palette", "recent");
        var rows = new List<(EditorAction Action, int Score)>();
        foreach (EditorAction a in _ctx.Actions)
        {
            if (_shortcutsOnly && ShortcutBindings.Of(a) is null)
            {
                continue;
            }

            int r = Array.IndexOf(recent, a.Id);
            int recency = r < 0 || _shortcutsOnly ? 0 : (MaxRecent - r) * 4;
            if (q.Length == 0)
            {
                rows.Add((a, recency));
            }
            else if (TextMatch.FuzzyWords(q, $"{a.Title} {a.Menu} {a.Id} {a.Description} {ShortcutBindings.Of(a)}") is { } score)
            {
                rows.Add((a, score + recency));
            }
        }

        // Commands that can run now come first; the rest follow, dimmed and not selectable.
        IEnumerable<(EditorAction Action, int Score)> ordered = q.Length == 0
            ? rows.OrderByDescending(r => r.Action.IsEnabled()).ThenByDescending(r => r.Score).ThenBy(r => r.Action.Menu, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Action.Order)
                .ThenBy(r => r.Action.Title, StringComparer.OrdinalIgnoreCase)
            : rows.OrderByDescending(r => r.Action.IsEnabled()).ThenByDescending(r => r.Score).ThenBy(r => r.Action.Title, StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach ((EditorAction a, int _) in ordered)
        {
            TreeItem item = _list.CreateItem(root);
            string menu = a.Menu.Length > 0 ? a.Menu.Replace("/", " › ", StringComparison.Ordinal) + " › " : "";
            string title = a.Title.Replace("&&", "&", StringComparison.Ordinal);
            item.SetText(0, menu + title);
            item.SetText(1, ShortcutBindings.Text(a) ?? "");
            item.SetTextAlignment(1, HorizontalAlignment.Right);
            item.SetMetadata(0, "a:" + a.Id);
            item.SetTooltipText(0, a.Id + (Array.IndexOf(recent, a.Id) >= 0 ? " (recent)" : "") + (a.Description is { } d ? "\n" + d : ""));
            bool enabled = a.IsEnabled();
            item.SetCustomColor(0, enabled ? EditorTheme.Text : EditorTheme.TextDisabled);
            item.SetCustomColor(1, enabled ? EditorTheme.TextDim : EditorTheme.TextDisabled);
            item.SetSelectable(0, enabled);
            item.SetSelectable(1, enabled);
            DrawMatches(item, menu, title, q, enabled);
            count++;
        }

        _hint.Text = _shortcutsOnly
            ? $"{PanelUi.Count(count, "shortcut")} · type to filter · Enter runs · Esc closes"
            : $"{PanelUi.Count(count, "command")} · Enter runs · @ finds objects · paste a change document to preview it · Esc closes";
    }

    /// <summary>Draws an action's row itself: its menu path dim, its title in the text colour, and the parts the query
    /// matched bright and semibold, as command palettes do.</summary>
    private void DrawMatches(TreeItem item, string menu, string title, string query, bool enabled)
    {
        var parts = new List<(string Text, bool Match, bool Path)> { (menu, false, true) };
        string[] words = TextMatch.Words(query);
        var hit = new bool[title.Length];
        foreach (string w in words)
        {
            int at = title.IndexOf(w, StringComparison.OrdinalIgnoreCase);
            for (int i = at; at >= 0 && i < at + w.Length; i++)
            {
                hit[i] = true;
            }
        }

        for (int i = 0; i < title.Length;)
        {
            int end = i;
            while (end < title.Length && hit[end] == hit[i])
            {
                end++;
            }

            parts.Add((title[i..end], hit[i], false));
            i = end;
        }

        item.SetCellMode(0, TreeItem.TreeCellMode.Custom);
        item.SetText(0, "");
        item.SetCustomDrawCallback(0, Callable.From((TreeItem drawn, Rect2 rect) =>
        {
            Font font = _list.GetThemeFont("font");
            int size = _list.GetThemeFontSize("font_size");
            bool selected = drawn.IsSelected(0);
            float x = rect.Position.X + _list.GetThemeConstant("inner_item_margin_left");
            float right = rect.End.X - _list.GetThemeConstant("inner_item_margin_right");
            float baseline = rect.Position.Y + (rect.Size.Y - font.GetHeight(size)) / 2 + font.GetAscent(size);
            foreach ((string text, bool match, bool path) in parts)
            {
                if (text.Length == 0 || x >= right)
                {
                    continue;
                }

                Font f = match ? EditorTheme.UiFontSemibold : font;
                Color c = !enabled ? EditorTheme.TextDisabled
                    : path ? EditorTheme.TextDim
                    : match || selected ? EditorTheme.TextBright
                    : EditorTheme.Text;
                _list.DrawString(f, new Vector2(x, baseline), text, HorizontalAlignment.Left, right - x, size, c);
                x += f.GetStringSize(text, fontSize: size).X;
            }
        }));
    }

    private void PopulateObjects(TreeItem root, string q)
    {
        MapProject p = _ctx.Project;
        var matches = ObjectInfo.All(p)
            .Select(o => (Row: o, Score: q.Length == 0 ? 0 : TextMatch.FuzzyWords(q, $"{o.Ref.Id} {o.Class} {o.Tag}")))
            .Where(x => x.Score is not null)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Row.Ref.Id, StringComparer.OrdinalIgnoreCase)
            .Take(MaxObjects).ToList();
        foreach ((ObjectRow row, int? _) in matches)
        {
            TreeItem item = _list.CreateItem(root);
            item.SetText(0, row.Tag.Length > 0 && row.Tag != row.Class ? $"{row.Ref.Id}  ·  {row.Tag}" : row.Ref.Id);
            item.SetText(1, row.Class);
            item.SetTextAlignment(1, HorizontalAlignment.Right);
            item.SetCustomColor(1, EditorTheme.TextDim);
            item.SetMetadata(0, $"o:{(int)row.Ref.Kind}:{row.Ref.Id}");
        }

        _hint.Text = $"{PanelUi.Count(matches.Count, "object")}{(matches.Count == MaxObjects ? " (first " + MaxObjects + ")" : "")} · Enter selects and frames";
    }

    private void PopulateChanges(TreeItem root)
    {
        TreeItem preview = _list.CreateItem(root);
        preview.SetText(0, "Preview this change document as a ghost");
        preview.SetText(1, "Enter");
        preview.SetMetadata(0, PreviewKey);
        TreeItem apply = _list.CreateItem(root);
        apply.SetText(0, "Apply this change document now (one undo step)");
        apply.SetText(1, "Ctrl+Enter");
        apply.SetMetadata(0, ApplyKey);
        foreach (TreeItem item in new[] { preview, apply })
        {
            item.SetTextAlignment(1, HorizontalAlignment.Right);
            item.SetCustomColor(1, EditorTheme.TextDim);
        }

        _hint.Text = "osmap.changes/1 · the preview shows Accept and Reject over the views";
    }

    // ---- running --------------------------------------------------------------------------------------------------

    private void RunSelected(bool apply)
    {
        string text = _query.Text.Trim();
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            bool applyNow = apply || _list.GetSelected()?.GetMetadata(0).AsString() == ApplyKey;
            Close();
            LiveEditing.Instance?.RunChangeDocument(text, applyNow);
            return;
        }

        if (_list.GetSelected()?.GetMetadata(0).AsString() is not { Length: > 2 } key)
        {
            return;
        }

        if (!_shortcutsOnly && !text.StartsWith('@') && RunDiscovery(key)) return;

        Close();
        if (key.StartsWith("o:", StringComparison.Ordinal))
        {
            string[] parts = key.Split(':', 3);
            var r = new ObjectRef((ObjectKind)int.Parse(parts[1], CultureInfo.InvariantCulture), parts[2]);
            _ctx.Selection.Set([r]);
            _ctx.Focus([r]);
            return;
        }

        string id = key[2..];
        if (_ctx.FindAction(id) is not { } action)
        {
            return;
        }

        Remember(id);
        if (!_ctx.Run(id))
        {
            _ctx.SetStatus($"{action.Title} is not available right now.");
        }
    }

    private static void Remember(string id)
    {
        string[] recent = AppSettings.GetStrings("palette", "recent");
        AppSettings.Set("palette", "recent", recent.Where(r => r != id).Prepend(id).Take(MaxRecent).ToArray());
    }
}
