using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Tools;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Panels;

/// <summary>
/// The docked, non-modal inspector for the selection: transform (location, rotation in degrees, scales, pre-pivot),
/// brush CSG, flags, material and builder parameters, GE kind, points, ID bit and platform, editor state (layer,
/// folder, hidden, locked, note), and the class properties (see <see cref="PropertyGrid"/>). Several objects edit
/// together; differing values show as mixed. Every edit is an undoable command; typing merges into one step.
/// </summary>
[Panel("Inspector", DockSide.Right, Order = 0)]
public partial class InspectorPanel : EditorPanel
{
    /// <summary>Width of the names of the Inspector's own rows (transform, brush, editor): the panels' shared label
    /// column, narrow enough that a selection never makes the dock wider.</summary>
    private const int LabelWidth = EditorTheme.LabelColumn;

    private static readonly (string Name, int Bit)[] s_flags = PolyFlagNames.All;

    private readonly List<Action> _syncers = new();
    private List<ObjectRef> _refs = new();

    /// <summary>The map the rows were built for: rows removed after another map opens must not edit the new one.</summary>
    private MapProject? _builtFor;
    private const string ChangedOnly = "Changed only";

    private Label _header = null!;
    private Control _bar = null!;
    private Label _kind = null!;
    private Label _summary = null!;
    private TextureRect _icon = null!;
    private Control _empty = null!;
    private ScrollContainer _scroll = null!;
    private VBoxContainer _sections = null!;
    private Label _propsHeader = null!;
    private PropertyGrid _grid = null!;
    private LiveEdit _live = null!;

    /// <summary>Typed Main/Post scale values keep textures from the brush as it was before the typing started.</summary>
    private readonly TextureLock.Series _scaleSeries = new();
    private DeferredAction _rebuild = null!;
    private DeferredAction _sync = null!;
    private string _signature = "";
    private bool _moreProperties;
    private bool Compact => WorkspaceMode.Simple && !_moreProperties;
    private ObjectIndex? _index;

    /// <summary>The objects by id, rebuilt after every change (selections can be thousands of objects).</summary>
    private ObjectIndex Index => _index ??= new ObjectIndex(Context.Project);

    protected override void Build()
    {
        _live = new LiveEdit(this, Context);
        _rebuild = new DeferredAction(Rebuild);
        _sync = new DeferredAction(SyncOrRebuild);

        // The selection: its type icon, its name as the panel's title and its class dim after it.
        var title = new HBoxContainer();
        title.AddThemeConstantOverride("separation", EditorTheme.SpaceS);
        AddChild(title);
        _icon = new TextureRect { StretchMode = TextureRect.StretchModeEnum.KeepCentered, Visible = false };
        title.AddChild(_icon);
        _header = PanelUi.Title(title, "Nothing selected");
        _header.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _header.SizeFlagsStretchRatio = 2;
        _kind = PanelUi.Caption(title, "", wrap: false);
        _kind.SizeFlagsStretchRatio = 1;
        _kind.HorizontalAlignment = HorizontalAlignment.Right;

        // A selection of several objects: what kinds, on a line of its own under the title so it is never cut short.
        _summary = PanelUi.Caption(this, "");
        _summary.MouseFilter = MouseFilterEnum.Pass;
        _summary.Visible = false;
        HFlowContainer bar = PanelUi.Toolbar(this);
        PanelUi.Search(bar, "Filter properties", t =>
        {
            _grid.Filter = t;
            _rebuild.Queue();
        }, "Show the properties whose name, category or type has every word typed");
        Button changedOnly = PanelUi.Toggle(bar, ChangedOnly, "Show only the properties stored in the map (changed from the class default)", false, on =>
        {
            _grid.StoredOnly = on;
            _rebuild.Queue();
        });
        changedOnly.Icon = Icons.Get("filter", EditorTheme.TextDim);

        // In a narrow dock the chip shows its icon only, so the filter row stays one row.
        Resized += () => changedOnly.Text = Size.X < EditorTheme.CompactPanelWidth ? "" : ChangedOnly;
        _bar = bar;

        VBoxContainer content = PanelUi.Scroll(this);
        _sections = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _sections.AddThemeConstantOverride("separation", 2);
        content.AddChild(_sections);
        _propsHeader = PanelUi.Header(content, "Properties");
        _grid = new PropertyGrid();
        _grid.Init(Context);
        WorkspaceMode.Changed += () => { _moreProperties = false; _rebuild.Queue(); };
        // Rows a colour drag stored under "Changed only" show once its picker closes.
        _grid.PickerClosed += () => _sync.Queue();
        content.AddChild(_grid);
        _scroll = PanelUi.ScrollOf(content);

        // Nothing selected: the panel's empty state in place of the (empty) scroll area, centred in the dock.
        _empty = PanelUi.EmptyState(this, "inspector", "Nothing selected",
            "Select objects in a view or the Outliner. Map-wide settings are in the Map tab.");
        _empty.Visible = false;

        Context.SelectionChanged += () => _rebuild.Queue();
        EditorLifetime.Defer(() => { ToolManager.Instance!.DragEnded += () => _sync.Queue(); });
        Context.ProjectChanged += _ =>
        {
            _index = null;
            _sync.Queue();
        };
        Context.AddAction(new EditorAction("inspector.selectSameClass", "Select All of Selected Classes", "Edit", SelectSameClass)
        {
            Order = 250, Description = "Select every object of the selected objects' classes", IsEnabled = () => Context.Selection.Count > 0,
        });
    }

