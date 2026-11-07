using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Scripting.Live;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Prefabs;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Panels;

/// <summary>
/// Prefabs: browse the map's, the user library's and the built-in prefabs with pictures; place one at the view centre
/// (or drag it into a view) with its parameters; update, re-parameter, select or break placed instances; and save the
/// selection as a new prefab. Every change goes through the same change-document ops as <c>ose</c> and scripts
/// (<c>prefab.place</c>, <c>prefab.update</c>, <c>prefab.break</c>, <c>prefab.save</c>), as one undo step each.
/// </summary>
[Panel("Prefabs", DockSide.Left, Order = 3)]
public partial class PrefabsPanel : EditorPanel
{
    protected virtual bool Friendly => false;
    protected virtual void DetailsShown(Control heading) { }
    private readonly PrefabThumbnails _thumbnails = new();
    private readonly List<PrefabDefinition> _shown = new();
    private readonly Dictionary<string, Func<SvJsonNode?>> _paramValues = new(StringComparer.Ordinal);
    private List<PrefabDefinition> _all = new();
    private ItemList _list = null!;
    private OptionButton _category = null!;
    private VBoxContainer _details = null!;
    private VBoxContainer _instance = null!;
    private LineEdit _saveName = null!;
    private LineEdit _saveCategory = null!;
    private OptionButton _saveTo = null!;
    private CheckBox _saveLink = null!;
    private ScrollContainer _detailsScroll = null!;
    private DeferredAction _rebuild = null!;
    private DeferredAction _refill = null!;
    private DeferredAction _refreshInstance = null!;
    private string _filter = "";

    /// <summary>A prefab to pick once the list is read (<c>-- --prefab=name</c>, so scripts can place it with --run).</summary>
    private string? _argPrefab = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--prefab=", StringComparison.Ordinal))?["--prefab=".Length..];

    protected override void Build()
    {
        _rebuild = new DeferredAction(Reload);
        _refill = new DeferredAction(Fill);
        _refreshInstance = new DeferredAction(RefreshInstance);
        HFlowContainer bar = PanelUi.Toolbar(this);
        PanelUi.Search(bar, "Search prefabs", t =>
        {
            _filter = t.Trim();
            Fill();
        });
        _category = PanelUi.Options(bar, "Show one category");
        _category.CustomMinimumSize = new Vector2(EditorTheme.NumberFieldWidth, 0);
        _category.ItemSelected += _ => Fill();
        PanelUi.IconButton(bar, "refresh", "Read the prefab folders again", () => _rebuild.Queue());

        // The grid takes the room; the details of the picked prefab scroll below it, and the save form stays at the
        // bottom of the panel.
        var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);
        _list = new ItemList
        {
            IconMode = ItemList.IconModeEnum.Top,
            FixedIconSize = new Vector2I(PrefabThumbnails.Size, PrefabThumbnails.Size),
            FixedColumnWidth = TileWidth,
            MaxColumns = 0,
            SameColumnWidth = true,
            MaxTextLines = 2,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            // Two rows of tiles with their two caption lines at least: the grid is what the panel is for.
            CustomMinimumSize = new Vector2(0, GridMinHeight),
            TooltipText = "Double\u2011click a prefab to place it at the view centre, or drag it into a view to place it there.",
        };
        _list.Resized += FitTiles;
        _list.ItemSelected += _ => ShowDetails();
        _list.ItemActivated += _ => PlaceSelected(null);
        _list.SetDragForwarding(Callable.From<Vector2, Variant>(DragData), Callable.From((Vector2 _, Variant _) => false),
            Callable.From((Vector2 _, Variant _) => { }));
        split.AddChild(_list);

        VBoxContainer lower = PanelUi.Scroll(split);
        _detailsScroll = PanelUi.ScrollOf(lower);
        _detailsScroll.SizeFlagsVertical = SizeFlags.Fill;
        _details = new VBoxContainer();
        lower.AddChild(_details);
        _instance = new VBoxContainer();
        lower.AddChild(_instance);
        if (!Friendly) BuildSave(this);

