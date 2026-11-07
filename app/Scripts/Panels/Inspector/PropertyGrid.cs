using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Lightmaps;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// Class properties of one or more property bags, from the class catalog's schema (grouped by category, enum
/// dropdowns, bool boxes, vectors, rotators in degrees, colours with a swatch that opens a colour picker, actor
/// pickers, asset and class paths, JSON for the rest) or, with no schema, the raw stored values. Names show as words ("Collide actors"; the Unreal name is in the
/// tooltip and search finds both). Stored values have a bright name and a reset button; defaults show dim. With several bags, a property that differs shows as mixed and an edit sets only what was
/// edited. Categories build their rows when first opened, so selecting stays fast on big classes.
/// </summary>
public partial class PropertyGrid : VBoxContainer
{
    private const string OtherCategory = "Other";
    private const int MinNamePercent = 15;
    private const int MaxNamePercent = 85;

    /// <summary>Fold state per category, kept for the session (folding Display stays folded).</summary>
    private static readonly Dictionary<string, bool> s_folded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The names' share of a row in percent, set by dragging the line between names and values (kept in the
    /// settings, the same for every grid).</summary>
    private static int s_namePercent = Math.Clamp(AppSettings.GetInt("inspector", "name_percent", 45), MinNamePercent, MaxNamePercent);

    private readonly List<GridRow> _rows = new();
    private readonly List<GridCategory> _categories = new();
    private readonly HashSet<string> _schemaNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>UE2 hue, saturation and brightness byte triples (LightHue, AmbientHue...) by each member's name: a
    /// colour row with the triple's swatch goes before the first of its rows shown.</summary>
    private readonly Dictionary<string, HsbColour> _hsb = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hsbBuilt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Property names per class, for stored values outside the common schema (see <see cref="Applies"/>).</summary>
    private readonly Dictionary<string, HashSet<string>> _classProps = new(StringComparer.OrdinalIgnoreCase);
    private EditorContext _context = null!;
    private LiveEdit _live = null!;
    private PropSource? _source;

    /// <summary>The map the rows were built for: rows removed after another map opens must not edit the new one.</summary>
    private MapProject? _builtFor;
    private bool _syncing;
    private bool _splitting;

    /// <summary>The swatch whose picker is open, and the signature from before it opened: rows are not rebuilt under a
    /// drag (which would close the picker) when it stores a value "Changed only" would show.</summary>
    private ColourSwatch? _picking;
    private string _pickingSignature = "";

    /// <summary>The history position when the open picker opened (see <see cref="CancelPicks"/>).</summary>
    private int _pickedFrom;

    /// <summary>A colour picker closed: rows held back while it was open may be due.</summary>
    public event Action? PickerClosed;

    public string Filter { get; set; } = "";

    public bool StoredOnly { get; set; }
    public bool CommonOnly { get; set; }

