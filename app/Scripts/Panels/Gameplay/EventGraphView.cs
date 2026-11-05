using Godot;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// The event graph around the selected actors, drawn as boxes and arrows: senders to the left, receivers to the
/// right, up to <see cref="Depth"/> links away. Fire links are solid, sender filters and plain references dashed, and
/// events no actor receives end in a red stub. Click a box to select its actor, double-click to frame it.
/// </summary>
public partial class EventGraphView : Control
{
    private const float ColumnWidth = 210, RowHeight = 30, BoxHeight = 22, Margin = 16;
    private const int MaxNodes = 120;

    // An actor's box: raised like a button, the selected actors with an accent outline.
    private static readonly StyleBoxFlat s_box = EditorTheme.Box(EditorTheme.PanelLight, EditorTheme.RadiusM, 1, EditorTheme.Border);
    private static readonly StyleBoxFlat s_focusBox = EditorTheme.Box(EditorTheme.Hover, EditorTheme.RadiusM, 1, EditorTheme.Accent);

    private readonly EditorContext _ctx;
    private readonly List<ActorBox> _nodes = new();
    private readonly List<(int From, int To, GameplayLink Link)> _edges = new();
    private readonly List<(int From, DanglingEvent Event)> _dangling = new();
    private string _note = "";
    private int _firstColumn;

    public EventGraphView()
    {
        _ctx = EditorContext.Instance;
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Stop;
        TooltipText = "Event graph";
    }

    /// <summary>How many links away from the selection to show.</summary>
    public int Depth { get; set; } = 1;

    /// <summary>Text filter (id, class, tag or event); empty shows every node in reach.</summary>
    public string Filter { get; set; } = "";

    /// <summary>Lays the graph out again from the current map and selection.</summary>
    public void Rebuild(GameplayGraph graph)
    {
        _nodes.Clear();
        _edges.Clear();
        _dangling.Clear();
        List<string> focus = _ctx.Selection.Items.Where(r => r.Kind == ObjectKind.Actor).Select(r => r.Id).Take(16).ToList();
        _note = focus.Count == 0 ? "Select actors to see their events here (Connections lists the whole map)." : "";
        var column = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in focus)
        {
            column[id] = 0;
        }

        // Breadth first: receivers one column right of their sender, senders one column left of their receiver.
        var frontier = new List<string>(focus);
        for (int step = 0; step < Depth && frontier.Count > 0 && column.Count < MaxNodes; step++)
        {
            var next = new List<string>();
            foreach (string id in frontier)
            {
                int c = column[id];
                foreach (GameplayLink l in graph.Out(id))
                {
                    Visit(l.To, c + 1);
                }

                foreach (GameplayLink l in graph.In(id))
                {
                    Visit(l.From, c - 1);
                }
            }

            frontier = next;

            void Visit(string id, int c)
            {
                if (column.Count < MaxNodes && column.TryAdd(id, c))
                {
                    next.Add(id);
                }
            }
        }

        if (column.Count >= MaxNodes)
        {
            _note = $"Showing the first {MaxNodes} actors: select fewer or lower the depth.";
        }

