using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>Undo history (RE+ parity): every step with its number, the current one selected, undone steps dim; click a
/// step to jump there. Named checkpoints mark a position with a star.</summary>
[Panel("History", DockSide.Bottom, Order = 2)]
public partial class HistoryPanel : EditorPanel
{
    /// <summary>Width of the step number column (four digits and the cell's margins).</summary>
    private const int IndexColumn = 52;

    private Tree _list = null!;
    private Button _undo = null!;
    private Button _redo = null!;
    private LineEdit _checkpoint = null!;
    private Label _state = null!;
    private DeferredAction _update = null!;
    private bool _building;

    protected override void Build()
    {
        _update = new DeferredAction(Rebuild);
        HFlowContainer bar = PanelUi.Toolbar(this);
        // The same steps as Edit > Undo and Redo, enabled when they are.
        _undo = PanelUi.Button(bar, "Undo", "Undo the last step (Ctrl+Z)", () => Context.Run("edit.undo"));
        _redo = PanelUi.Button(bar, "Redo", "Redo the next step (Ctrl+Y)", () => Context.Run("edit.redo"));
        var checkpoint = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        checkpoint.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        bar.AddChild(checkpoint);
        _checkpoint = new LineEdit
        {
            PlaceholderText = "Checkpoint name", TooltipText = "A name for the current position (empty: the time)",
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(EditorTheme.SearchMinWidth, 0),
        };
        _checkpoint.TextSubmitted += _ => AddCheckpoint();
        checkpoint.AddChild(_checkpoint);
        PanelUi.Button(checkpoint, "Add checkpoint", "Name the current position in the history", AddCheckpoint);
        _state = PanelUi.Caption(checkpoint, "", wrap: false);
        PanelMarks.Status(_state);

        _list = new Tree
        {
            Name = "Steps",
            Columns = 2,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _list.SetColumnExpand(0, false);
        _list.SetColumnCustomMinimumWidth(0, IndexColumn);
        _list.SetColumnClipContent(1, true);
        _list.ItemSelected += () =>
        {
            if (!_building && _list.GetSelected() is { } item)
            {
                Context.Session.Commands.JumpTo(item.GetMetadata(0).AsInt32());
            }
        };
        AddChild(_list);

        Context.ProjectChanged += _ => _update.Queue();
        Context.Saved += _update.Queue;
        Context.AddAction(new EditorAction("history.checkpoint", "Add Undo Checkpoint", "Edit", () =>
        {
            _checkpoint.GrabFocus();
        })
        {
            Order = 20, Description = "Name the current position in the undo history (History panel)",
        });
    }

    public override void Refresh() => _update.Queue();

    private void AddCheckpoint()
    {
        CommandStack stack = Context.Session.Commands;
        string name = _checkpoint.Text.Trim();
        if (name.Length == 0)
        {
            name = $"Checkpoint {DateTime.Now:HH:mm:ss}";
        }

        stack.Checkpoint(name);
        _checkpoint.Text = "";
        Context.Log($"Checkpoint '{name}' at step {stack.Position}.");
        Rebuild();
    }

    private void Rebuild()
    {
        CommandStack stack = Context.Session.Commands;
        IReadOnlyList<HistoryEntry> history = stack.History();
        _building = true;
        try
        {
            _list.Clear();
            TreeItem root = _list.CreateItem();
            TreeItem current = Step(root, 0, "Map opened", stack.CheckpointName(0), done: true);
            foreach (HistoryEntry e in history)
            {
                TreeItem item = Step(root, e.Index, e.Name, stack.CheckpointName(e.Index), e.IsDone);
                if (e.Index == stack.Position)
                {
                    current = item;
                }
            }

            current.Select(0);
            _list.ScrollToItem(current);
        }
        finally
        {
            _building = false;
        }

        _undo.Disabled = !stack.CanUndo;
        _redo.Disabled = !stack.CanRedo;
        _state.Text = $"Step {stack.Position} of {history.Count}" + (Context.Session.IsDirty ? " · unsaved" : "");
        _state.TooltipText = _state.Text;
    }

    /// <summary>One step: its number (dim, right-aligned), its name, a star and the name of a checkpoint there. Undone
    /// steps are dim.</summary>
    private TreeItem Step(TreeItem root, int index, string name, string? checkpoint, bool done)
    {
        TreeItem item = _list.CreateItem(root);
        item.SetMetadata(0, index);
        item.SetText(0, index.ToString(CultureInfo.InvariantCulture));
        item.SetTextAlignment(0, HorizontalAlignment.Right);
        item.SetCustomColor(0, EditorTheme.TextDim);
        item.SetText(1, checkpoint is null ? name : $"{name} · {checkpoint}");
        item.SetTooltipText(1, checkpoint is null ? name : $"{name}\nCheckpoint: {checkpoint}");
        if (checkpoint is not null)
        {
            item.SetIcon(1, Icons.Get("star", EditorTheme.Warning));
        }

        if (!done)
        {
            item.SetCustomColor(1, EditorTheme.TextHidden);
            item.SetIconModulate(1, EditorTheme.TextHidden);
        }

        return item;
    }
}