        _thumbnails.Ready += UpdatePictures;
        Context.ProjectChanged += _ => _refreshInstance.Queue();
        Context.SelectionChanged += () => _refreshInstance.Queue();
        Context.Saved += () => _rebuild.Queue();
        if (!Friendly) RegisterActions();
    }

    public override void Refresh() => _rebuild.Queue();

    /// <summary>The width a grid column aims at: the picture and a little room on each side for its caption. The
    /// columns are then stretched to fill the grid (see <see cref="FitTiles"/>).</summary>
    private const int TileWidth = PrefabThumbnails.Size + EditorTheme.SpaceXl;

    /// <summary>The height of the picked prefab's details under the grid (drag the divider for more).</summary>
    private const int DetailsHeight = 96;

    /// <summary>The least height of the grid: two rows of tiles, each a picture and two caption lines.</summary>
    private const int GridMinHeight = 2 * (PrefabThumbnails.Size + 2 * EditorTheme.RowHeight);

    /// <summary>Whether the save form is folded, kept for the session: it starts folded, as browsing comes
    /// first.</summary>
    private static bool s_saveFolded = true;

    private string? Folder => Context.Session.Folder;

    private PrefabDefinition? Selected =>
        _list.GetSelectedItems() is { Length: > 0 } sel && sel[0] < _shown.Count ? _shown[sel[0]] : null;

    // ---- browsing -------------------------------------------------------------------------------------------------

    private void Reload()
    {
        (List<PrefabDefinition> prefabs, List<string> problems) = PrefabStore.All(Folder);
        foreach (string problem in problems)
        {
            Context.Log($"Prefabs: {problem}", Severity.Warning);
        }

        _all = prefabs.Where(p => !Friendly || FeaturedGameplay.Conditions.ContainsKey(p.Name)).OrderBy(p => p.Category ?? "~", StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Title ?? p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        string? keep = _category.Selected > 0 ? _category.GetItemText(_category.Selected) : null;
        _category.Clear();
        _category.AddItem("All");
        foreach (string c in _all.Select(p => p.Category).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            _category.AddItem(c);
            if (c == keep)
            {
                _category.Select(_category.ItemCount - 1);
            }
        }

        Fill();
        RefreshInstance();
        if (_argPrefab is { } wanted && _all.Any(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase)))
        {
            _argPrefab = null;
            Reveal(wanted);
        }
    }

    private void Fill()
    {
        string? selected = Selected?.Name;
        string? category = _category.Selected > 0 ? _category.GetItemText(_category.Selected) : null;
        _shown.Clear();
        _shown.AddRange(_all.Where(p =>
            (category is null || string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase)) &&
            (_filter.Length == 0 || TextMatch.FuzzyWords(_filter, $"{p.Name} {p.Title} {p.Category} {p.Summary}") is not null)));
        _list.Clear();
        foreach (PrefabDefinition p in _shown)
        {
            int i = _list.AddItem(Caption(p.Title ?? p.Name), _thumbnails.Get(p, Folder));
            _list.SetItemTooltip(i, Tooltip(p));
            if (p.Name == selected)
            {
                _list.Select(i);
            }
        }

        ShowDetails();
    }

    /// <summary>Puts in the pictures drawn since the list was filled (the list and the details stay as they are).</summary>
    private void UpdatePictures()
    {
        for (int i = 0; i < _shown.Count && i < _list.ItemCount; i++)
        {
            if (_list.GetItemIcon(i) == _thumbnails.Pending)
            {
                _list.SetItemIcon(i, _thumbnails.Get(_shown[i], Folder));
            }
        }
    }

    /// <summary>A tile caption broken at word ends (the list itself breaks inside words).</summary>
    private string Caption(string title) =>
        WorkbenchUi.TileCaption(title, _list.GetThemeFont("font"), _list.GetThemeFontSize("font_size"), _list.FixedColumnWidth - 2 * EditorTheme.SpaceS);

    /// <summary>Sizes the tiles so whole columns fill the grid's width, two at least: no ragged gutter at the right.
    /// The picture shrinks with a narrow tile; the captions are broken again for the new width.</summary>
    private void FitTiles()
    {
        // ItemList pads every item by its h_separation and also puts it between columns, so a column takes the
        // item's width and two separations.
        float gap = 2 * _list.GetThemeConstant("h_separation");
        float room = _list.Size.X - _list.GetThemeStylebox("panel").GetMinimumSize().X - _list.GetVScrollBar().GetCombinedMinimumSize().X;
        int columns = Math.Max(2, (int)Math.Round(room / (TileWidth + gap)));
        int width = Math.Max(1, (int)(room / columns - gap) - 1);
        if (width == _list.FixedColumnWidth)
        {
            return;
        }

        _list.FixedColumnWidth = width;
        int picture = Math.Clamp(width - 2 * EditorTheme.SpaceS, 1, PrefabThumbnails.Size);
        _list.FixedIconSize = new Vector2I(picture, picture);
        _refill.Queue();
    }

    private string Tooltip(PrefabDefinition p)
    {
        string where = p.Source switch
        {
            PrefabSource.Project => "this map's prefabs",
            PrefabSource.Library => "your prefab library",
            _ => "built in",
        };
        string? summary = Friendly && FeaturedGameplay.Descriptions.TryGetValue(p.Name, out string? plain) ? plain : p.Summary;
        return $"{p.Title ?? p.Name} ({p.Name}, {where})" + (summary is { } s ? "\n" + s : "") +
            (p.Params.Count > 0 ? "\nParameters: " + string.Join(", ", p.Params.Select(x => x.Name)) : "");
    }

    // ---- details and placing ------------------------------------------------------------------------------------------

    private void ShowDetails()
    {
        Clear(_details);
        _paramValues.Clear();
        // The details get room only while a prefab is picked (how to place one is the grid's tooltip).
        _detailsScroll.CustomMinimumSize = new Vector2(0, Selected is null ? 0 : Friendly ? Math.Clamp(Size.Y * .45f, DetailsHeight, EditorTheme.RowHeight * 12) : DetailsHeight);
        _detailsScroll.VerticalScrollMode = Selected is null ? ScrollContainer.ScrollMode.Disabled : ScrollContainer.ScrollMode.Reserve;
        if (Selected is not { } def)
        {
            return;
        }

        PanelUi.Header(_details, def.Title ?? def.Name);
        if (Friendly) PanelUi.Primary(_details, "Place", "Place this piece at the view centre with the settings below, or drag its tile into the view.", () => PlaceSelected(null));
        string? description = Friendly && FeaturedGameplay.Descriptions.TryGetValue(def.Name, out string? plain) ? plain : def.Summary;
        if (description is { } summary)
        {
            PanelUi.Caption(_details, summary);
        }
        if (FeaturedGameplay.Conditions.TryGetValue(def.Name, out string? conditions)) PanelUi.Caption(_details, conditions);

        PrefabInstance? instance = SelectedInstances().FirstOrDefault(i => i.Prefab == def.Name);
        foreach (PrefabParam param in def.Params)
        {
            SvJsonNode value = instance?.Params.GetValueOrDefault(param.Name) ?? param.Default;
            ParamRow(param, value);
        }

        HFlowContainer row = PanelUi.Toolbar(_details);
        if (!Friendly) PanelUi.Button(row, "Place", "Place it at the view centre with these parameters", () => PlaceSelected(null)).Icon = Icons.Get("plus", EditorTheme.Text);
        if (FeaturedGameplay.Conditions.ContainsKey(def.Name))
        {
            var resolver = OpenScct.Editor.Core.Assets.AssetResolver.For(EditorServices.ContentRoot, Folder);
            string? problem = FeaturedGameplay.Problem(def.Name, Params(def), Folder, resolver, EditorServices.Classes);
            if (problem is not null)
            {
                PanelUi.Callout(_details, Severity.Warning, problem);
                PanelUi.Button(_details, "Get content packs", "Install the required shared and map assets", () =>
                {
                    if (!Context.Run("content.packs")) FirstMapPanel.ShowSetup();
                });
            }
        }
        if (Friendly) { DetailsShown(_details.GetChild<Control>(0)); return; }
        PanelUi.Button(row, "Update all", "Rebuild every instance of this prefab in the map from the prefab as it is now",
            () => Apply($"Update {def.Name} instances", Op("prefab.update", ("name", ApiJson.Str(def.Name)))))
            .Disabled = !Instances(def.Name).Any();
        PanelUi.Button(row, "Select all", "Select every instance of this prefab", () => SelectParts(Instances(def.Name).Select(i => i.Id)))
            .Disabled = !Instances(def.Name).Any();
        if (instance is not null)
        {
            PanelUi.Button(row, "Apply to selected", $"Give instance {instance.Id} these parameters (it is rebuilt)", () =>
                Apply($"Change {instance.Id}", Op("prefab.update", ("id", ApiJson.Str(instance.Id)), ("params", Params(def)))));
        }
    }

    private void ParamRow(PrefabParam param, SvJsonNode value)
    {
        HBoxContainer row = PanelUi.Row(_details, param.Name, param.Summary);
        switch (param.Default)
        {
            case SvJsonNumber:
                ApiJson.TryNumber(value, out double v);
                bool whole = ApiJson.TryNumber(param.Default, out double d) && d == Math.Floor(d);
                SpinBox spin = PanelUi.Spin(row, param.Min ?? -1e6, param.Max ?? 1e6, whole ? 1 : 0.01, v, param.Summary ?? "");
                spin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                _paramValues[param.Name] = () => ApiJson.Num(spin.Value);
                break;
            case SvJsonString when param.Options is { Count: > 0 } options:
                OptionButton pick = PanelUi.Options(row, param.Summary ?? "");
                pick.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                foreach (string o in options)
                {
                    pick.AddItem(o);
                }

                pick.Select(Math.Max(0, options.ToList().IndexOf((value as SvJsonString)?.Value ?? "")));
                _paramValues[param.Name] = () => ApiJson.Str(pick.GetItemText(pick.Selected));
                break;
            case SvJsonString:
                var text = new LineEdit { Text = (value as SvJsonString)?.Value ?? "", SizeFlagsHorizontal = SizeFlags.ExpandFill };
                row.AddChild(text);
                _paramValues[param.Name] = () => ApiJson.Str(text.Text);
                break;
            default:
                var check = new CheckBox { ButtonPressed = value.Kind == SvJsonKind.True };
                row.AddChild(check);
                _paramValues[param.Name] = () => ApiJson.Bool(check.ButtonPressed);
                break;
        }
    }

    /// <summary>The parameter values that differ from the prefab's defaults (the rest stay defaults).</summary>
    private SvJsonObject Params(PrefabDefinition def)
    {
        var o = new SvJsonObject();
        foreach (PrefabParam param in def.Params)
        {
            if (_paramValues.TryGetValue(param.Name, out Func<SvJsonNode?>? read) && read() is { } value
                && ApiJson.Write(value) != ApiJson.Write(param.Default))
            {
                o.Add(param.Name, value);
            }
        }

        return o;
    }

    /// <summary>Places the selected prefab at a point (the view centre when null) and selects what it made.</summary>
    private void PlaceSelected(Vec3? at)
    {
        if (Selected is not { } def)
        {
            return;
        }

        Place(def, Params(def), at ?? Context.PlacementPoint?.Invoke() ?? Context.Project.Workspace.CameraLocation);
    }

    private void Place(PrefabDefinition def, SvJsonObject given, Vec3 point)
    {
        if (FeaturedGameplay.Conditions.ContainsKey(def.Name) && FeaturedGameplay.Problem(def.Name, given, Folder,
            OpenScct.Editor.Core.Assets.AssetResolver.For(EditorServices.ContentRoot, Folder), EditorServices.Classes) is { } problem)
        {
            Context.Log(problem, Severity.Warning);
            Context.SetStatus(problem);
            return;
        }
        SvJsonObject op = Op("prefab.place", ("name", ApiJson.Str(def.Name)), ("at", ApiJson.Vec(point)));
        if (given.Count > 0)
        {
            op.Add("params", given);
        }

        if (Apply($"Place {def.Name}", op) is { } result)
        {
            List<ObjectRef> made = ((result["created"] as SvJsonObject)?.Select(kv => kv.Key) ?? [])
                .Select(id => MapObjects.Find(Context.Project, id)).OfType<ObjectRef>().ToList();
            if (made.Count > 0)
            {
                Context.Selection.Set(made);
            }
        }
    }

    // ---- instances --------------------------------------------------------------------------------------------------

    private IEnumerable<PrefabInstance> Instances(string prefab) =>
        Context.Project.Prefabs.Where(i => string.Equals(i.Prefab, prefab, StringComparison.OrdinalIgnoreCase));

    /// <summary>The instances the selected objects belong to.</summary>
    private List<PrefabInstance> SelectedInstances()
    {
        var ids = Context.Selection.Items.Select(r => Context.Project.MetaOf(r)?.PrefabInstance).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Context.Project.Prefabs.Where(i => ids.Contains(i.Id)).ToList();
    }

    /// <summary>True when the prefab changed since the instance was placed or last updated.</summary>
    private bool IsStale(PrefabInstance i) =>
        _all.FirstOrDefault(p => string.Equals(p.Name, i.Prefab, StringComparison.OrdinalIgnoreCase)) is { } def && i.Hash is { } h && h != def.Hash;

    private void RefreshInstance()
    {
        if (Friendly) return;
        Clear(_instance);
        List<PrefabInstance> selected = SelectedInstances();
        List<PrefabInstance> stale = Context.Project.Prefabs.Where(IsStale).ToList();
        if (selected.Count == 0 && stale.Count == 0)
        {
            return;
        }

        // The selected instances on one line, with the common command and the rest in a menu.
        if (selected.Count > 0)
        {
            SvJsonNode ids = ApiJson.Strings(selected.Select(i => i.Id));
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", EditorTheme.SpaceXs);
            _instance.AddChild(row);
            string names = string.Join(", ", selected.Select(i => $"{i.Id} ({i.Prefab}{(IsStale(i) ? ", out of date" : "")})"));
            Label which = PanelUi.Caption(row, $"Placed: {names}", wrap: false);
            which.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            which.TooltipText = names;
            which.MouseFilter = MouseFilterEnum.Pass;
            PanelUi.Button(row, "Update", "Rebuild the selected instances from their prefabs (hand edits to their parts are replaced)",
                () => Apply("Update prefab instances", Op("prefab.update", ("ids", ids))));
            MoreMenu(row, "More commands for the selected instances",
                ("Select parts", "Select every part of the selected instances", () => SelectParts(selected.Select(i => i.Id))),
                ("Break", "Unlink the selected instances: their parts stay as plain objects",
                    () => Apply("Break prefab instances", Op("prefab.break", ("ids", ids)))),
                ("Show in list", "Pick the selected instance's prefab in the list", () => Reveal(selected[0].Prefab)));
        }

        if (stale.Count > 0)
        {
            PanelUi.Callout(_instance, Severity.Warning, $"{PanelUi.Count(stale.Count, "instance")} out of date: their prefab changed since they were placed.");
            HFlowContainer row = PanelUi.Toolbar(_instance);
            PanelUi.Button(row, "Update out of date", "Rebuild the instances whose prefab changed since they were placed",
                () => Apply("Update out-of-date prefabs", Op("prefab.update", ("ids", ApiJson.Strings(stale.Select(i => i.Id))))));
        }

        if (Selected is { } def && selected.Any(i => i.Prefab == def.Name))
        {
            ShowDetails();
        }
    }

    private void SelectParts(IEnumerable<string> instanceIds)
    {
        var ids = instanceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Context.Selection.Set(Context.Project.AllRefs()
            .Where(r => Context.Project.MetaOf(r)?.PrefabInstance is { } i && ids.Contains(i))
            .ToList());
    }

    protected void Reveal(string prefab)
    {
        _filter = "";
        _category.Select(0);
        Fill();
        int index = _shown.FindIndex(p => string.Equals(p.Name, prefab, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _list.Select(index);
            _list.EnsureCurrentIsVisible();
            ShowDetails();
        }
    }

    // ---- saving -----------------------------------------------------------------------------------------------------

    /// <summary>A "more" button whose menu runs <paramref name="items"/> (text, tooltip, action).</summary>
    private static void MoreMenu(Control parent, string tooltip, params (string Text, string Tooltip, Action Run)[] items)
    {
        var more = new MenuButton
        {
            ThemeTypeVariation = "IconButton",
            Icon = Icons.Get("more", Colors.White),
            IconAlignment = HorizontalAlignment.Center,
            Flat = false,
            FocusMode = FocusModeEnum.None,
            TooltipText = tooltip,
            CustomMinimumSize = new Vector2(EditorTheme.IconButtonSize, EditorTheme.IconButtonSize),
        };
        PopupMenu menu = more.GetPopup();
        PanelUi.CapPopup(menu);
        for (int i = 0; i < items.Length; i++)
        {
            menu.AddItem(items[i].Text, i);
            menu.SetItemTooltip(i, items[i].Tooltip);
        }

        menu.IdPressed += id => items[(int)id].Run();
        parent.AddChild(more);
    }

    private void BuildSave(Control parent)
    {
        // Folded until wanted, so the grid keeps the room.
        var fold = new FoldableContainer { Title = "Save selection as prefab", Folded = s_saveFolded, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        fold.FoldingChanged += folded => s_saveFolded = folded;
        parent.AddChild(fold);
        var form = new VBoxContainer();
        fold.AddChild(form);
        const string nameTip = "The prefab's file name: letters, digits, _ and -";
        _saveName = new LineEdit { PlaceholderText = "Name", TooltipText = nameTip, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(80, 0) };
        PanelUi.Row(form, "Name", nameTip).AddChild(_saveName);
        _saveCategory = new LineEdit { PlaceholderText = "Optional", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(80, 0) };
        PanelUi.Row(form, "Category", "Groups prefabs in the category list").AddChild(_saveCategory);
        const string toTip = "The map's prefabs travel with the map; the library is yours, for every map";
        _saveTo = PanelUi.Options(PanelUi.Row(form, "Save to", toTip), toTip);
        _saveTo.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _saveTo.AddItem("This map");
        _saveTo.AddItem("My library");
        _saveLink = new CheckBox { Text = "Make the selection an instance", TooltipText = "The selected objects become an instance of the new prefab", ButtonPressed = true };
        form.AddChild(_saveLink);
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        form.AddChild(actions);
        WorkbenchUi.Guard(PanelUi.Primary(actions, "Save as prefab", "Save the selected objects as a prefab around their bottom centre", () => SaveSelection(overwrite: false)),
            () => Context.Selection.Count > 0 && _saveName.Text.Trim().Length > 0);
    }

    private void SaveSelection(bool overwrite)
    {
        string name = _saveName.Text.Trim();
        if (Context.Selection.Count == 0)
        {
            Context.SetStatus("Select the objects to save as a prefab first");
            return;
        }

        bool library = _saveTo.Selected == 1 || Folder is null;
        if (!overwrite && _all.Any(p => p.Source == (library ? PrefabSource.Library : PrefabSource.Project)
            && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            var ask = new ConfirmationDialog
            {
                Title = "Prefab exists",
                DialogText = $"There is already a prefab called {name} there. Replace it? Its instances can then be updated.",
                OkButtonText = "Replace",
                Exclusive = false,
            };
            ask.Confirmed += () =>
            {
                ask.QueueFree();
                SaveSelection(overwrite: true);
            };
            ask.Canceled += ask.QueueFree;
            AddChild(ask);
            ask.PopupCentered();
            return;
        }

        SvJsonObject op = Op("prefab.save",
            ("name", ApiJson.Str(name)),
            ("selection", ApiJson.Bool(true)),
            ("to", ApiJson.Str(library ? "library" : "project")),
            ("link", ApiJson.Bool(_saveLink.ButtonPressed)),
            ("overwrite", overwrite ? ApiJson.Bool(true) : null),
            ("category", _saveCategory.Text.Trim() is { Length: > 0 } c ? ApiJson.Str(c) : null));
        if (Apply($"Save prefab {name}", op) is not null)
        {
            if (library && Folder is null && _saveTo.Selected == 0)
            {
                Context.Log("The map is not saved yet, so the prefab went to your library.");
            }

            Reload();
            Reveal(name);
        }
    }

    // ---- shared -----------------------------------------------------------------------------------------------------

    private static SvJsonObject Op(string op, params (string Key, SvJsonNode? Value)[] fields) =>
        ApiJson.Obj([("op", ApiJson.Str(op)), .. fields]);

    /// <summary>Applies one op as an undo step; returns the result, or null (logged) when it fails.</summary>
    private SvJsonObject? Apply(string label, SvJsonObject op)
    {
        SvJsonObject doc = ApiJson.Obj(
            ("format", ApiJson.Str(ChangeApplier.Format)),
            ("label", ApiJson.Str(label)),
            ("ops", ApiJson.Arr([op])));
        ChangeResult result = ChangeApplier.Apply(Context.Session, doc);
        if (!result.Ok)
        {
            string error = ProposalBook.ErrorText(result.Json["error"] as SvJsonObject);
            Context.Log($"{label} failed: {error}", Severity.Error);
            Context.SetStatus($"{label} failed: {error}");
            return null;
        }

        Context.SetStatus(label);
        return result.Json;
    }

    private static void Clear(Control box)
    {
        foreach (Node child in box.GetChildren())
        {
            box.RemoveChild(child);
            child.QueueFree();
        }
    }

    // ---- drag and drop ----------------------------------------------------------------------------------------------

    /// <summary>Captures the prefab and parameters for a real viewport drop.</summary>
    private Variant DragData(Vector2 at)
    {
        int index = _list.GetItemAtPosition(at, exact: true);
        if (index < 0 || index >= _shown.Count)
        {
            return default;
        }

        // Preserve edited parameters when dragging the already selected tile.
        if (Selected != _shown[index])
        {
            _list.Select(index);
            ShowDetails();
        }
        PrefabDefinition def = _shown[index];
        SvJsonObject given = Params(def);
        var session = Context.Session;
        _list.SetDragPreview(new Label { Text = def.Title ?? def.Name, MouseFilter = MouseFilterEnum.Ignore });
        return Variant.From(new PrefabDragData(def.Title ?? def.Name, point =>
        {
            if (Context.Session == session)
            {
                Place(def, given, point);
            }
        }));
    }

    private void RegisterActions()
    {
        Context.AddAction(new EditorAction("prefab.placeSelected", "Place Prefab", "Tools/Prefabs", () => PlaceSelected(null))
        {
            Order = 100,
            IsEnabled = () => Selected is not null,
            Description = "Place the prefab picked in the Prefabs panel at the view centre",
        });
        Context.AddAction(new EditorAction("prefab.updateSelected", "Update Selected Prefab Instances", "Tools/Prefabs", () =>
            Apply("Update prefab instances", Op("prefab.update", ("ids", ApiJson.Strings(SelectedInstances().Select(i => i.Id))))))
        {
            Order = 110,
            IsEnabled = () => SelectedInstances().Count > 0,
        });
        Context.AddAction(new EditorAction("prefab.updateStale", "Update Out-of-Date Prefab Instances", "Tools/Prefabs", () =>
            Apply("Update out-of-date prefabs", Op("prefab.update", ("ids", ApiJson.Strings(Context.Project.Prefabs.Where(IsStale).Select(i => i.Id))))))
        {
            Order = 120,
            IsEnabled = () => Context.Project.Prefabs.Any(IsStale),
        });
        Context.AddAction(new EditorAction("prefab.breakSelected", "Break Selected Prefab Instances", "Tools/Prefabs", () =>
            Apply("Break prefab instances", Op("prefab.break", ("ids", ApiJson.Strings(SelectedInstances().Select(i => i.Id))))))
        {
            Order = 130,
            IsEnabled = () => SelectedInstances().Count > 0,
            Description = "Unlink the selected instances: their parts stay as plain objects",
        });
    }
}