        MapProject p = _ctx.Project;
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<int, string> col in column.Where(kv => focus.Contains(kv.Key, StringComparer.OrdinalIgnoreCase) || Matches(p, graph, kv.Key))
                     .GroupBy(kv => kv.Value, kv => kv.Key).OrderBy(g => g.Key))
        {
            int row = 0;
            foreach (string id in col.Order(StringComparer.OrdinalIgnoreCase))
            {
                Actor? a = p.FindActor(id);
                index[id] = _nodes.Count;
                _nodes.Add(new ActorBox(id, a is null ? "(missing)" : $"{a.ShortClass} · tag {a.Tag}", col.Key, row++,
                    focus.Contains(id, StringComparer.OrdinalIgnoreCase)));
            }
        }

        foreach (ActorBox n in _nodes)
        {
            foreach (GameplayLink l in graph.Out(n.Id))
            {
                if (index.TryGetValue(l.To, out int to))
                {
                    _edges.Add((index[n.Id], to, l));
                }
            }

            foreach (DanglingEvent d in graph.Dangling.Where(d => string.Equals(d.From, n.Id, StringComparison.OrdinalIgnoreCase)))
            {
                _dangling.Add((index[n.Id], d));
            }
        }

        int minColumn = _nodes.Count == 0 ? 0 : _nodes.Min(n => n.Column);
        int columns = _nodes.Count == 0 ? 0 : _nodes.Max(n => n.Column) - minColumn + 1;
        int rows = _nodes.Count == 0 ? 0 : _nodes.GroupBy(n => n.Column).Max(g => g.Count()) + _dangling.Count;
        _firstColumn = minColumn;
        CustomMinimumSize = new Vector2(Margin * 2 + columns * ColumnWidth + 120, Margin * 2 + rows * RowHeight + 24);
        QueueRedraw();
    }

    public override void _Draw()
    {
        Font font = GetThemeDefaultFont();
        if (_note.Length > 0)
        {
            DrawString(font, new Vector2(Margin, Margin + 4), _note, fontSize: EditorTheme.FontSizeCaption, modulate: EditorTheme.TextDim);
        }

        foreach ((int from, int to, GameplayLink link) in _edges)
        {
            Rect2 a = RectOf(_nodes[from]), b = RectOf(_nodes[to]);
            Vector2 start = a.Position + new Vector2(a.Size.X, BoxHeight / 2), end = b.Position + new Vector2(0, BoxHeight / 2);
            if (_nodes[to].Column <= _nodes[from].Column)
            {
                // A link back or within a column: leave from the bottom, arrive at the bottom.
                start = a.GetCenter() + new Vector2(0, BoxHeight / 2);
                end = b.GetCenter() + new Vector2(0, BoxHeight / 2);
            }

            Color c = link.Kind switch
            {
                LinkKind.Fire => Viewports.Overlays.LinkOverlay.Fire,
                LinkKind.Accept => Viewports.Overlays.LinkOverlay.Incoming,
                _ => Viewports.Overlays.LinkOverlay.Reference,
            };
            if (link.Kind == LinkKind.Fire)
            {
                DrawLine(start, end, c, 1.5f, antialiased: true);
            }
            else
            {
                DrawDashedLine(start, end, c, 1.2f, 5);
            }

            Vector2 dir = (end - start).Normalized() * 8;
            DrawPolygon([end, end - dir + new Vector2(-dir.Y, dir.X) * 0.5f, end - dir - new Vector2(-dir.Y, dir.X) * 0.5f], [c]);
            DrawString(font, (start + end) / 2 + new Vector2(-30, -3), link.Event ?? Short(link.Via), fontSize: EditorTheme.FontSizeMicro, modulate: c);
        }

        int stubRow = 0;
        int lastRow = _nodes.Count == 0 ? 0 : _nodes.GroupBy(n => n.Column).Max(g => g.Count());
        foreach ((int from, DanglingEvent d) in _dangling)
        {
            Rect2 a = RectOf(_nodes[from]);
            var at = new Vector2(a.Position.X + 20, Margin + 24 + (lastRow + stubRow++) * RowHeight);
            DrawDashedLine(a.GetCenter() + new Vector2(0, BoxHeight / 2), at, EditorTheme.Error, 1.2f, 4);
            DrawString(font, at + new Vector2(4, 4), $"{d.Via} = {d.Event}: no actor has this tag", fontSize: EditorTheme.FontSizeCaption, modulate: EditorTheme.Error);
        }

        foreach (ActorBox n in _nodes)
        {
            Rect2 r = RectOf(n);
            DrawStyleBox(n.Focus ? s_focusBox : s_box, r);
            float baseline = r.Position.Y + (BoxHeight + font.GetAscent(EditorTheme.FontSizeCaption) - font.GetDescent(EditorTheme.FontSizeCaption)) / 2;
            DrawString(font, new Vector2(r.Position.X + EditorTheme.SpaceS, baseline), n.Id, width: r.Size.X - EditorTheme.SpaceS * 2,
                fontSize: EditorTheme.FontSizeCaption, modulate: n.Focus ? EditorTheme.TextBright : EditorTheme.Text);
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click ||
            _nodes.FirstOrDefault(n => RectOf(n).HasPoint(click.Position)) is not { } hit || _ctx.Project.FindActor(hit.Id) is null)
        {
            return;
        }

        ObjectRef[] refs = [ObjectRef.Actor(hit.Id)];
        if (click.DoubleClick)
        {
            _ctx.Focus(refs);
        }
        else
        {
            _ctx.Selection.Set(refs);
        }

        AcceptEvent();
    }

    public override string _GetTooltip(Vector2 atPosition) =>
        _nodes.FirstOrDefault(n => RectOf(n).HasPoint(atPosition)) is { } n ? $"{n.Id}\n{n.Detail}" : "";

    private Rect2 RectOf(ActorBox n) =>
        new(Margin + (n.Column - _firstColumn) * ColumnWidth, Margin + 24 + n.Row * RowHeight, ColumnWidth - 40, BoxHeight);

    private bool Matches(MapProject p, GameplayGraph g, string id)
    {
        if (Filter.Length == 0)
        {
            return true;
        }

        bool Has(string? s) => s is not null && s.Contains(Filter, StringComparison.OrdinalIgnoreCase);
        Actor? a = p.FindActor(id);
        return Has(id) || Has(a?.Class) || Has(a?.Tag) || g.Out(id).Any(l => Has(l.Event)) || g.In(id).Any(l => Has(l.Event));
    }

    private static string Short(string via) => via.Length > 22 ? via[..20] + "…" : via;

    private sealed record ActorBox(string Id, string Detail, int Column, int Row, bool Focus);
}