    /// <summary>Another map was opened: edits still waiting for the typing pause belong to the old one.</summary>
    public override void Refresh()
    {
        _live.Cancel();
        _grid.Cancel();
        _scaleSeries.Clear();
        _rebuild.Queue();
    }

    // ---- build ----------------------------------------------------------------------------------------------------

    private void Rebuild()
    {
        _windowTint = null;
        _index = null;
        _live.Flush();
        _grid.Flush();
        _scaleSeries.Clear();
        foreach (Node child in _sections.GetChildren())
        {
            _sections.RemoveChild(child);
            child.QueueFree();
        }

        _syncers.Clear();
        _builtFor = Context.Project;
        _refs = Context.Selection.Items.Where(Index.Exists).ToList();
        ShowTitle();
        _bar.Visible = _refs.Count > 0;
        _header.GetParent<Control>().Visible = _refs.Count > 0;
        _scroll.Visible = _refs.Count > 0;
        _empty.Visible = _refs.Count == 0;
        bool props = _refs.Any(r => r.Kind is ObjectKind.Actor or ObjectKind.Brush or ObjectKind.Stair);
        _grid.CommonOnly = Compact;
        _propsHeader.Visible = props;
        if (_refs.Count == 0)
        {
            _grid.ShowSource(null);
            _signature = Signature();
            return;
        }

        HFlowContainer tools = PanelUi.Toolbar(_sections);
        PanelUi.Button(tools, "Focus", "Frame the selection in the views", () => Context.Focus(_refs));
        if (!Compact) PanelUi.Button(tools, "Same class", "Select every object of the selected classes", SelectSameClass);
        if (_refs.Any(r => r.Kind == ObjectKind.Actor && Context.Project.FindActor(r.Id) is { } actor
            && (MissionLinks.IsObjective(actor) || MissionLinks.IsTrigger(actor))))
            PanelUi.Button(tools, "Link missions & objectives…", "Open named mission, objective and device links", MissionsPanel.OpenForSelection);
        if (WorkspaceMode.Simple && !(_refs.Count == 1 && Index.Brush(_refs[0])?.Stair is not null)) PanelUi.Button(tools, _moreProperties ? "Fewer properties" : "More properties",
            "Show technical settings for this selection. Changes still use the usual undo history.", () => { _moreProperties = !_moreProperties; _rebuild.Queue(); });

        if (_refs.Any(r => r.Kind is ObjectKind.Actor or ObjectKind.Brush or ObjectKind.Stair))
        {
            TransformSection();
        }
        if (_refs.Count == 1 && Index.Brush(_refs[0]) is { Stair: not null } staircase)
        {
            StairSection(staircase);
            _bar.Visible = false; _propsHeader.Visible = false;
            _grid.ShowSource(null);
            _signature = Signature();
            SyncAll();
            return;
        }
        if (Compact && _refs.Count == 1 && Index.Brush(_refs[0])?.Shape.Builder is { } builder) BuilderRows(_refs[0], builder);
        if (_refs.Count == 1 && Context.Project.FindActor(_refs[0].Id) is { Door: not null } door) DoorSection(door);
        if (_refs.Count == 1 && Context.Project.FindActor(_refs[0].Id) is { Window: not null } window) WindowSection(window);

        if (!Compact && _refs.Any(r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair)))
        {
            BrushSection();
        }

        if (_refs.Any(r => r.Kind == ObjectKind.Ge))
        {
            GeSection();
        }

        if (_refs.Any(r => r.Kind == ObjectKind.Actor))
        {
            if (!Compact) MatchSection();
            if (!Compact) RecoveredSection();
        }