    /// <summary>Changes when the grid needs new rows (a stored key appeared or went away outside the schema, or under
    /// "stored only").</summary>
    public string Signature
    {
        get
        {
            if (_source is null)
            {
                return "";
            }

            if (_picking is not null)
            {
                return _pickingSignature;
            }

            IEnumerable<string> keys = _source.Owners(_context.Project).SelectMany(o => o.Props.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
            if (!StoredOnly)
            {
                keys = keys.Where(k => !_schemaNames.Contains(k));
            }

            return string.Join(',', keys.Order(StringComparer.OrdinalIgnoreCase));
        }
    }

    internal void Init(EditorContext context)
    {
        _context = context;
        _live = new LiveEdit(this, context);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", EditorTheme.SpaceXxs);
    }

    /// <summary>Applies any edit still waiting for the typing pause.</summary>
    public void Flush() => _live.Flush();

    /// <summary>Drops an edit waiting for the typing pause (another map was opened).</summary>
    public void Cancel() => _live.Cancel();

    /// <summary>Rebuilds the rows for a source (null clears the grid).</summary>
    internal void ShowSource(PropSource? source)
    {
        _live.Flush();
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }

        _rows.Clear();
        _categories.Clear();
        _schemaNames.Clear();
        _hsb.Clear();
        _hsbBuilt.Clear();
        _classProps.Clear();
        _picking = null;
        _source = source;
        _builtFor = _context.Project;
        if (source is null)
        {
            return;
        }

        MapProject p = _context.Project;
        IReadOnlyList<PropOwner> owners = source.Owners(p);
        List<PropField> fields = Fields(source, owners);
        string[] words = TextMatch.Words(Filter);
        var shown = fields.Where(f => (!CommonOnly || (OpenScct.Editor.Core.Classes.PropertyHelp.Common(f.Name) && owners.All(o => OpenScct.Editor.Core.Classes.PropertyHelp.Find(EditorServices.Classes, o.Class, f.Name) is not null))) && (words.Length == 0 || TextMatch.AllWords(words, $"{f.Name} {PanelUi.DisplayName(f.Name)} {f.Category} {f.TypeText}"))
            && (!StoredOnly || owners.Any(o => o.Props.Contains(f.Name)))).ToList();
        if (shown.Count == 0)
        {
            PanelUi.Caption(this, owners.Count == 0 ? "" : words.Length > 0 ? "No properties match the filter." : "No properties are changed from the class defaults.");
        }

        // Inside another fold (Map info), categories are flat titles, not a second band of raised bars.
        bool nested = FindParent<FoldableContainer>(this) is not null;
        foreach (IGrouping<string, PropField> group in shown.GroupBy(f => f.Category, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key == OtherCategory ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            bool anyStored = group.Any(f => owners.Any(o => o.Props.Contains(f.Name)));
            bool folded = !CommonOnly && words.Length == 0 && !StoredOnly && s_folded.GetValueOrDefault(group.Key, !anyStored);
            var box = new FoldableContainer
            {
                Title = PanelUi.DisplayName(group.Key), Folded = folded, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = group.Key,
                ThemeTypeVariation = nested ? "FoldNested" : "",
            };
            var content = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            content.AddThemeConstantOverride("separation", EditorTheme.SpaceXxs);
            box.AddChild(content);
            AddChild(box);
            var category = new GridCategory(group.Key, box, content, group.ToList());
            _categories.Add(category);
            box.FoldingChanged += isFolded =>
            {
                s_folded[category.Name] = isFolded;
                if (!isFolded && !category.Built)
                {
                    BuildRows(category);
                    Sync();
                }
            };
            if (!folded)
            {
                BuildRows(category);
            }
        }

        if (source.AllowAdd && !CommonOnly)
        {
            AddPropertyRow();
        }

        Sync();
    }

    /// <summary>Re-reads every built row's value from the project (rows being typed in keep their text).</summary>
    public void Sync()
    {
        if (_source is null)
        {
            return;
        }

        IReadOnlyList<PropOwner> owners = _source.Owners(_context.Project);
        _syncing = true;
        try
        {
            foreach (GridRow row in _rows)
            {
                RowState state = row.Hsb is { } hsb ? HsbState(owners, hsb) : StateOf(owners, row.Field);
                row.Label.AddThemeColorOverride("font_color", state.Stored ? EditorTheme.Text : EditorTheme.TextDim);
                row.Label.TooltipText = row.Hsb is { } h ? HsbTooltip(h, state) : Tooltip(row.Field, state);
                // Hidden but keeping its place, so the value fields of set and unset rows line up.
                row.Reset.Modulate = state.Stored ? Colors.White : Colors.Transparent;
                row.Reset.Disabled = !state.Stored;
                row.Reset.MouseFilter = state.Stored ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
                if (!row.Focused())
                {
                    row.Show(state);
                }
            }

            foreach (GridCategory c in _categories)
            {
                int stored = c.Fields.Count(f => owners.Any(o => o.Props.Contains(f.Name)));
                c.Box.Title = stored > 0 ? $"{PanelUi.DisplayName(c.Name)} ({stored} changed)" : PanelUi.DisplayName(c.Name);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    // ---- schema ---------------------------------------------------------------------------------------------------

    private List<PropField> Fields(PropSource source, IReadOnlyList<PropOwner> owners)
    {
        IClassCatalog? catalog = EditorServices.Classes;
        var classes = owners.Select(o => o.Class).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool schema = source.Schema is not null
            || (catalog is not null && classes.Count > 0 && classes.All(c => c.Length > 0 && catalog.Find(c) is not null));
        var fields = new List<PropField>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (source.Schema is { } fixedSchema)
        {
            // A struct the catalog does not describe (a game mode's settings): the source lists its fields.
            foreach (PropInfo info in fixedSchema)
            {
                if (seen.Add(info.Name) && _schemaNames.Add(info.Name) && PropField.KindOf(info, catalog) is { } kind)
                {
                    fields.Add(new PropField(info.Name, kind, info.Category ?? OtherCategory, info, kind == PropKind.Enum ? EnumTable.Values(info.Enum) : null));
                }
            }
        }
        else if (schema)
        {
            // Properties every class has (multi-edit across classes), in the first class's order.
            var common = classes.Skip(1).Select(c => new HashSet<string>(catalog!.AllProperties(c).Select(i => i.Name), StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (PropInfo info in catalog!.AllProperties(classes[0]))
            {
                if (!seen.Add(info.Name) || source.Skip.Contains(info.Name) || common.Any(s => !s.Contains(info.Name)))
                {
                    continue;
                }

                _schemaNames.Add(info.Name);
                bool stored = owners.Any(o => o.Props.Contains(info.Name));
                if ((!info.Editable && !stored) || PropField.KindOf(info, catalog) is not { } kind)
                {
                    continue;
                }

                string category = info.Category is { Length: > 0 } cat ? cat.Trim('_') : ShortName(info.DeclaringClass) ?? OtherCategory;
                fields.Add(new PropField(info.Name, kind, category, info, kind == PropKind.Enum ? EnumTable.Values(info.Enum) : null));
            }
        }

        // Stored values the schema does not list (or every value, with no schema).
        foreach (string key in owners.SelectMany(o => o.Props.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Contains(key) || source.Skip.Contains(key))
            {
                continue;
            }

            seen.Add(key);
            SvJsonNode? sample = owners.Select(o => o.Props[key]).FirstOrDefault(v => v is not null and not SvJsonLiteral { Kind: SvJsonKind.Null });
            fields.Add(new PropField(key, PropField.KindOf(sample), schema ? OtherCategory : "Properties", null, null));
        }

        FindHsbColours(fields);
        return fields;
    }

    /// <summary>Finds the UE2 colours stored as bytes: XHue with XSaturation (and XBrightness when there is one), as
    /// lights (LightHue) and zones (AmbientHue) have them.</summary>
    private void FindHsbColours(List<PropField> fields)
    {
        var byName = new Dictionary<string, PropField>(StringComparer.OrdinalIgnoreCase);
        foreach (PropField f in fields)
        {
            byName.TryAdd(f.Name, f);
        }

        foreach (PropField f in fields)
        {
            const string Hue = "Hue";
            if (f.Kind != PropKind.Int || f.Name.Length <= Hue.Length || !f.Name.EndsWith(Hue, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string prefix = f.Name[..^Hue.Length];
            if (byName.GetValueOrDefault(prefix + "Saturation") is { Kind: PropKind.Int } saturation)
            {
                PropField? brightness = byName.GetValueOrDefault(prefix + "Brightness") is { Kind: PropKind.Int or PropKind.Float } b ? b : null;
                var hsb = new HsbColour(prefix, f, saturation, brightness);
                foreach (PropField member in hsb.Fields)
                {
                    _hsb.TryAdd(member.Name, hsb);
                }
            }
        }
    }

    private static string? ShortName(string? cls) => cls is null ? null : cls[(cls.LastIndexOf('.') + 1)..];

    /// <summary>The class default of a property, or null when unknown (no catalog, or the class is not in it). A
    /// schema property the class defaults do not list has the UE2 zero value (0, false, "", None).</summary>
    private static SvJsonNode? Default(PropOwner owner, PropField field)
    {
        IClassCatalog? catalog = EditorServices.Classes;
        if (catalog is null || owner.Class.Length == 0 || catalog.Find(owner.Class) is null)
        {
            return null;
        }

        return catalog.Default(owner.Class, field.Name) ?? (field.Info is null ? null : PropText.Zero(field.Kind));
    }

    private static SvJsonNode? Effective(PropOwner owner, PropField field) => owner.Props[field.Name] ?? Default(owner, field);

    /// <summary>Stores only what differs from the class default (as the original map does).</summary>
    private static SvJsonNode? Normalise(PropOwner owner, PropField field, SvJsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        SvJsonNode? d = Default(owner, field);
        return d is not null && PropText.Canon(d) == PropText.Canon(value) ? null : value;
    }

    /// <summary>
    /// False for an owner a stored value outside the common schema does not belong to: with objects of several classes
    /// selected, a property one class has (or one object stores) shows under Other, and an edit there must not write it
    /// onto objects whose class has no such property. Their values are left out of the row too.
    /// </summary>
    private bool Applies(PropOwner owner, PropField field)
    {
        if (field.Info is not null || owner.Props.Contains(field.Name))
        {
            return true;
        }

        IClassCatalog? catalog = EditorServices.Classes;
        if (catalog is null || owner.Class.Length == 0 || catalog.Find(owner.Class) is null)
        {
            return true;
        }

        if (!_classProps.TryGetValue(owner.Class, out HashSet<string>? names))
        {
            names = new HashSet<string>(catalog.AllProperties(owner.Class).Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
            _classProps[owner.Class] = names;
        }

        return names.Contains(field.Name);
    }

    private RowState StateOf(IReadOnlyList<PropOwner> owners, PropField field)
    {
        IReadOnlyList<PropOwner> all = owners;
        owners = owners.Where(o => Applies(o, field)).ToList();
        var values = owners.Select(o => Effective(o, field)).ToList();
        bool mixed = values.Select(PropText.Canon).Distinct(StringComparer.Ordinal).Skip(1).Any();
        bool stored = all.Any(o => o.Props.Contains(field.Name));
        return new RowState(values, stored, mixed, owners.Count > 0 ? Default(owners[0], field) : null);
    }

    private string Tooltip(PropField f, RowState s)
    {
        var lines = new List<string> { PanelUi.DisplayName(f.Name), $"{f.Name}: {f.TypeText}" };
        string? help = _source?.Owners(_context.Project).Select(o => OpenScct.Editor.Core.Classes.PropertyHelp.Find(EditorServices.Classes, o.Class, f.Name)).FirstOrDefault(t => t is not null);
        if (help is not null) lines.Insert(0, help + "\n");
        if (f.Info?.DeclaringClass is { } declaring)
        {
            lines.Add("Declared in " + declaring);
        }

        lines.Add(s.Default is null ? "No class default" : "Default: " + PropText.Canon(s.Default));
        lines.Add(s.Stored ? "Stored in the map (reset to use the default)" : "Using the class default");
        return string.Join('\n', lines);
    }

    // ---- editing --------------------------------------------------------------------------------------------------

    private void Apply(PropField field, Func<PropOwner, SvJsonNode?, SvJsonNode?> change, bool final) =>
        _live.Change(() => Commit(field, change), final, _source?.Live ?? false);

    private void Commit(PropField field, Func<PropOwner, SvJsonNode?, SvJsonNode?> change)
    {
        if (_source is not { } source || !ReferenceEquals(_context.Project, _builtFor))
        {
            return;
        }

        MapProject p = _context.Project;
        SvJsonNode? NewValue(PropOwner o) => Applies(o, field) ? Normalise(o, field, change(o, Effective(o, field))) : o.Props[field.Name];
        if (!source.Owners(p).Any(o => PropText.Canon(NewValue(o)) != PropText.Canon(o.Props[field.Name])))
        {
            return;
        }

        _context.Execute(source.Set(p, field.Name, NewValue));
    }

    /// <summary>Sets several properties as one command. <paramref name="change"/> gives an owner's new value of a field
    /// (Set false leaves the stored value as it is).</summary>
    private void CommitMany(string name, IReadOnlyList<PropField> fields, Func<PropOwner, PropField, (bool Set, SvJsonNode? Value)> change)
    {
        if (_source is not { } source || !ReferenceEquals(_context.Project, _builtFor))
        {
            return;
        }

        MapProject p = _context.Project;
        SvJsonNode? NewValue(PropOwner o, PropField f) => Applies(o, f) && change(o, f) is (true, var v) ? Normalise(o, f, v) : o.Props[f.Name];
        if (!source.Owners(p).Any(o => fields.Any(f => PropText.Canon(NewValue(o, f)) != PropText.Canon(o.Props[f.Name]))))
        {
            return;
        }

        var byName = fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        _context.Execute(source.SetMany(p, name, fields.Select(f => f.Name).ToList(), (o, key) => NewValue(o, byName[key])));
    }

    /// <summary>A colour from the picker: while dragging it applies at once where edits may apply live (merging into one
    /// undo step), and at the end of the drag everywhere; the end closes the step.</summary>
    private void Pick(Action commit, bool final)
    {
        if (final || (_source?.Live ?? false))
        {
            commit();
        }

        if (final)
        {
            _context.Session.Commands.EndMerge();
        }
    }

    private void BuildRows(GridCategory category)
    {
        category.Built = true;
        foreach (PropField f in category.Fields)
        {
            // A UE2 hue, saturation and brightness triple: its colour row before the first of its rows shown (so it is
            // there under "Changed only" whichever of the three is stored).
            if (_hsb.TryGetValue(f.Name, out HsbColour? hsb) && _hsbBuilt.Add(hsb.Prefix))
            {
                RowEditor colour = HsbEditor(hsb);
                (Label hsbLabel, Button hsbReset) = AddRow(category, PanelUi.DisplayName(hsb.Prefix) + " colour", colour.Editor,
                    $"Reset the {hsb.Title} to the class default", () => CommitMany($"Reset {hsb.Title}", hsb.Fields, (_, _) => (true, null)));
                _rows.Add(new GridRow(hsb.Hue, hsbLabel, hsbReset, colour.Show, colour.Focused, hsb));
            }

            RowEditor editor = CreateEditor(f);
            (Label label, Button reset) = AddRow(category, PanelUi.DisplayName(f.Name), editor.Editor, "Reset to the class default",
                () => Apply(f, (_, _) => null, final: true));
            if (editor.Below is { } below)
            {
                category.Content.AddChild(below);
            }

            _rows.Add(new GridRow(f, label, reset, editor.Show, editor.Focused));
        }
    }

    /// <summary>A row: the name, the handle between names and values, the value editor and a reset button.</summary>
    private (Label Label, Button Reset) AddRow(GridCategory category, string name, Control editor, string resetTooltip, Action reset)
    {
        HBoxContainer row = PanelUi.Row(category.Content, name, labelWidth: 0);
        var label = (Label)row.GetChild(0);
        // Names and values split the row's width (names shortened with an ellipsis, the line between them can be
        // dragged), so a long name or a row of buttons never makes the dock wider.
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.SizeFlagsStretchRatio = NameRatio(s_namePercent);
        editor.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(NameSplit(label, editor));
        row.AddChild(editor);
        return (label, PanelUi.IconButton(row, "reset", resetTooltip, reset));
    }

    /// <summary>The thin handle between a row's name and value; dragging it resizes the names of every row.</summary>
    private Control NameSplit(Label label, Control editor)
    {
        var handle = new Control
        {
            CustomMinimumSize = new Vector2(4, 0),
            MouseDefaultCursorShape = CursorShape.Hsize,
            TooltipText = "Drag to give names or values more room",
        };
        handle.GuiInput += e =>
        {
            switch (e)
            {
                case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                    _splitting = b.Pressed;
                    if (!b.Pressed)
                    {
                        AppSettings.Set("inspector", "name_percent", s_namePercent);
                    }

                    handle.AcceptEvent();
                    break;
                case InputEventMouseMotion m when _splitting:
                    float room = label.Size.X + editor.Size.X;
                    SetNamePercent((int)Math.Round(100 * (label.Size.X + m.Position.X) / Math.Max(room, 1)));
                    handle.AcceptEvent();
                    break;
            }
        };
        return handle;
    }

    private void SetNamePercent(int percent)
    {
        percent = Math.Clamp(percent, MinNamePercent, MaxNamePercent);
        if (percent == s_namePercent)
        {
            return;
        }

        s_namePercent = percent;
        foreach (GridRow row in _rows)
        {
            row.Label.SizeFlagsStretchRatio = NameRatio(percent);
        }
    }

    /// <summary>The name label's stretch ratio for a share of the row; values have ratio 1.</summary>
    private static float NameRatio(int percent) => percent / (100f - percent);

    private RowEditor CreateEditor(PropField f) => f.Kind switch
    {
        PropKind.Int or PropKind.Float => new(NumberEditor(f)),
        PropKind.Bool => new(BoolEditor(f)),
        PropKind.Enum => new(EnumEditor(f)),
        PropKind.Vector or PropKind.Rotator => new(AxisEditor(f)),
        PropKind.Color => ColourEditor(f),
        _ => new(TextEditor(f)),
    };

    private (Control, Action<RowState>, Func<bool>) BoolEditor(PropField f)
    {
        var box = new CheckBox();
        box.Toggled += on =>
        {
            if (!_syncing)
            {
                Apply(f, (_, _) => SvJsonNode.Bool(on), final: true);
            }
        };
        return (box, s =>
        {
            bool on = s.Values.Count > 0 && s.Values.All(v => v is SvJsonLiteral { Kind: SvJsonKind.True });
            box.SetPressedNoSignal(on);
            PanelMarks.SetMixed(box, s.Mixed);
            box.TooltipText = s.Mixed ? $"{PanelUi.MixedTooltip}: click to set it on every object" : "";
        }, () => false);
    }

    private (Control, Action<RowState>, Func<bool>) EnumEditor(PropField f)
    {
        var option = new OptionButton { FitToLongestItem = false, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        PanelUi.CapPopup(option.GetPopup());
        IReadOnlyList<string> names = f.EnumValues ?? [];
        for (int i = 0; i < names.Count; i++)
        {
            option.AddItem($"{names[i]}", i);
        }

        option.ItemSelected += index =>
        {
            if (!_syncing)
            {
                int value = option.GetItemId((int)index);
                Apply(f, (_, _) => SvJsonNode.Integer(value), final: true);
            }
        };
        return (option, s =>
        {
            int index = -1;
            if (!s.Mixed && s.Values.FirstOrDefault() is SvJsonNumber n && n.TryGetInt32(out int v) && v >= 0 && v < names.Count)
            {
                index = v;
            }

            SvJsonNode? value = s.Values.FirstOrDefault();
            PanelMarks.Choose(option, index, s.Mixed, value is null ? "(not set)" : PropText.Canon(value) + " (not in the list)");
            option.TooltipText = s.Mixed ? PanelUi.MixedTooltip : index >= 0 ? names[index]
                : value is null ? "Not set, and the class has no default" : "Value outside the enum: " + PropText.Canon(value);
        }, () => false);
    }

    private (Control, Action<RowState>, Func<bool>) AxisEditor(PropField f)
    {
        string[] axes = PropText.Axes(f.Kind);
        AxisFields fields = AxisFields.Create(f.Kind == PropKind.Rotator ? ["Pitch°", "Yaw°", "Roll°"] : axes);
        fields.Edited += (axis, v, final) => Apply(f, (_, cur) => PropText.WithComponent(cur, f.Kind, axis, v), final);
        return (fields, s =>
        {
            var values = new double?[axes.Length];
            for (int i = 0; i < axes.Length; i++)
            {
                var parts = s.Values.Select(v => PropText.Component(v, f.Kind, i)).Distinct().ToList();
                values[i] = parts.Count == 1 ? parts[0] : null;
            }

            fields.SetValues(values);
        }, () => fields.HasFocusInside);
    }

    /// <summary>A UE2 Color struct: a swatch bar whose picker sets R, G and B (each object keeps its alpha), and the
    /// R G B A boxes on a line of their own under it, wide enough for three digits in a narrow dock.</summary>
    private RowEditor ColourEditor(PropField f)
    {
        (Control fields, Action<RowState> show, Func<bool> focused) = AxisEditor(f);
        ColourSwatch swatch = ColourSwatch.Create($"{PanelUi.DisplayName(f.Name)}: click to pick the colour (its alpha stays as it is)",
            "the colour picked goes to every object, each keeping its alpha");
        // The colour the picker opened on: Escape in the picker puts it back, which undoes the picks.
        Color start = default;
        swatch.Opening += () =>
        {
            OpenPicker(swatch);
            start = swatch.Color;
        };
        swatch.Ended += () => ClosePicker(swatch);
        swatch.Picked += (c, final) => Pick(() =>
        {
            if (c == start)
            {
                CancelPicks();
            }
            else
            {
                Commit(f, (_, cur) => PropText.WithRgb(cur, Byte(c.R), Byte(c.G), Byte(c.B)));
            }
        }, final);

        var below = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        below.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        below.AddChild(new Control { CustomMinimumSize = new Vector2(EditorTheme.SpaceL, 0), MouseFilter = MouseFilterEnum.Ignore });
        fields.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        below.AddChild(fields);
        // Lined up with the value boxes above, which end before the reset button.
        below.AddChild(new Control { CustomMinimumSize = new Vector2(EditorTheme.IconButtonSize, 0), MouseFilter = MouseFilterEnum.Ignore });
        return new RowEditor(swatch, s =>
        {
            show(s);
            var colours = s.Values.Select(v => (R: PropText.Component(v, PropKind.Color, 0), G: PropText.Component(v, PropKind.Color, 1),
                B: PropText.Component(v, PropKind.Color, 2))).Distinct().ToList();
            (double r, double g, double b) = colours.FirstOrDefault();
            swatch.ShowColour(new Color((float)(r / 255), (float)(g / 255), (float)(b / 255)), colours.Count > 1);
        }, focused, below);
    }

    private static int Byte(float channel) => (int)Math.Round(Math.Clamp(channel, 0f, 1f) * 255);

    /// <summary>A picker opens: typing still waiting is applied and the drag starts a new undo step.</summary>
    private void OpenPicker(ColourSwatch swatch)
    {
        _live.Flush();
        _context.Session.Commands.EndMerge();
        _pickingSignature = Signature;
        _pickedFrom = _context.Session.Commands.Position;
        _picking = swatch;
    }

    /// <summary>Escape in a picker (Godot puts back the colour it opened on): the picks made since it opened are
    /// undone, as cancelling a drag in the views undoes it, so a cancelled pick leaves no step behind.</summary>
    private void CancelPicks()
    {
        while (_context.Session.Commands.Position > _pickedFrom && _context.Session.Commands.Undo())
        {
        }
    }

    private void ClosePicker(ColourSwatch swatch)
    {
        _context.Session.Commands.EndMerge();
        if (ReferenceEquals(_picking, swatch))
        {
            _picking = null;
            PickerClosed?.Invoke();
        }
    }

    /// <summary>
    /// The colour row of a UE2 hue, saturation and brightness triple: a swatch bar of the colour, with the number rows
    /// under it. The swatch shows the hue and saturation exactly as FGetHSV makes them (UE2's hue triangle; saturation
    /// 0 the full colour, 255 white). A light's brightest channel is brightness / 255; a zone's ambient is FGetHSV's own
    /// colour, brightness curve and all, as the game lights pawns with it. In the picker only what a drag really moves
    /// is written: a hue change sets the hue byte nearest it, a saturation change the saturation byte, and a change in
    /// how bright it is scales the brightness (each object's by the same factor, so a light above 255 keeps its value
    /// until the brightness is changed). Moves within the picker's own steps (its sliders snap to whole degrees and
    /// percent, its square to pixels) count as no move.
    /// </summary>
    private RowEditor HsbEditor(HsbColour hsb)
    {
        ColourSwatch swatch = ColourSwatch.Create(
            $"{PanelUi.DisplayName(hsb.Prefix)} colour from {(hsb.Brightness is null ? "hue and saturation" : "hue, saturation and brightness")}: click to pick it",
            hsb.Brightness is null ? "the colour picked goes to every object"
                : "a hue or saturation picked goes to every object; a brightness change scales each object's own");

        // The colour the picker opened on (Escape puts it back). What a drag starts from: the picker's colour, the first
        // object's hue and saturation, every object's bytes, and what the drag has moved so far. Each drag starts
        // afresh from where the last one ended, so a hue-bar drag after a drag in the square moves only the hue.
        Color opened = default;
        Color start = default;
        (int Hue, int Saturation) first = default;
        var from = new Dictionary<string, (int Hue, int Saturation, double Brightness)>(StringComparer.Ordinal);
        bool hueMoved = false, saturationMoved = false, valueMoved = false;
        void StartDrag(Color colour)
        {
            start = colour;
            hueMoved = saturationMoved = valueMoved = false;
            from.Clear();
            foreach (PropOwner o in HsbOwners(hsb))
            {
                from[o.Key] = ((int)Num(o, hsb.Hue), (int)Num(o, hsb.Saturation), hsb.Brightness is null ? 255 : Num(o, hsb.Brightness));
            }

            first = from.Count > 0 ? (from.First().Value.Hue, from.First().Value.Saturation) : (0, 255);
        }

        swatch.Opening += () =>
        {
            OpenPicker(swatch);
            opened = swatch.Color;
            StartDrag(opened);
        };
        swatch.Ended += () => ClosePicker(swatch);
        swatch.Picked += (c, final) => Pick(() =>
        {
            // The colour the picker opened on: Escape in the picker puts it back, which undoes the picks.
            if (c == opened)
            {
                CancelPicks();
                return;
            }

            // Black has no hue: dragging to it changes only the brightness. A grey start (a white light) has no hue
            // either, so the first colour picked from it sets both. The picker rounds to its slider steps and to 8-bit
            // channels, which moves the hue and saturation of a dark or pale colour further (one channel step is a
            // bigger share of a small spread between the brightest and dimmest channel), so the allowance grows there.
            if (c.V > 0.5f / 255)
            {
                float spread = start.V * start.S;
                if (spread * 255 < 3)
                {
                    bool coloured = c.V * c.S * 255 >= 3;
                    hueMoved |= coloured;
                    saturationMoved |= coloured;
                }
                else
                {
                    hueMoved |= HueDistance(c.H, start.H) > HueStep + (1f / (255 * 6 * spread));
                    saturationMoved |= Math.Abs(c.S - start.S) > SaturationStep + (1f / (255 * start.V));
                }
            }

            valueMoved |= hsb.Brightness is not null && Math.Abs(c.V - start.V) > ValueStep;
            // The bytes nearest the colour picked, fitted from the first object's (only what moved is fitted).
            (int Hue, int Saturation) picked = hueMoved || saturationMoved
                ? LightColour.FromColour(c.R, c.G, c.B, first.Hue, first.Saturation, fitHue: hueMoved, fitSaturation: saturationMoved)
                : first;
            CommitMany($"Set {hsb.Title}", hsb.Fields, (o, field) =>
            {
                if (!from.TryGetValue(o.Key, out var was))
                {
                    return (false, null);
                }

                // A hue picked goes to every object, a saturation picked too; each keeps the byte the drag left alone.
                if (field == hsb.Hue)
                {
                    return hueMoved ? (true, SvJsonNode.Integer(picked.Hue)) : (false, null);
                }

                if (field == hsb.Saturation)
                {
                    return saturationMoved ? (true, SvJsonNode.Integer(picked.Saturation)) : (false, null);
                }

                if (!valueMoved)
                {
                    return (false, null);
                }

                double level = start.V > 0 ? Level(hsb, was.Brightness) * c.V / start.V : c.V / Peak(hsb, was.Hue, was.Saturation);
                double b = Math.Max(0, Math.Round(hsb.Ambient ? LightColour.ValueOf(level) : level * 255));
                return field.Kind == PropKind.Float ? (true, SvJsonNode.Float((float)b)) : (true, SvJsonNode.Integer((long)Math.Min(b, 255)));
            });
            if (final)
            {
                StartDrag(c);
            }
        }, final);

        return new RowEditor(swatch, _ =>
        {
            var colours = HsbOwners(hsb).Select(o => (H: (int)Num(o, hsb.Hue), S: (int)Num(o, hsb.Saturation),
                B: hsb.Brightness is null ? 255 : Num(o, hsb.Brightness))).Distinct().ToList();
            (int h, int sat, double bright) = colours.Count > 0 ? colours[0] : (0, 255, 255);
            Vec3 chroma = LightColour.Chroma(h, sat);
            float v = (float)Math.Clamp(Level(hsb, bright) * Peak(hsb, h, sat), 0, 1);
            swatch.ShowColour(new Color((float)chroma.X * v, (float)chroma.Y * v, (float)chroma.Z * v), colours.Count > 1);
        }, () => false);
    }

    // The picker's own resolution: its HSV sliders snap to whole degrees and percent, its colour square to pixels
    // (about 0.004 of saturation or value each).
    private const float HueStep = 0.9f / 360;
    private const float SaturationStep = 0.008f;
    private const float ValueStep = 0.008f;

    /// <summary>The owners a triple belongs to.</summary>
    private List<PropOwner> HsbOwners(HsbColour hsb) =>
        (_source?.Owners(_context.Project) ?? []).Where(o => Applies(o, hsb.Hue)).ToList();

    /// <summary>How bright a brightness value makes the colour's brightest channel, before the hue's own peak: a
    /// light's brightness / 255 (past 1 above 255), a zone ambient's FGetHSV curve.</summary>
    private static double Level(HsbColour hsb, double brightness) => hsb.Ambient ? LightColour.Scale(brightness) : brightness / 255;

    /// <summary>The brightest channel of the hue and saturation at full level: 1 for lights (their colour is scaled to
    /// it), FGetHSV's own (below 1 between two primaries) for a zone's ambient.</summary>
    private static double Peak(HsbColour hsb, int hue, int saturation) => hsb.Ambient ? Math.Max(LightColour.Peak(hue, saturation), 1e-6) : 1;

    private static RowState HsbState(IReadOnlyList<PropOwner> owners, HsbColour hsb) =>
        new([], owners.Any(o => hsb.Fields.Any(f => o.Props.Contains(f.Name))), false, null);

    private static string HsbTooltip(HsbColour hsb, RowState s) => string.Join('\n',
        PanelUi.DisplayName(hsb.Prefix) + " colour",
        string.Join(", ", hsb.Fields.Select(f => f.Name)) + " as one colour.",
        "UE2 hue runs red 0, green 85, blue 170 (red again at 255); saturation 0 is the full colour and 255 white.",
        "The picker's H and S are an ordinary colour's, not these bytes.",
        s.Stored ? "Stored in the map (reset to use the class defaults)" : "Using the class defaults");

    private static double Num(PropOwner owner, PropField field) => PropValues.Num(Effective(owner, field));

    /// <summary>The distance between two hues on the 0..1 circle.</summary>
    private static float HueDistance(float a, float b)
    {
        float d = Math.Abs(a - b) % 1f;
        return Math.Min(d, 1f - d);
    }

    private (Control, Action<RowState>, Func<bool>) NumberEditor(PropField f)
    {
        AxisFields field = AxisFields.Number(f.Name);
        field.Edited += (_, value, final) =>
        {
            if (PropText.TryParse(PanelUi.Num(value), f.Kind, out SvJsonNode? parsed))
            {
                Apply(f, (_, _) => parsed, final);
            }
        };
        return (field, state => field.SetValues([state.Mixed ? null : PropValues.Num(state.Values.FirstOrDefault())]),
            () => field.HasFocusInside);
    }

    private (Control, Action<RowState>, Func<bool>) TextEditor(PropField f)
    {
        var row = new HBoxContainer();
        var edit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, SelectAllOnFocus = true };
        row.AddChild(edit);
        // Only typed text is an edit; leaving a box untouched writes nothing.
        bool typed = false;
        // A struct reads as (Zone=ZoneInfo392, ...) and turns into its JSON while the box has the focus.
        SvJsonNode? value = null;
        bool mixed = false;

        void ShowText(bool editing)
        {
            // Setting the text is not typing (also when called outside a sync).
            bool syncing = _syncing;
            _syncing = true;
            edit.Text = mixed ? "" : !editing && PropText.IsStructured(value) ? PropText.Summary(value) : PropText.Display(value, f.Kind);
            _syncing = syncing;
        }

        void Final(string text)
        {
            if (!typed)
            {
                return;
            }

            if (PropText.TryParse(text, f.Kind, out SvJsonNode? v))
            {
                typed = false;
                Apply(f, (_, _) => v, final: true);
            }
            else
            {
                _context.SetStatus($"{f.Name}: '{text}' is not a valid {f.TypeText}.");
            }
        }

        edit.TextChanged += t =>
        {
            if (_syncing)
            {
                return;
            }

            typed = true;
            if (f.Kind != PropKind.Raw && PropText.TryParse(t, f.Kind, out SvJsonNode? v))
            {
                Apply(f, (_, _) => v, final: false);
            }
        };
        edit.TextSubmitted += Final;
        edit.FocusEntered += () =>
        {
            if (PropText.IsStructured(value))
            {
                ShowText(editing: true);
                edit.SelectAll();
            }
        };
        edit.FocusExited += () =>
        {
            Final(edit.Text);
            if (!typed)
            {
                ShowText(editing: false);
            }
        };

        if (f.Kind == PropKind.ActorRef)
        {
            AddActorPicker(row, f, edit);
        }

        return (row, s =>
        {
            typed = false;
            value = s.Values.FirstOrDefault();
            mixed = s.Mixed;
            ShowText(edit.HasFocus());
            PanelMarks.SetMixed(edit, s.Mixed, f.Kind is PropKind.ActorRef or PropKind.AssetRef or PropKind.ClassRef ? "None" : "");
        }, () => edit.HasFocus());
    }

    /// <summary>A dropdown of actors of the property's class, and a button that selects the referenced actor.</summary>
    private void AddActorPicker(HBoxContainer row, PropField f, LineEdit edit)
    {
        const int Cap = 400;
        var pick = new MenuButton
        {
            Icon = Icons.Get("chevron-down", Colors.White), TooltipText = "Pick an actor", ThemeTypeVariation = "IconButton",
            IconAlignment = HorizontalAlignment.Center, CustomMinimumSize = new Vector2(EditorTheme.IconButtonSize, EditorTheme.IconButtonSize),
        };
        PanelUi.CapPopup(pick.GetPopup());
        var ids = new List<string>();
        PopupMenu popup = pick.GetPopup();
        popup.AboutToPopup += () =>
        {
            popup.Clear();
            ids.Clear();
            ids.Add("");
            popup.AddItem("None", 0);
            string cls = f.Info?.ObjectClass ?? "Engine.Actor";
            IClassCatalog? catalog = EditorServices.Classes;
            IEnumerable<Actor> candidates = _context.Project.Actors
                .Where(a => catalog is null || catalog.Find(a.Class) is null || catalog.IsA(a.Class, cls))
                .OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase);
            foreach (Actor a in candidates.Take(Cap))
            {
                ids.Add(a.Id);
                popup.AddItem($"{a.Id}  ({a.ShortClass})", ids.Count - 1);
            }
        };
        popup.IdPressed += id =>
        {
            string target = ids[(int)id];
            Apply(f, (_, _) => target.Length == 0 ? SvJsonNode.Null : PropValues.ActorRef(target), final: true);
        };
        row.AddChild(pick);

        PanelUi.IconButton(row, "goto", "Select the referenced actor and frame it", () =>
        {
            string id = edit.Text.Trim();
            if (_context.Project.FindActor(id) is { } a)
            {
                ObjectRef r = ObjectRef.Actor(a.Id);
                _context.Selection.Set([r]);
                _context.Focus([r]);
            }
        });
    }

    private void AddPropertyRow()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
        var key = new LineEdit { PlaceholderText = "Property", TooltipText = "The name of a property to store", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var value = new LineEdit { PlaceholderText = "Value", TooltipText = "The value: JSON, or plain text for a string", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var add = new Button { Text = "Add", FocusMode = FocusModeEnum.None, TooltipText = "Store a property by name" };
        void Add()
        {
            string name = key.Text.Trim();
            if (name.Length == 0 || _source is not { } source)
            {
                return;
            }

            if (source.Skip.Contains(name))
            {
                _context.SetStatus($"{name} is edited in the inspector's own fields.");
                return;
            }

            SvJsonNode v = PropText.ParseJson(value.Text) ?? SvJsonNode.String(value.Text);
            PropField field = _categories.SelectMany(c => c.Fields).FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? new PropField(name, PropField.KindOf(v), OtherCategory, null, null);
            _live.Change(() => Commit(field, (_, _) => v), final: true);
            key.Text = "";
            value.Text = "";
            EditorLifetime.Defer(() => ShowSource(_source));
        }

        add.Pressed += Add;
        value.TextSubmitted += _ => Add();
        row.AddChild(key);
        row.AddChild(value);
        row.AddChild(add);
        AddChild(row);
    }

    private static T? FindParent<T>(Node node)
        where T : Node
    {
        for (Node? n = node.GetParent(); n is not null; n = n.GetParent())
        {
            if (n is T t)
            {
                return t;
            }
        }

        return null;
    }

    /// <summary>A UE2 colour kept as hue, saturation and (optionally) brightness bytes.</summary>
    private sealed record HsbColour(string Prefix, PropField Hue, PropField Saturation, PropField? Brightness)
    {
        public IReadOnlyList<PropField> Fields { get; } = Brightness is null ? [Hue, Saturation] : [Hue, Saturation, Brightness];

        /// <summary>A zone's ambient (AmbientHue...): FGetHSV's brightness curve applies, not a linear scale.</summary>
        public bool Ambient => Prefix.Equals("Ambient", StringComparison.OrdinalIgnoreCase);

        /// <summary>"light colour", for undo steps.</summary>
        public string Title => PanelUi.DisplayName(Prefix).ToLowerInvariant() + " colour";
    }

    /// <summary>A row's value editor, and a line of its own under the row when it has one (a colour's R G B A).</summary>
    private readonly record struct RowEditor(Control Editor, Action<RowState> Show, Func<bool> Focused, Control? Below = null)
    {
        public RowEditor((Control Editor, Action<RowState> Show, Func<bool> Focused) e)
            : this(e.Editor, e.Show, e.Focused)
        {
        }
    }

    /// <summary>A property's row, or (<see cref="Hsb"/> set) a hue, saturation and brightness triple's colour row.</summary>
    private sealed record GridRow(PropField Field, Label Label, Button Reset, Action<RowState> Show, Func<bool> Focused, HsbColour? Hsb = null);

    private sealed class GridCategory(string name, FoldableContainer box, VBoxContainer content, List<PropField> fields)
    {
        public string Name { get; } = name;

        public FoldableContainer Box { get; } = box;

        public VBoxContainer Content { get; } = content;

        public List<PropField> Fields { get; } = fields;

        public bool Built { get; set; }
    }

    private readonly record struct RowState(IReadOnlyList<SvJsonNode?> Values, bool Stored, bool Mixed, SvJsonNode? Default);
}
