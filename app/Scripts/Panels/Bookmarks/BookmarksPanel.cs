using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// Camera bookmarks ("Working Views"): saved with the project, one undo step per change. Add stores the current
/// camera; double-click jumps. As in RE+, Ctrl+1–9 stores slot 1–9 and 1–9 jumps to it. Names and notes edit
/// inline.
/// </summary>
[Panel("Bookmarks", DockSide.Right, Order = 3)]
public partial class BookmarksPanel : EditorPanel
{
    private const int Slots = 9;

    private Tree _tree = null!;
    private Control _empty = null!;
    private Control _bar = null!;
    private Label _help = null!;
    private Button _updateButton = null!;
    private Button _jump = null!;
    private Button _remove = null!;
    private DeferredAction _update = null!;

    protected override void Build()
    {
        _update = new DeferredAction(Rebuild);
        HFlowContainer bar = PanelUi.Toolbar(this);
        _bar = bar;
        PanelUi.Button(bar, "Add", "Bookmark the current camera (Ctrl+1–9 stores a numbered slot)", () => Store(null));
        _jump = PanelUi.Button(bar, "Jump", "Move the camera to the selected bookmark", () =>
        {
            if (Selected() is { } i)
            {
                Jump(i);
            }
        });
        _updateButton = PanelUi.Button(bar, "Update", "Move the selected bookmark to the current camera", () =>
        {
            if (Selected() is { } i)
            {
                Store(i);
            }
        });
        _remove = PanelUi.IconButton(bar, "trash", "Delete the selected bookmark", Remove, danger: true);

        _tree = new Tree
        {
            Name = "Bookmarks",
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        PanelUi.Columns(_tree, ("#", 36, false), ("Name", 80, true), ("Note", 80, true));
        _tree.ItemSelected += UpdateButtons;
        _tree.ItemActivated += () =>
        {
            if (Selected() is { } i && _tree.GetSelectedColumn() == 0)
            {
                Jump(i);
            }
            else
            {
                _tree.EditSelected(true);
            }
        };
        _tree.ItemEdited += OnEdited;
        AddChild(_tree);
        _empty = PanelUi.EmptyState(_tree, "camera-bookmark", "No bookmarks",
            "Bookmarks keep camera views to come back to. Ctrl+1–9 stores a numbered one, 1–9 jumps to it.", "Bookmark this view", () => Store(null));
        _help = PanelUi.Caption(this, "Ctrl+1–9 stores a slot, 1–9 jumps to it · double\u2011click a slot number to jump, a name or note to edit it", wrap: false);

        Context.ProjectChanged += _ => _update.Queue();
        Context.AddAction(new EditorAction("bookmarks.add", "Bookmark Camera", "View", () => Store(null))
        {
            Order = 300, Description = "Bookmark the current camera (Bookmarks panel)",
        });
        for (int slot = 1; slot <= Slots; slot++)
        {
            int index = slot - 1;
            string n = slot.ToString(CultureInfo.InvariantCulture);
            Context.AddAction(new EditorAction($"bookmarks.store{n}", $"Store Camera Bookmark {n}", "View/Bookmarks", () => Store(index, slot: true))
            {
                Shortcut = "Ctrl+" + n,
                Order = 100 + slot,
            });
            Context.AddAction(new EditorAction($"bookmarks.jump{n}", $"Jump to Camera Bookmark {n}", "View/Bookmarks", () => Jump(index))
            {
                Shortcut = n,
                Order = 200 + slot,
                IsEnabled = () => index < Context.Project.Workspace.Bookmarks.Count,
            });
        }
    }

    public override void Refresh() => _update.Queue();

    private void Rebuild()
    {
        int? selected = Selected();
        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        List<Bookmark> list = Context.Project.Workspace.Bookmarks;
        for (int i = 0; i < list.Count; i++)
        {
            Bookmark b = list[i];
            TreeItem item = _tree.CreateItem(root);
            item.SetText(0, (i + 1).ToString(CultureInfo.InvariantCulture));
            item.SetTextAlignment(0, HorizontalAlignment.Right);
            // Numbered slots (1–9 jump to them) in the text colour, the rest dim.
            item.SetCustomColor(0, i < Slots ? EditorTheme.Text : EditorTheme.TextDim);
            item.SetText(1, b.Name);
            item.SetText(2, b.Note ?? "");
            item.SetEditable(1, true);
            item.SetEditable(2, true);
            item.SetMetadata(0, i);
            item.SetTooltipText(0, $"{b.Location}  {b.Rotation}");
            if (selected == i)
            {
                item.Select(0);
            }
        }

        // While empty, the empty state's button and hint stand in for the toolbar and the help line.
        _empty.Visible = list.Count == 0;
        _bar.Visible = list.Count > 0;
        _help.Visible = list.Count > 0;
        _tree.ColumnTitlesVisible = list.Count > 0;
        UpdateButtons();
    }

    /// <summary>Enables the buttons that act on the selected bookmark when one is selected.</summary>
    private void UpdateButtons()
    {
        bool any = Selected() is not null;
        _jump.Disabled = !any;
        _updateButton.Disabled = !any;
        _remove.Disabled = !any;
    }

    private int? Selected() => _tree.GetSelected() is { } item ? item.GetMetadata(0).AsInt32() : null;

    /// <summary>Stores the current camera: as a new bookmark, over bookmark <paramref name="index"/>, or (for a numbered
    /// slot) in that slot, filling earlier empty slots with copies so numbering holds.</summary>
    private void Store(int? index, bool slot = false)
    {
        MapProject p = Context.Project;
        Workspace w = p.Workspace;
        Vec3 location = w.CameraLocation;
        Rot rotation = w.CameraRotation;
        string name = index is { } i && i < w.Bookmarks.Count ? w.Bookmarks[i].Name : $"View {(index ?? w.Bookmarks.Count) + 1}";
        Context.Execute(PanelCommands.Bookmarks(p, index is null ? $"Add bookmark {name}" : $"Store bookmark {name}", list =>
        {
            var b = new Bookmark { Name = name, Location = location, Rotation = rotation };
            if (index is not { } at)
            {
                list.Add(b);
                return;
            }

            while (slot && list.Count < at)
            {
                list.Add(new Bookmark { Name = $"View {list.Count + 1}", Location = location, Rotation = rotation });
            }

            if (at < list.Count)
            {
                b.Note = list[at].Note;
                list[at] = b;
            }
            else
            {
                list.Add(b);
            }
        }));
        Context.SetStatus($"Bookmark {name} stored.");
    }

    private void Jump(int index)
    {
        List<Bookmark> list = Context.Project.Workspace.Bookmarks;
        if (index < list.Count)
        {
            Bookmark b = list[index];
            Context.ViewFrom(b.Location, b.Rotation);
            Context.SetStatus($"Bookmark {index + 1}: {b.Name}");
        }
    }

    private void Remove()
    {
        if (Selected() is not { } index)
        {
            return;
        }

        string name = Context.Project.Workspace.Bookmarks[index].Name;
        Context.Execute(PanelCommands.Bookmarks(Context.Project, $"Remove bookmark {name}", list => list.RemoveAt(index)));
    }

    private void OnEdited()
    {
        if (_tree.GetEdited() is not { } item)
        {
            return;
        }

        int index = item.GetMetadata(0).AsInt32();
        int column = _tree.GetEditedColumn();
        string text = item.GetText(column).Trim();
        Context.Execute(PanelCommands.Bookmarks(Context.Project, column == 1 ? "Rename bookmark" : "Set bookmark note", list =>
        {
            if (index >= list.Count)
            {
                return;
            }

            if (column == 1)
            {
                list[index].Name = text;
            }
            else
            {
                list[index].Note = text.Length == 0 ? null : text;
            }
        }));
    }
}
