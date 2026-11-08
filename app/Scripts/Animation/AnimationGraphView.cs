using Godot;
using OpenScctEditor.Panels;
using OpenScct.Animation;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Animation;

public partial class AnimationGraphView : VBoxContainer
{
    private readonly StudioState _state;
    private string? _graphId;
    private string? _selectedNode;
    private GraphEdit _view = null!;
    private VBoxContainer _details = null!;
    private OptionButton _graphs = null!;
    private Label _live = null!;
    private bool _updating;
    private bool _statesView;
    private readonly Dictionary<string, string> _stateNodes = new(StringComparer.Ordinal);
    public AnimationGraphView(StudioState state) => _state = state;
    private AnimationGraph? Graph => _state.Project.Graphs.FirstOrDefault(g => g.Id == _graphId);
    public override void _Ready()
    {
        HFlowContainer tools = PanelUi.Toolbar(this);
        _graphs = new OptionButton(); tools.AddChild(_graphs);
        _graphs.ItemSelected += i => { if (!_updating) { _graphId = _graphs.GetItemMetadata((int)i).AsString(); Refresh(); } };
        PanelUi.Button(tools, "+ Graph", "Create a graph from the selected clip", NewGraph);
        PanelUi.Toggle(tools, "States", "Show states and connect transition arrows", false, value => { _statesView = value; Refresh(); });
        foreach (GraphNodeKind kind in Enum.GetValues<GraphNodeKind>()) PanelUi.Button(tools, "+ " + kind, "Add a node", () => AddNode(kind));
        PanelUi.Button(tools, "Preview", "Run this graph with editable parameters", () => { if (Graph is { } graph) { _state.GraphPlayer = new(_state.Project, graph); _state.Playing = true; Parameters(); } });
        _live = PanelUi.Caption(tools, "", false);
        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; AddChild(split);
        _view = new GraphEdit { Name = "AnimationBlendGraph", SizeFlagsHorizontal = SizeFlags.ExpandFill, RightDisconnects = true }; split.AddChild(_view);
        _details = PanelUi.Scroll(split); PanelUi.ScrollOf(_details).CustomMinimumSize = new(220, 0);
        _view.ConnectionRequest += (from, _, to, _) => Edit("Connect animation nodes", graph =>
        {
            if (_statesView)
            {
                string parameter = graph.Parameters.FirstOrDefault()?.Name ?? "Go";
                if (graph.Parameters.Count == 0) graph.Parameters.Add(new() { Name = parameter, Kind = ParameterKind.Trigger });
                graph.Transitions.Add(new() { From = _stateNodes[from.ToString()], To = _stateNodes[to.ToString()], Parameter = parameter, Operator = graph.Parameters[0].Kind == ParameterKind.Trigger ? ConditionOperator.Triggered : ConditionOperator.Greater });
                return;
            }
            OpenScct.Animation.GraphNode target = graph.Nodes.Single(n => n.Id == to.ToString());
            if (target.Kind == GraphNodeKind.Clip) throw new InvalidDataException("Clip nodes have no inputs.");
            target.Inputs.Add(new() { Node = from.ToString(), Point = new(target.Inputs.Count, 0) });
        });
        _view.DisconnectionRequest += (from, _, to, _) => Edit("Disconnect animation nodes", graph =>
        {
            if (_statesView) graph.Transitions.RemoveAll(t => t.From == _stateNodes[from.ToString()] && t.To == _stateNodes[to.ToString()]);
            else graph.Nodes.Single(n => n.Id == to.ToString()).Inputs.RemoveAll(i => i.Node == from.ToString());
        });
        _view.NodeSelected += node => { string id = node.Name.ToString(); _selectedNode = _statesView && Graph is { } graph ? graph.States.Single(s => s.Name == _stateNodes[id]).Node : id; Details(); };
        _state.Changed += Refresh; _state.TimeChanged += Live;
        TreeExiting += () => { _state.Changed -= Refresh; _state.TimeChanged -= Live; };
        Refresh();
    }
    private void NewGraph()
    {
        if (_state.CurrentClip is not { } clip) return;
        var node = new OpenScct.Animation.GraphNode { Clip = clip.Id, Position = new(80, 80) };
        var graph = new AnimationGraph { Skeleton = clip.Skeleton, Nodes = [node], States = [new() { Name = "Idle", Node = node.Id }], Parameters = [new() { Name = "Speed" }] };
        _graphId = graph.Id; _state.Session.Edit("Create animation graph", p => p.Graphs.Add(graph));
    }
    private void AddNode(GraphNodeKind kind)
    {
        if (Graph is null || _state.CurrentClip is null) return;
        Edit("Add animation node", graph => graph.Nodes.Add(new() { Kind = kind, Clip = kind == GraphNodeKind.Clip ? _state.ClipId : null, Position = new(80 + graph.Nodes.Count * 180, 80) }));
    }
    private void Refresh()
    {
        if (!IsNodeReady()) return;
        _updating = true;
        if (Graph is null) _graphId = _state.Project.Graphs.FirstOrDefault()?.Id;
        _graphs.Clear();
        foreach (AnimationGraph graph in _state.Project.Graphs) { _graphs.AddItem(graph.Name); int i = _graphs.ItemCount - 1; _graphs.SetItemMetadata(i, graph.Id); if (graph.Id == _graphId) _graphs.Selected = i; }
        _view.ClearConnections();
        _stateNodes.Clear();
        foreach (Node child in _view.GetChildren().Where(n => n is Godot.GraphNode).ToArray()) { _view.RemoveChild(child); child.QueueFree(); }
        if (Graph is { } selected)
        {
            if (_statesView)
            {
                for (int i = 0; i < selected.States.Count; i++)
                {
                    GraphState state = selected.States[i]; string name = "state_" + i; string stateName = state.Name; _stateNodes[name] = stateName;
                    var visual = new Godot.GraphNode { Name = name, Title = stateName, PositionOffset = state.Position == System.Numerics.Vector2.Zero ? new(80 + i * 190, 80) : new(state.Position.X, state.Position.Y), CustomMinimumSize = new(155, 65) };
                    _view.AddChild(visual); visual.AddChild(new Label { Text = selected.Entry == stateName ? "Entry state" : "Animation state" });
                    visual.SetSlot(0, true, 0, EditorTheme.Accent, true, 0, EditorTheme.Accent);
                    visual.Dragged += (_, position) => Edit("Move animation state", graph => graph.States.Single(s => s.Name == stateName).Position = new(position.X, position.Y));
                }
                foreach (GraphTransition transition in selected.Transitions)
                    _view.ConnectNode(_stateNodes.First(n => n.Value == transition.From).Key, 0, _stateNodes.First(n => n.Value == transition.To).Key, 0);
            }
            else foreach (OpenScct.Animation.GraphNode node in selected.Nodes)
            {
                string id = node.Id;
                var visual = new Godot.GraphNode { Name = id, Title = node.Kind == GraphNodeKind.Clip ? _state.Project.Clips.FirstOrDefault(c => c.Id == node.Clip)?.Name ?? "Clip" : node.Kind.ToString(), PositionOffset = new(node.Position.X, node.Position.Y), CustomMinimumSize = new(155, 65) };
                _view.AddChild(visual);
                visual.AddChild(new Label { Text = node.Kind == GraphNodeKind.Clip ? "Clip → pose" : "Pose inputs → blend" });
                visual.SetSlot(0, node.Kind != GraphNodeKind.Clip, 0, EditorTheme.Accent, true, 0, EditorTheme.Accent);
                visual.Dragged += (_, position) => Edit("Move graph node", graph => graph.Nodes.Single(n => n.Id == id).Position = new(position.X, position.Y));
            }
            if (!_statesView) foreach (OpenScct.Animation.GraphNode node in selected.Nodes)
                foreach (GraphInput input in node.Inputs) _view.ConnectNode(input.Node, 0, node.Id, 0);
        }
        _updating = false; Details();
    }
    private void Details()
    {
        foreach (Node child in _details.GetChildren()) { _details.RemoveChild(child); child.QueueFree(); }
        if (Graph is not { } graph) { PanelUi.Caption(_details, "Select a clip and create a graph."); return; }
        PanelUi.Header(_details, "State machine");
        var stateName = new LineEdit { PlaceholderText = "New state name" }; _details.AddChild(stateName);
        PanelUi.Button(_details, "State from selected node", "Create a state with this node as its output", () =>
        {
            if (_selectedNode is { } id) Edit("Add animation state", g => g.States.Add(new() { Name = stateName.Text, Node = id }));
        });
        OptionButton entry = Options(_details, "Entry state", graph.States.Select(s => s.Name), graph.Entry);
        entry.ItemSelected += i => Edit("Graph entry state", g => g.Entry = entry.GetItemText((int)i));
        if (graph.Nodes.FirstOrDefault(n => n.Id == _selectedNode) is { } node)
        {
            PanelUi.Header(_details, node.Kind.ToString());
            if (node.Kind == GraphNodeKind.Clip)
            {
                var clips = _state.Project.Clips.Where(c => c.Skeleton == graph.Skeleton).ToArray();
                OptionButton clipChoice = Options(_details, "Clip", clips.Select(c => c.Name), clips.FirstOrDefault(c => c.Id == node.Clip)?.Name);
                clipChoice.ItemSelected += i => Edit("Graph clip", g => g.Nodes.Single(n => n.Id == node.Id).Clip = clips[(int)i].Id);
            }
            var parameter = new LineEdit { Text = node.Parameter, PlaceholderText = "X / weight parameter" }; _details.AddChild(parameter);
            parameter.TextSubmitted += value => Edit("Graph node parameter", g => g.Nodes.Single(n => n.Id == node.Id).Parameter = value);
            var parameterY = new LineEdit { Text = node.ParameterY, PlaceholderText = "Y parameter" }; _details.AddChild(parameterY);
            parameterY.TextSubmitted += value => Edit("Graph Y parameter", g => g.Nodes.Single(n => n.Id == node.Id).ParameterY = value);
            SpinBox weight = PanelUi.Spin(_details, 0, 1, .05, node.Weight, "Layer weight"); weight.ValueChanged += value => Edit("Graph layer weight", g => g.Nodes.Single(n => n.Id == node.Id).Weight = (float)value);
            var mask = new LineEdit { Text = string.Join(',', node.Mask), PlaceholderText = "Mask bones (comma separated)" }; _details.AddChild(mask);
            mask.TextSubmitted += value => Edit("Graph bone mask", g => g.Nodes.Single(n => n.Id == node.Id).Mask = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList());
            for (int i = 0; i < node.Inputs.Count; i++)
            {
                int index = i; GraphInput input = node.Inputs[i];
                PanelUi.Caption(_details, "Input " + (i + 1));
                SpinBox x = PanelUi.Spin(_details, -10000, 10000, .1, input.Point.X, "Blend point X");
                SpinBox y = PanelUi.Spin(_details, -10000, 10000, .1, input.Point.Y, "Blend point Y");
                x.ValueChanged += value => Edit("Blend point X", g => g.Nodes.Single(n => n.Id == node.Id).Inputs[index].Point = new((float)value, input.Point.Y));
                y.ValueChanged += value => Edit("Blend point Y", g => g.Nodes.Single(n => n.Id == node.Id).Inputs[index].Point = new(input.Point.X, (float)value));
            }
        }
        PanelUi.Header(_details, "Parameters");
        var name = new LineEdit { PlaceholderText = "Parameter name" }; _details.AddChild(name);
        OptionButton kind = Options(_details, "Type", Enum.GetNames<ParameterKind>(), "Float");
        PanelUi.Button(_details, "Add parameter", "Add a typed graph parameter", () => Edit("Add graph parameter", g => g.Parameters.Add(new() { Name = name.Text, Kind = (ParameterKind)kind.Selected })));
        foreach (GraphParameter p in graph.Parameters)
        {
            PanelUi.Caption(_details, p.Name + " · " + p.Kind);
            SpinBox value = PanelUi.Spin(_details, p.Kind is ParameterKind.Boolean or ParameterKind.Trigger ? 0 : -10000, p.Kind is ParameterKind.Boolean or ParameterKind.Trigger ? 1 : 10000, p.Kind == ParameterKind.Float ? .1 : 1,
                _state.GraphPlayer?.Capture().Parameters.GetValueOrDefault(p.Name, p.Default) ?? p.Default, "Preview parameter");
            value.ValueChanged += v => _state.GraphPlayer?.Set(p.Name, (float)v);
        }
        PanelUi.Header(_details, "Transitions");
        OptionButton from = Options(_details, "From", graph.States.Select(s => s.Name), null);
        OptionButton to = Options(_details, "To", graph.States.Select(s => s.Name), null);
        OptionButton condition = Options(_details, "Parameter", graph.Parameters.Select(p => p.Name), null);
        OptionButton op = Options(_details, "Condition", Enum.GetNames<ConditionOperator>(), "Greater");
        SpinBox compare = PanelUi.Spin(_details, -10000, 10000, .1, 0, "Comparison value");
        SpinBox blend = PanelUi.Spin(_details, 0, 10, .01, .15, "Transition blend seconds");
        PanelUi.Button(_details, "Add transition", "First matching transition wins in list order", () => Edit("Add graph transition", g => g.Transitions.Add(new()
        { From = from.Text, To = to.Text, Parameter = condition.Text, Operator = (ConditionOperator)op.Selected, Value = (float)compare.Value, BlendSeconds = (float)blend.Value })));
        for (int i = 0; i < graph.Transitions.Count; i++)
        {
            int index = i; GraphTransition transition = graph.Transitions[i];
            PanelUi.Caption(_details, $"{transition.From} → {transition.To}");
            var parameter = Options(_details, "Condition", graph.Parameters.Select(p => p.Name), transition.Parameter);
            parameter.ItemSelected += value => Edit("Transition parameter", g => g.Transitions[index].Parameter = parameter.GetItemText((int)value));
            var operation = Options(_details, "Compare", Enum.GetNames<ConditionOperator>(), transition.Operator.ToString());
            operation.ItemSelected += value => Edit("Transition comparison", g => g.Transitions[index].Operator = (ConditionOperator)value);
            PanelUi.Spin(_details, -10000, 10000, .1, transition.Value, "Comparison value").ValueChanged += value => Edit("Transition threshold", g => g.Transitions[index].Value = (float)value);
            PanelUi.Spin(_details, 0, 60, .01, transition.BlendSeconds, "Blend seconds").ValueChanged += value => Edit("Transition blend", g => g.Transitions[index].BlendSeconds = (float)value);
            PanelUi.Spin(_details, 0, 3600, .01, transition.ExitTime, "Earliest exit time in seconds").ValueChanged += value => Edit("Transition exit time", g => g.Transitions[index].ExitTime = (float)value);
            PanelUi.Button(_details, "Remove transition", "Delete this transition", () => Edit("Remove transition", g => g.Transitions.RemoveAt(index)));
        }
        var target = new LineEdit { PlaceholderText = "actor:ActorId" }; _details.AddChild(target);
        PanelUi.Button(_details, "Bind graph to actor", "Graph controls custom animated actors", () => _state.Session.Edit("Bind animation graph", p => p.Bindings.Add(new() { Target = target.Text, Slot = "graph", Graph = graph.Id, Mode = ReplacementMode.Gameplay })));
    }
    private void Parameters() => Details();
    private void Live()
    {
        if (_state.GraphPlayer is not { } player) return;
        var weights = player.InputWeights();
        _live.Text = $"{player.State} · {player.Time:0.00}s · " + string.Join(" · ", Graph?.Nodes.Where(n => n.Kind == GraphNodeKind.Clip && weights.ContainsKey(n.Id)).Select(n => $"{_state.Project.Clips.First(c => c.Id == n.Clip).Name} {weights[n.Id]:0.00}") ?? []);
        if (_statesView) foreach (Godot.GraphNode node in _view.GetChildren().OfType<Godot.GraphNode>()) node.Modulate = _stateNodes.GetValueOrDefault(node.Name.ToString()) == player.State ? EditorTheme.Accent : Colors.White;
    }
    private void Edit(string name, Action<AnimationGraph> edit)
    {
        if (_graphId is not { } id) return;
        try { _state.Session.Edit(name, p => edit(p.Graphs.Single(g => g.Id == id))); }
        catch (InvalidDataException e) { _live.Text = e.Message; }
    }
    private static OptionButton Options(Control parent, string label, IEnumerable<string> values, string? selected)
    {
        PanelUi.Caption(parent, label); var options = new OptionButton(); parent.AddChild(options);
        foreach (string value in values) { options.AddItem(value); if (value == selected) options.Selected = options.ItemCount - 1; }
        return options;
    }
}