        if (!Compact) EditorSection();
        _grid.ShowSource(props ? new ObjectPropSource(_refs) : null);
        _signature = Signature();
        SyncAll();
    }

    /// <summary>The title line: the object's name and its class, or how many objects of which classes.</summary>
    private void ShowTitle()
    {
        (string name, string kind, string detail, string? icon) = Summary();
        _header.Text = name;
        _header.TooltipText = detail.Length == 0 ? name : $"{name}\n{detail}";
        // One object: its kind dim at the end of the title line. Several: the breakdown on its own line below.
        bool many = _refs.Count > 1;
        _kind.Text = many ? "" : kind;
        _kind.TooltipText = detail;
        _kind.Visible = !many && kind.Length > 0;
        _summary.Text = many ? kind : "";
        _summary.TooltipText = many ? detail : "";
        _summary.Visible = many && kind.Length > 0;
        _icon.Visible = icon is not null;
        _icon.Texture = icon is null ? null : Icons.Get(icon, EditorTheme.TextDim);
    }

    /// <summary>The title, the dim kind after it, the kind in full for the tooltips (the qualified class, every class
    /// of a selection), and the type icon.</summary>
    private (string Name, string Kind, string Detail, string? Icon) Summary()
    {
        if (_refs.Count == 0)
        {
            return ("Nothing selected", "", "", null);
        }

        if (_refs.Count == 1)
        {
            ObjectRef r = _refs[0];
            string cls = Index.Actor(r)?.Class ?? "";
            string brush = Index.Brush(r)?.Stair is { } stairDesign ? stairDesign.Layout.ToUpperInvariant() + " staircase" : Index.Brush(r)?.Oper == CsgOper.Subtract ? "Brush · subtract" : "Brush · add";
            string ge = ObjectInfo.GeClass(Index.Ge(r)?.Kind ?? "");
            return r.Kind switch
            {
                ObjectKind.Actor => (r.Id, cls.Length == 0 ? "" : OpenScct.Editor.Core.Classes.ClassNames.Title(cls), cls, PanelIcons.TypeIconName(cls)),
                ObjectKind.Brush or ObjectKind.Stair => (r.Id, brush, brush, Index.Brush(r)?.Oper == CsgOper.Subtract ? "type-brush-subtract" : "type-brush"),
                _ => (r.Id, ge, ge, "type-ge"),
            };
        }

        var counts = _refs.Select(r => Index.Class(r) ?? "?").GroupBy(c => c)
            .OrderByDescending(g => g.Count()).Select(g => PanelUi.Count(g.Count(), Noun(g.Key))).ToList();
        string kind = string.Join(" · ", counts.Take(3)) + (counts.Count > 3 ? $" · +{counts.Count - 3}" : "");
        return (PanelUi.Count(_refs.Count, "object"), kind, string.Join("\n", counts), null);

        // "Engine.PlayerStart" → "player start"; S-prefixed class names stay as map makers know them.
        static string Noun(string cls)
        {
            string name = PanelUi.DisplayName(cls);
            return name.Length > 1 && char.IsLower(name[1]) ? char.ToLowerInvariant(name[0]) + name[1..] : name;
        }
    }

    /// <summary>What decides the rows: the objects, their classes, GE point counts, builders, layers, grid keys.</summary>
    private string Signature()
    {
        var parts = new List<string>();
        foreach (ObjectRef r in _refs)
        {
            parts.Add(r.ToString());
            if (Index.Brush(r)?.Stair is { } stair) parts.Add(stair.ToJson());
            // Tint drags update the map live; keep the rows (and their popup) until the picker closes.
            if (Context.Project.FindActor(r.Id)?.Door is { } door) parts.Add(door.ToJson());
            if (Context.Project.FindActor(r.Id)?.Window is { } window) parts.Add(_windowTint?.Picking == true ? _windowTintSignature : window.ToJson());
            parts.Add(r.Kind switch
            {
                ObjectKind.Actor => Index.Actor(r)?.Class ?? "",
                ObjectKind.Brush or ObjectKind.Stair => Index.Brush(r)?.Shape.Builder is { } b ? b.Kind + string.Join(',', b.Numbers.Keys) : "",
                _ => Index.Ge(r) is { } ge ? $"{ge.Kind}:{ge.Points.Count}:{ge.Appearance is not null}" : "",
            });
        }

        parts.AddRange(Context.Project.Layers.Select(l => l.Name));
        parts.Add(_grid.Signature);
        return string.Join('|', parts);
    }

    private void SyncOrRebuild()
    {
        if (ToolManager.Instance?.Active is TransformTool { IsDragging: true })
        {
            SyncAll();
            return;
        }
        if (Signature() != _signature)
        {
            Rebuild();
            return;
        }

        ShowTitle();
        SyncAll();
    }

    private void SyncAll()
    {
        foreach (Action sync in _syncers)
        {
            sync();
        }

        _grid.Sync();
    }

    private void Execute(IEditCommand? command)
    {
        if (command is ReplaceObjectsCommand { IsEmpty: true } || command is null || !ReferenceEquals(Context.Project, _builtFor))
        {
            return;
        }

        Context.Execute(command);
    }

    // ---- transform --------------------------------------------------------------------------------------------------

    private void TransformSection()
    {
        PanelUi.Header(_sections, "Transform");
        VecRow("Location", ["X", "Y", "Z"],
            r => r.Kind switch
            {
                ObjectKind.Actor => Index.Actor(r) is { } a ? Arr(a.Location) : null,
                ObjectKind.Brush or ObjectKind.Stair => Index.Brush(r) is { } b ? Arr(b.Location) : null,
                _ => null,
            },
            brush: (b, i, v) => b.Location = b.Location.With(i, v),
            actor: (a, i, v) => a.Location = a.Location.With(i, v),
            tooltip: "Where the object sits in the map. X and Y move along the floor; Z changes its height.");
        VecRow("Rotation", ["Pitch", "Yaw", "Roll"],
            r => r.Kind switch
            {
                ObjectKind.Actor => Index.Actor(r) is { } a ? Deg(a.Rotation) : null,
                ObjectKind.Brush or ObjectKind.Stair => Index.Brush(r) is { } b ? Deg(b.Rotation) : null,
                _ => null,
            },
            brush: (b, i, v) => b.Rotation = WithAxis(b.Rotation, i, v),
            actor: (a, i, v) => a.Rotation = WithAxis(a.Rotation, i, v),
            tooltip: "Turn the object: pitch tilts up/down, yaw turns left/right, roll tips sideways. Values are degrees.");
        if (_refs.Any(r => r.Kind == ObjectKind.Actor))
        {
            VecRow("Draw scale", ["Scale"], r => r.Kind == ObjectKind.Actor && Index.Actor(r) is { } a ? [a.DrawScale] : null,
                actor: (a, _, v) => a.DrawScale = v, tooltip: "Resize the object's appearance uniformly. 2 makes it twice as large.");
            VecRow("Draw scale 3D", ["X", "Y", "Z"], r => r.Kind == ObjectKind.Actor && Index.Actor(r) is { } a ? Arr(a.DrawScale3D) : null,
                actor: (a, i, v) => a.DrawScale3D = a.DrawScale3D.With(i, v), tooltip: "Stretch the object's appearance along individual axes. 1 keeps the original size.");
        }

        if (Compact) return;
        if (_refs.Any(r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair)))
        {
            VecRow("Main scale", ["X", "Y", "Z"], r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair) && Index.Brush(r) is { } b ? Arr(b.MainScale) : null,
                brush: (b, i, v) => _scaleSeries.Rescale(b, x => x.MainScale = x.MainScale.With(i, v), SurfaceActions.ScaleKeepsTextures),
                tooltip: "Brush MainScale (applied before rotation)");
            VecRow("Post scale", ["X", "Y", "Z"], r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair) && Index.Brush(r) is { } b ? Arr(b.PostScale) : null,
                brush: (b, i, v) => _scaleSeries.Rescale(b, x => x.PostScale = x.PostScale.With(i, v), SurfaceActions.ScaleKeepsTextures),
                tooltip: "Brush PostScale (applied after rotation)");
        }

        VecRow("Pre-pivot", ["X", "Y", "Z"],
            r => r.Kind switch
            {
                ObjectKind.Actor => Index.Actor(r) is { } a ? Arr(a.PrePivot) : null,
                ObjectKind.Brush or ObjectKind.Stair => Index.Brush(r) is { } b ? Arr(b.PrePivot) : null,
                _ => null,
            },
            brush: (b, i, v) => b.PrePivot = b.PrePivot.With(i, v),
            actor: (a, i, v) => a.PrePivot = a.PrePivot.With(i, v));
    }

    private static double[] Arr(Vec3 v) => [v.X, v.Y, v.Z];

    private static double[] Deg(Rot r) => [Rot.ToDegrees(r.Pitch), Rot.ToDegrees(r.Yaw), Rot.ToDegrees(r.Roll)];

    private static Rot WithAxis(Rot r, int axis, double degrees)
    {
        int u = Rot.FromDegrees(degrees);
        return axis switch
        {
            0 => r with { Pitch = u },
            1 => r with { Yaw = u },
            _ => r with { Roll = u },
        };
    }

    /// <summary>A row of number boxes bound to a per-object vector; an edit changes one component on every object.</summary>
    private void VecRow(string label, string[] axes, Func<ObjectRef, double[]?> read, Action<Brush, int, double>? brush = null,
        Action<Actor, int, double>? actor = null, Action<Ge, int, double>? ge = null, string? tooltip = null)
    {
        HBoxContainer row = PanelUi.Row(_sections, label, tooltip, LabelWidth);
        AxisFields fields = axes.Length == 1 ? AxisFields.Number(tooltip ?? label) : AxisFields.Create(axes);
        row.AddChild(fields);
        fields.Edited += (axis, v, final) => _live.Change(() =>
        {
            // Rotations compare in rotator units, since that is what is stored.
            bool Differs(double[] cur) => label.StartsWith("Rotation", StringComparison.Ordinal)
                ? Rot.FromDegrees(cur[axis]) != Rot.FromDegrees(v)
                : Math.Abs(cur[axis] - v) > 1e-9;
            if (!_refs.Any(r => read(r) is { } cur && Differs(cur)))
            {
                return;
            }

            Execute(ReplaceObjectsCommand.Edit(Context.Project, _refs, $"Set {label.TrimEnd('°')} {axes[axis]}",
                brush: brush is null ? null : b => brush(b, axis, v),
                actor: actor is null ? null : a => actor(a, axis, v),
                ge: ge is null ? null : g => ge(g, axis, v),
                mergeKey: $"inspector:{label}:{axis}"));
        }, final);
        _syncers.Add(() =>
        {
            var values = _refs.Select(read).OfType<double[]>().ToList();
            var shown = new double?[axes.Length];
            for (int i = 0; i < axes.Length; i++)
            {
                int axis = i;
                var distinct = values.Select(v => Math.Round(v[axis], 4)).Distinct().ToList();
                shown[i] = distinct.Count == 1 ? distinct[0] : null;
            }

            fields.SetValues(shown);
        });
    }

    // ---- brush ------------------------------------------------------------------------------------------------------

    private void BrushSection()
    {
        PanelUi.Header(_sections, "Brush");
        List<Brush> Brushes() => _refs.Where(r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair)).Select(r => Index.Brush(r)).OfType<Brush>().ToList();
        List<ObjectRef> brushRefs = _refs.Where(r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair)).ToList();

        HBoxContainer csgRow = PanelUi.Row(_sections, "CSG", labelWidth: LabelWidth);
        OptionButton csg = PanelUi.Options(csgRow);
        csg.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        csg.AddItem("Add", (int)CsgOper.Add);
        csg.AddItem("Subtract", (int)CsgOper.Subtract);
        csg.TooltipText = "Add fills its volume with solid world; Subtract carves it out of the world";
        csg.ItemSelected += index =>
        {
            var oper = (CsgOper)csg.GetItemId((int)index);
            Execute(ReplaceObjectsCommand.Edit(Context.Project, brushRefs.Where(r => Index.Brush(r)?.Oper != oper), $"CSG {oper}",
                brush: b => b.Oper = oper));
        };
        _syncers.Add(() =>
        {
            var opers = Brushes().Select(b => b.Oper).Distinct().ToList();
            PanelMarks.Choose(csg, opers.Count == 1 ? csg.GetItemIndex((int)opers[0]) : -1, opers.Count > 1);
        });

        // A flow rather than a fixed grid, so a narrow dock gets fewer columns instead of being widened; equal widths keep
        // the boxes in columns.
        var flags = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _sections.AddChild(flags);
        var boxes = new List<CheckBox>();
        foreach ((string name, int bit) in s_flags)
        {
            var box = new CheckBox { Text = name, TooltipText = $"PolyFlags 0x{bit:X8}" };
            boxes.Add(box);
            box.Toggled += on => Execute(ReplaceObjectsCommand.Edit(Context.Project, brushRefs, $"{(on ? "Set" : "Clear")} {name}",
                brush: b => b.PolyFlags = on ? b.PolyFlags | bit : b.PolyFlags & ~bit));
            flags.AddChild(box);
            _syncers.Add(() =>
            {
                var states = Brushes().Select(b => (b.PolyFlags & bit) != 0).Distinct().ToList();
                box.SetPressedNoSignal(states is [true]);
                PanelMarks.SetMixed(box, states.Count > 1);
                box.TooltipText = states.Count > 1 ? $"PolyFlags 0x{bit:X8}\n{PanelUi.MixedTooltip}: click to set it on every brush" : $"PolyFlags 0x{bit:X8}";
            });
        }

        float widest = boxes.Max(b => b.GetCombinedMinimumSize().X);
        foreach (CheckBox box in boxes)
        {
            box.CustomMinimumSize = new Vector2(widest, 0);
        }

        TextRow("Material", "Material of every face (Package.Group.Name); empty for the default",
            r => r.Kind is (ObjectKind.Brush or ObjectKind.Stair) && Index.Brush(r) is { } b
                ? b.Shape.Faces.Select(f => f.Material ?? "").Distinct().Count() == 1 ? b.Shape.Faces.FirstOrDefault()?.Material ?? "" : null
                : null,
            text => ReplaceObjectsCommand.Edit(Context.Project, brushRefs, "Set material", brush: b =>
            {
                string? m = text.Trim().Length == 0 ? null : text.Trim();
                foreach (BrushFace f in b.Shape.Faces)
                {
                    f.Material = m;
                }
            }));

        Label info = PanelUi.Caption(_sections, "");
        _syncers.Add(() =>
        {
            List<Brush> list = Brushes();
            int faces = list.Sum(b => b.Shape.Faces.Count);
            var builders = list.Select(b => b.Shape.Builder?.Kind ?? "hand-made").Distinct().ToList();
            info.Text = $"{PanelUi.Count(faces, "face")} · {string.Join(", ", builders)}";
        });

        if (brushRefs.Count == 1 && Index.Brush(brushRefs[0])?.Shape.Builder is { } spec)
        {
            BuilderRows(brushRefs[0], spec);
        }
    }

    /// <summary>Parameters of a parametric brush; changing one rebuilds the shape through its builder.</summary>
    private void BuilderRows(ObjectRef brushRef, BuilderSpec spec)
    {
        IShapeBuilder? builder = EditorServices.ShapeBuilder(spec.Kind);
        if (spec.Kind is "stairs" or "linearStair" or "curvedStair" or "spiralStair")
            PanelUi.Button(_sections, "Convert to Editable Stairs", "Upgrade the complete intact staircase into one editable object", () => Context.Run("stairs.convert"));
        foreach (string key in spec.Numbers.Keys.Order(StringComparer.Ordinal))
        {
            HBoxContainer row = PanelUi.Row(_sections, PanelUi.Words(key),
                builder is null ? $"{spec.Kind}.{key}: no builder for this kind is loaded" : $"{spec.Kind}.{key}: changing it rebuilds the {spec.Kind}",
                LabelWidth);
            AxisFields field = AxisFields.Number(key);
            field.Editable = builder is not null;
            row.AddChild(field);
            field.Edited += (_, v, final) => _live.Change(() => Regenerate(brushRef, key, v), final, live: false);
            _syncers.Add(() =>
            {
                double? value = Index.Brush(brushRef)?.Shape.Builder?.Numbers.GetValueOrDefault(key);
                field.SetValues([value]);
            });
        }
    }

    private void Regenerate(ObjectRef brushRef, string key, double value)
    {
        MapProject p = Context.Project;
        if (Index.Brush(brushRef) is not { Shape.Builder: { } spec } brush || EditorServices.ShapeBuilder(spec.Kind) is not { } builder
            || spec.Numbers.GetValueOrDefault(key) == value)
        {
            return;
        }

        BuilderSpec next = spec.Clone();
        next.Numbers[key] = value;
        IReadOnlyList<(BrushShape Shape, CsgOper Oper, Vec3 Offset)> parts;
        try
        {
            parts = builder.Build(next);
        }
        catch (ArgumentException ex)
        {
            Context.Log($"{spec.Kind}: {ex.Message}", Severity.Warning);
            return;
        }

        if (parts.Count != 1)
        {
            Context.Log($"{spec.Kind} makes {parts.Count} brushes; rebuild it with the geometry tools.", Severity.Warning);
            return;
        }

        BrushShape shape = parts[0].Shape.Clone();
        shape.Builder = next;
        var materials = brush.Shape.Faces.Select(f => f.Material).Distinct().ToList();
        if (materials is [{ } material])
        {
            shape.Faces.ForEach(f => f.Material ??= material);
        }

        bool keepTextures = SurfaceActions.ScaleKeepsTextures;
        Execute(ReplaceObjectsCommand.Edit(p, [brushRef], $"Set {spec.Kind} {key}", brush: b =>
        {
            Brush old = b.Clone();
            b.Shape = shape.Clone();
            if (keepTextures)
            {
                // Resizing keeps each face's surface and texture size, as scaling does.
                TextureLock.Reshaped(old, b);
            }
        }));
    }

    // ---- GE ---------------------------------------------------------------------------------------------------------

    private void GeSection()
    {
        PanelUi.Header(_sections, "Geometric event");
        List<ObjectRef> geRefs = _refs.Where(r => r.Kind == ObjectKind.Ge).ToList();
        List<Ge> Ges() => geRefs.Select(r => Index.Ge(r)).OfType<Ge>().ToList();

        OptionRow("Kind", "The traversal kind", GeKinds.All.Select((k, i) => (KindName(k), i)).ToList(),
            () => Ges().Select(g => GeKinds.All.ToList().IndexOf(g.Kind)).ToList(),
            id => ReplaceObjectsCommand.Edit(Context.Project, geRefs, $"GE kind {GeKinds.All[id]}", ge: g =>
            {
                g.Kind = GeKinds.All[id];
                if (g.Appearance is not null)
                {
                    g.Appearance = GeGeometry.Supports(g.Kind) ? GeGeometry.Defaults(g.Kind) : null;
                }
            }));
        var masks = new List<(string, int)> { ("None (always on)", 0) };
        for (int bit = 1; bit <= 128; bit <<= 1)
        {
            masks.Add(($"ID {bit}", bit));
        }

        OptionRow("GE ID", "The bit matched against LevelInfo.GEActivationMask", masks,
            () => Ges().Select(g => g.Mask).ToList(),
            id => ReplaceObjectsCommand.Edit(Context.Project, geRefs, $"GE ID {id}", ge: g => g.Mask = id));
        OptionRow("Platform", "Original GEPlatform", [("Common", 0), ("PS2 only", 1), ("Xbox only", 2)],
            () => Ges().Select(g => g.Platform).ToList(),
            id => ReplaceObjectsCommand.Edit(Context.Project, geRefs, "GE platform", ge: g => g.Platform = id));

        if (Ges().Any(g => GeGeometry.Supports(g.Kind)))
        {
            CheckRow("Geometry", "Generate visible parts that follow this GE's endpoints", () => Ges().Select(g => g.Appearance is not null).ToList(),
                on => ReplaceObjectsCommand.Edit(Context.Project, geRefs, "GE visible geometry", ge: g =>
                    g.Appearance = on && GeGeometry.Supports(g.Kind) ? g.Appearance ?? GeGeometry.Defaults(g.Kind) : null));
        }

        if (Ges().Any(g => g.Appearance is not null))
        {
            PanelUi.Header(_sections, "Traversal geometry");
            VecRow("Width / height", [""], r => Index.Ge(r)?.Appearance is { } a ? [a.Width] : null,
                ge: (g, _, value) =>
                {
                    if (g.Appearance is { } a)
                    {
                        a.Width = Math.Clamp(value, 4, 512);
                        if (g.Kind == GeKinds.Ladder)
                        {
                            a.Radius = Math.Min(a.Radius, (a.Width - 1) / 2);
                        }
                    }
                }, tooltip: "Ladder width or fence height in units");
            VecRow("Radius", [""], r => Index.Ge(r)?.Appearance is { } a ? [a.Radius] : null,
                ge: (g, _, value) =>
                {
                    if (g.Appearance is { } a)
                    {
                        a.Radius = Math.Clamp(value, 0.5, g.Kind == GeKinds.Ladder ? Math.Min(32, (a.Width - 1) / 2) : 32);
                    }
                }, tooltip: "Radius of rails, poles, bars or cables in units");
            VecRow("Spacing", [""], r => Index.Ge(r)?.Appearance is { } a ? [a.Spacing] : null,
                ge: (g, _, value) =>
                {
                    if (g.Appearance is { } a)
                    {
                        a.Spacing = Math.Clamp(value, 4, 256);
                    }
                }, tooltip: "Distance between ladder rungs or fence uprights in units");
            TextRow("Material", "Material asset path; blank uses the default surface", r => Index.Ge(r)?.Appearance?.Material ?? "",
                text => ReplaceObjectsCommand.Edit(Context.Project, geRefs, "GE geometry material", ge: g =>
                {
                    if (g.Appearance is { } a)
                    {
                        a.Material = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                    }
                }));
            PanelUi.Caption(_sections, "The geometry follows the endpoints. Moving, copying and undo keep it with the GE.");
        }

        var counts = Ges().Select(g => g.Points.Count).Distinct().ToList();
        if (counts is [int count])
        {
            for (int i = 0; i < count; i++)
            {
                int index = i;
                VecRow($"Point {i + 1}", ["X", "Y", "Z"],
                    r => r.Kind == ObjectKind.Ge && Index.Ge(r) is { } g && index < g.Points.Count ? Arr(g.Points[index]) : null,
                    ge: (g, axis, v) => g.Points[index] = g.Points[index].With(axis, v));
            }
        }
        else
        {
            PanelUi.Caption(_sections, "The selected GEs have different point counts; points edit one GE at a time.");
        }

        Label info = PanelUi.Caption(_sections, "");
        _syncers.Add(() =>
        {
            List<Ge> list = Ges();
            string flags = string.Join(", ", list.Select(g => $"0x{g.Flags:X}").Distinct());
            string warn = list.Any(g => g.Points.Count != (GeKinds.HasThirdPoint(g.Kind) ? 3 : 2))
                ? " · point count does not fit the kind (ladders and pipes have 3, others 2)"
                : "";
            info.Text = $"Flags {flags}{warn}";
        });
    }

    private static string KindName(string kind) => kind switch
    {
        GeKinds.LedgeGrab => "Ledge grab (LG)",
        GeKinds.ZipLine => "Zip line (ZL)",
        GeKinds.Pipe => "Climbable pipe / pole",
        GeKinds.Ladder => "Ladder",
        GeKinds.HandOverHand => "Hand over hand (HOH)",
        GeKinds.Pole => "Pole marker",
        GeKinds.Fence => "Fence",
        _ => kind,
    };

    // ---- editor state ---------------------------------------------------------------------------------------------

    private void EditorSection()
    {
        PanelUi.Header(_sections, "Editor");
        MapProject p = Context.Project;
        List<EditorMeta> Metas() => _refs.Select(Index.Meta).OfType<EditorMeta>().ToList();
        List<string> layers = ObjectInfo.LayerNames(p);
        OptionRow("Layer", "The editor layer", layers.Select((l, i) => (l, i)).ToList(),
            () => Metas().Select(m => layers.FindIndex(l => string.Equals(l, ObjectInfo.LayerName(m), StringComparison.OrdinalIgnoreCase))).ToList(),
            id => Edits.SetMeta(Context.Project, _refs, $"Move to layer {layers[id]}", m => m.Layer = ObjectInfo.LayerValue(layers[id])));
        TextRow("Folder", "Outliner folder, '/' separated", r => Index.Meta(r)?.Folder,
            text => Edits.SetMeta(Context.Project, _refs, "Set folder", m => m.Folder = text.Trim().Trim('/')));
        CheckRow("Hidden", "Hidden in the views (kept across builds and saves)", () => Metas().Select(m => m.Hidden).ToList(),
            on => Edits.SetMeta(Context.Project, _refs, on ? "Hide" : "Show", m => m.Hidden = on));
        CheckRow("Locked", "Cannot be selected or moved in the views", () => Metas().Select(m => m.Locked).ToList(),
            on => Edits.SetMeta(Context.Project, _refs, on ? "Lock" : "Unlock", m => m.Locked = on));
        TextRow("Note", "A note for the author (never compiled)", r => Index.Meta(r)?.Note ?? "",
            text => Edits.SetMeta(Context.Project, _refs, "Set note", m => m.Note = text.Trim().Length == 0 ? null : text));
        Label prefab = PanelUi.Caption(_sections, "");
        _syncers.Add(() =>
        {
            var instances = Metas().Select(m => m.PrefabInstance).OfType<string>().Distinct().ToList();
            prefab.Visible = instances.Count > 0;
            prefab.Text = "Prefab instance: " + string.Join(", ", instances);
        });
    }

    // ---- generic rows -------------------------------------------------------------------------------------------

    /// <summary>A text box applied on Enter or leaving it (mixed values show blank).</summary>
    private void TextRow(string label, string tooltip, Func<ObjectRef, string?> read, Func<string, IEditCommand> commit)
    {
        HBoxContainer row = PanelUi.Row(_sections, label, tooltip, LabelWidth);
        var edit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, SelectAllOnFocus = true };
        row.AddChild(edit);
        string shown = "";
        void Final(string text)
        {
            if (text != shown)
            {
                shown = text;
                Execute(commit(text));
            }
        }

        edit.TextSubmitted += Final;
        edit.FocusExited += () => Final(edit.Text);
        _syncers.Add(() =>
        {
            if (edit.HasFocus())
            {
                return;
            }

            var values = _refs.Select(read).Where(v => v is not null).Distinct().ToList();
            bool mixed = values.Count > 1 || _refs.Select(read).Any(v => v is null);
            shown = mixed ? "" : values.FirstOrDefault() ?? "";
            edit.Text = shown;
            PanelMarks.SetMixed(edit, mixed);
        });
    }

    private void OptionRow(string label, string tooltip, IReadOnlyList<(string Text, int Id)> items, Func<List<int>> read, Func<int, IEditCommand> commit)
    {
        HBoxContainer row = PanelUi.Row(_sections, label, tooltip, LabelWidth);
        OptionButton option = PanelUi.Options(row);
        option.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foreach ((string text, int id) in items)
        {
            option.AddItem(text, id);
        }

        option.ItemSelected += index => Execute(commit(option.GetItemId((int)index)));
        _syncers.Add(() =>
        {
            var values = read().Distinct().ToList();
            PanelMarks.Choose(option, values is [int v] && option.GetItemIndex(v) is int i and >= 0 ? i : -1, values.Count > 1);
        });
    }

    private void CheckRow(string label, string tooltip, Func<List<bool>> read, Func<bool, IEditCommand> commit)
    {
        HBoxContainer row = PanelUi.Row(_sections, label, tooltip, LabelWidth);
        var box = new CheckBox();
        box.Toggled += on => Execute(commit(on));
        row.AddChild(box);
        _syncers.Add(() =>
        {
            var values = read().Distinct().ToList();
            box.SetPressedNoSignal(values is [true]);
            PanelMarks.SetMixed(box, values.Count > 1);
            box.TooltipText = values.Count > 1 ? $"{PanelUi.MixedTooltip}: click to set it on every object" : "";
        });
    }

    /// <summary>Like Edit > Select All of Same Class: actors match on the full class name, and hidden or locked
    /// objects are left out.</summary>
    private void SelectSameClass()
    {
        string? Key(ObjectRef r) => r.Kind == ObjectKind.Actor ? Index.Actor(r)?.Class : Index.Class(r);
        var classes = Context.Selection.Items.Select(Key).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        Context.Selection.Set(ObjectFilter.Pickable(Context.Project).Where(r => Key(r) is { } c && classes.Contains(c)));
    }
}
