using System.Text.RegularExpressions;
using Godot;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>The editor log: every line the editor logs with its severity icon and a dim time, severity chips with
/// counts, a filter, clear and copy. Long file paths show as their file names (the tooltip has the whole line). Keeps
/// the last 5000 lines.</summary>
[Panel("Log", DockSide.Bottom, Order = 1)]
public partial class LogPanel : EditorPanel
{
    private const int MaxLines = 5000;

    /// <summary>Old lines go in batches of this many, so a busy log does not rebuild the list for every new line.</summary>
    private const int TrimBatch = 500;

    private readonly List<LogLine> _lines = new();
    private readonly bool[] _show = [true, true, true];
    private readonly SeverityChip[] _chips = new SeverityChip[3];
    private Tree _list = null!;
    private TreeItem _root = null!;
    private string[] _words = [];
    private DeferredAction _scroll = null!;
    private DeferredAction _counts = null!;

    protected override void Build()
    {
        _scroll = new DeferredAction(() =>
        {
            if (_root.GetChildCount() > 0)
            {
                _list.ScrollToItem(_root.GetChild(-1));
            }
        });
        _counts = new DeferredAction(() =>
        {
            foreach (SeverityChip chip in _chips)
            {
                chip.Refresh();
            }
        });
        HFlowContainer bar = PanelUi.Toolbar(this);
        var chips = new HBoxContainer();
        chips.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        bar.AddChild(chips);
        foreach (Severity s in new[] { Severity.Error, Severity.Warning, Severity.Info })
        {
            int index = (int)s;
            _chips[index] = PanelUi.SeverityToggle(chips, s, () => _lines.Count(l => l.Severity == s), true, on =>
            {
                _show[index] = on;
                Rebuild();
            });
        }

        var end = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        end.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        bar.AddChild(end);
        PanelUi.Search(end, "Filter\u2026", t =>
        {
            _words = TextMatch.Words(t);
            Rebuild();
        }, "Show the lines that have every word typed");
        PanelUi.IconButton(end, "copy", "Copy the shown lines", () =>
        {
            PanelUi.Copy(string.Join('\n', _lines.Where(Shown).Select(l => l.ToString())));
            Context.SetStatus("Copied the log.");
        });
        PanelUi.IconButton(end, "trash", "Clear the log", () =>
        {
            _lines.Clear();
            Rebuild();
        }, danger: true);

        _list = new Tree
        {
            Name = "Lines",
            Columns = 2,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _list.SetColumnExpand(0, false);
        _list.Ready += () =>
        {
            // The time column fits "12:34:56" in the small mono font beside the first column's fold margin.
            float time = EditorTheme.MonoFont.GetStringSize("00:00:00", HorizontalAlignment.Left, -1, EditorTheme.FontSizeSmall).X;
            int margins = _list.GetThemeConstant("item_margin") + _list.GetThemeConstant("h_separation")
                + _list.GetThemeConstant("inner_item_margin_left") + _list.GetThemeConstant("inner_item_margin_right");
            _list.SetColumnCustomMinimumWidth(0, Mathf.CeilToInt(time) + margins);
        };
        _list.SetColumnClipContent(1, true);
        PanelUi.DimColumn(_list, 0);
        _root = _list.CreateItem();
        AddChild(_list);
        Context.Logged += OnLogged;
    }

    private void OnLogged(string text, Severity severity)
    {
        var line = new LogLine(DateTime.Now, text, severity);
        _lines.Add(line);
        _counts.Queue();
        if (_lines.Count > MaxLines + TrimBatch)
        {
            _lines.RemoveRange(0, _lines.Count - MaxLines);
            Rebuild();
            return;
        }

        if (Shown(line))
        {
            Add(line);
            _scroll.Queue();
        }
    }

    private bool Shown(LogLine l) => _show[(int)l.Severity] && (_words.Length == 0 || TextMatch.AllWords(_words, l.Text));

    private void Rebuild()
    {
        _list.Clear();
        _root = _list.CreateItem();
        foreach (LogLine l in _lines.Where(Shown))
        {
            Add(l);
        }

        _counts.Queue();
        _scroll.Queue();
    }

    private void Add(LogLine l)
    {
        TreeItem item = _list.CreateItem(_root);
        item.SetText(0, l.Time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        item.SetCustomColor(0, EditorTheme.TextDim);
        item.SetCustomFont(0, EditorTheme.MonoFont);
        item.SetCustomFontSize(0, EditorTheme.FontSizeSmall);
        item.SetIcon(1, PanelIcons.Severity(l.Severity));
        item.SetText(1, ShortPaths(l.Text));
        item.SetTooltipText(1, l.Text);
        item.SetTooltipText(0, l.Time.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A line with each full file path cut to its file name; the tooltip keeps the whole line.</summary>
    private static string ShortPaths(string text) => PathPattern().Replace(text, m =>
    {
        string path = m.Value.TrimEnd('.', ',', ';', ':', ')');
        string name = System.IO.Path.GetFileName(path.TrimEnd('/', '\\'));
        return (name.Length > 0 ? name : path) + m.Value[path.Length..];
    });

    /// <summary>A Windows path (C:\... or C:/...) or a Godot path (user://..., res://...), up to the next space or quote.</summary>
    [GeneratedRegex("""(?:[A-Za-z]:[\\/]|(?:user|res)://)[^\s"'<>|]+""")]
    private static partial Regex PathPattern();

    private sealed record LogLine(DateTime Time, string Text, Severity Severity)
    {
        public override string ToString() => $"{Time:HH:mm:ss}  {Text}";
    }
}
