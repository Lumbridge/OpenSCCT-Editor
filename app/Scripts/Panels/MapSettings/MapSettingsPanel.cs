using System.Globalization;
using Godot;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Interop.Sdc.Lights;
using OpenScct.Editor.Core.Interop.Sdc.Recovery;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Gameplay;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// Map-wide settings: the map code and title, the map info strings, per game mode settings (add, remove,
/// edit), the LevelInfo properties from the class schema, the lightmap settings, the default Play options, the teams'
/// outfits and the
/// movement limits the design checks use. Every change is one undo step.
/// </summary>
[Panel("Map", DockSide.Right, Order = 1)]
public partial class MapSettingsPanel : EditorPanel
{
    /// <summary>Modes the game knows, with their GameModeId (EGameMode index).</summary>
    private static readonly (string Mode, int Id)[] s_modes = [("Story", 5), ("DiskHunt", 4), ("Deathmatch", 6), ("MercDeathmatch", 7)];

    /// <summary>The ui/mapinfo.json strings (SBase.SMapSettings) and the per-mode strings object.</summary>
    private static readonly PropInfo[] s_mapInfoSchema =
    [
        Field("MapName", "string", "Menu"), Field("LegendMap", "string", "Menu"), Field("LegendMapInBrief", "string", "Menu"),
        Field("ExpliMapInBriefSpy", "string", "Briefing"), Field("ExpliMapInBriefSentry", "string", "Briefing"),
        Field("modes", "map", "Briefing"),
    ];

    /// <summary>A game mode's settings (SBase.SMapSettings.GameModeInfo).</summary>
    private static readonly PropInfo[] s_modeSchema =
    [
        Field("TimeLimit", "int"), Field("ScoreLimit", "int"), Field("SpiesLives", "int"), Field("SentriesLives", "int"),
        Field("RespawnTime", "int"), Field("Handicap", "int"), Field("NumDisksMax", "int"),
        Field("GameModeId", "byte", enumName: "Core.Object.EGameMode"),
    ];

    private readonly List<Action> _syncers = new();
    private readonly List<PropertyGrid> _grids = new();
    private readonly Dictionary<PropertyGrid, string> _gridSignatures = new();
    private readonly Dictionary<PropertyGrid, PropSource> _sources = new();
    private VBoxContainer _content = null!;
    private PropertyGrid _modeGrid = null!;
    private OptionButton _mode = null!;
    private DeferredAction _sync = null!;
    private string? _modesSignature;

    protected override void Build()
    {
        _sync = new DeferredAction(Sync);
        _content = PanelUi.Scroll(this);
        Context.ProjectChanged += _ => _sync.Queue();
    }

    public override void Refresh()
    {
        foreach (Node child in _content.GetChildren())
        {
            _content.RemoveChild(child);
            child.QueueFree();
        }

        _syncers.Clear();
        _grids.Clear();
        _gridSignatures.Clear();
        _sources.Clear();
        _modesSignature = null;
        IdentitySection();
        GridSection("Map info", "Menu and briefing strings (ui/mapinfo.json)", new ProjectPropSource("", "map info", p => p.MapInfo, s_mapInfoSchema));
        ModesSection();
        GridSection("LevelInfo", "Properties of the level's LevelInfo actor",
            new ProjectPropSource("Engine.LevelInfo", "LevelInfo", p => p.LevelInfo), folded: true);
        LightingSection();
        BakedWorldSection();
        PlaySection();
        OutfitsSection();
        MovementSection();
        Sync();
    }

    private void Sync()
    {
        string modes = ModesSignature();
        if (modes != _modesSignature)
        {
            _modesSignature = modes;
            FillModes();
        }

        foreach (Action s in _syncers)
        {
            s();
        }

        foreach (PropertyGrid g in _grids)
        {
            if (g.Signature != _gridSignatures.GetValueOrDefault(g))
            {
                RefreshGrid(g);
            }
            else
            {
                g.Sync();
            }
        }
    }

    // ---- identity -------------------------------------------------------------------------------------------------

    private void IdentitySection()
    {
        PanelUi.Header(_content, "Map code and title");
        TextRow("Map code", "Folder and file name; the original game needs exactly 5 characters (e.g. ShipD)",
            () => Context.Project.Id, text =>
            {
                string id = text.Trim();
                return id.Length == 0 ? null : new ProjectSettingsCommand($"Map code {id}", p => p.Id = id);
            });
        TextRow("Title", $"The map's name in menus, 1 to {ProjectIdentity.MaxName} characters (the Identity panel has its dossier, pictures and places)",
            () => Context.Project.Title,
            text => text.Trim().Length is > 0 and <= ProjectIdentity.MaxName ? new ProjectSettingsCommand("Map title", p => p.Title = text.Trim()) : null);
        PanelContainer hint = PanelUi.Callout(_content, Severity.Warning, "");
        Label hintText = hint.GetChild(0).GetChild<Label>(1);
        _syncers.Add(() =>
        {
            string id = Context.Project.Id;
            var problems = new List<string>();
            if (id.Length != 5)
            {
                problems.Add($"the original game needs a 5-character map code ('{id}' has {id.Length})");
            }

            if (id.Any(c => c > 127 || !char.IsLetterOrDigit(c)))
            {
                problems.Add("use ASCII letters and digits only");
            }

            hint.Visible = problems.Count > 0;
            hintText.Text = "Original export: " + string.Join("; ", problems) + ".";
        });
        // Paths are shortened to their last part; the tooltip has them in full.
        Label source = PanelUi.Caption(_content, "");
        source.MouseFilter = MouseFilterEnum.Pass;
        _syncers.Add(() =>
        {
            ProjectSource s = Context.Project.Source;
            source.Text = $"Source: {s.Kind}" + (s.File is { } f ? $" ({Path.GetFileName(f)})" : "")
                + (Context.Project.Baked is { } baked ? $" · baked world from {Path.GetFileName(baked.SourceMap.TrimEnd('/', '\\'))}" : "");
            source.TooltipText = string.Join("\n", new[] { s.File, Context.Project.Baked?.SourceMap }.OfType<string>());
        });
    }

    // ---- property grids -------------------------------------------------------------------------------------------

    private PropertyGrid GridSection(string title, string tooltip, PropSource source, bool folded = false)
    {
        var box = new FoldableContainer { Title = title, Folded = folded, TooltipText = tooltip, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _content.AddChild(box);
        var grid = new PropertyGrid();
        grid.Init(Context);
        grid.PickerClosed += () => _sync.Queue();
        box.AddChild(grid);
        _grids.Add(grid);
        _sources[grid] = source;
        RefreshGrid(grid);
        return grid;
    }

    private void RefreshGrid(PropertyGrid grid)
    {
        grid.ShowSource(_sources[grid]);
        _gridSignatures[grid] = grid.Signature;
    }

    // ---- game modes -----------------------------------------------------------------------------------------------

    private void ModesSection()
    {
        PanelUi.Header(_content, "Game modes");
        HFlowContainer bar = PanelUi.Toolbar(_content);
        _mode = PanelUi.Options(bar, "The mode whose settings show below");
        _mode.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _mode.CustomMinimumSize = new Vector2(EditorTheme.SearchMinWidth, 0);
        _mode.ItemSelected += _ => ShowMode();
        var add = new MenuButton
        {
            Text = "Add mode", TooltipText = "Add a mode (copies the shown mode's values)", ThemeTypeVariation = EditorTheme.CommandMenu, Flat = false,
            Icon = Icons.Get("chevron-down", EditorTheme.TextDim), IconAlignment = HorizontalAlignment.Right,
        };
        PopupMenu addMenu = add.GetPopup();
        addMenu.AboutToPopup += () =>
        {
            addMenu.Clear();
            foreach ((string mode, int _) in s_modes.Where(m => !Context.Project.MapSettings.ContainsKey(m.Mode)))
            {
                addMenu.AddItem(mode);
            }
        };
        addMenu.IndexPressed += index => AddMode(addMenu.GetItemText((int)index));
        bar.AddChild(add);
        PanelUi.IconButton(bar, "trash", "Remove the shown mode from the map", RemoveMode, danger: true);

        var grid = new PropertyGrid();
        grid.Init(Context);
        grid.PickerClosed += () => _sync.Queue();
        _content.AddChild(grid);
        _grids.Add(grid);
        _sources[grid] = ModeSource(null);
        _modeGrid = grid;
    }

    private string ModesSignature() => string.Join(',', Context.Project.MapSettings.Keys);

    private void FillModes()
    {
        string? current = CurrentMode();
        _mode.Clear();
        foreach (string mode in Context.Project.MapSettings.Keys)
        {
            _mode.AddItem(mode);
            if (mode == current)
            {
                _mode.Select(_mode.ItemCount - 1);
            }
        }

        if (_mode.Selected < 0 && _mode.ItemCount > 0)
        {
            _mode.Select(0);
        }

        ShowMode();
    }

    private string? CurrentMode() => _mode.Selected >= 0 ? _mode.GetItemText(_mode.Selected) : null;

    private void ShowMode()
    {
        _sources[_modeGrid] = ModeSource(CurrentMode());
        RefreshGrid(_modeGrid);
    }

    /// <summary>The settings of one mode, bound to that mode (so redo edits the same mode whatever is shown then).</summary>
    private static ProjectPropSource ModeSource(string? mode) =>
        new("", $"{mode} settings", p => mode is null ? null : p.MapSettings.GetValueOrDefault(mode), s_modeSchema);

    private static PropInfo Field(string name, string type, string category = "Game mode", string? enumName = null) =>
        new(name, type, category, Editable: true, enumName, Struct: null, ObjectClass: null, ArrayDim: 1, Inner: null, DeclaringClass: null);

    private void AddMode(string mode)
    {
        MapProject p = Context.Project;
        PropSet template = CurrentMode() is { } from && p.MapSettings.TryGetValue(from, out PropSet? s) ? s.Clone() : new PropSet();
        if (s_modes.FirstOrDefault(m => m.Mode == mode) is { Mode: not null } known)
        {
            template.Set("GameModeId", SvJsonNode.Integer(known.Id));
        }

        Context.Execute(new ProjectSettingsCommand($"Add mode {mode}", q => q.MapSettings[mode] = template.Clone()));
        EditorLifetime.Defer(() =>
        {
            for (int i = 0; i < _mode.ItemCount; i++)
            {
                if (_mode.GetItemText(i) == mode)
                {
                    _mode.Select(i);
                    ShowMode();
                }
            }
        });
    }

    private void RemoveMode()
    {
        if (CurrentMode() is { } mode)
        {
            Context.Execute(new ProjectSettingsCommand($"Remove mode {mode}", q => q.MapSettings.Remove(mode)));
        }
    }

    // ---- lighting -------------------------------------------------------------------------------------------------

    private void LightingSection()
    {
        PanelUi.Header(_content, "Lighting (lightmaps)");
        LightingSettings L() => Context.Project.Lighting;
        OptionRow("Mode", [("Quality", "quality"), ("Faithful (as the original editor)", "faithful")],
            () => L().Mode == LightmapMode.Faithful ? "faithful" : "quality",
            v => Lighting("Lighting mode", l => l.Mode = v == "faithful" ? LightmapMode.Faithful : LightmapMode.Quality));
        NumberRow("Density", "Texels per LightMapScale: 1 is the original's, 2 (the default) twice as sharp. Quality mode",
            () => L().Density, v => Lighting("Lightmap density", l => l.Density = Math.Clamp(v, LightingSettings.MinDensity, LightingSettings.MaxDensity)));
        NumberRow("Shadow samples", "Samples per texel side on shadow edges (4 = 16 rays). Quality mode",
            () => L().Samples, v => Lighting("Shadow samples", l => l.Samples = Math.Clamp((int)v, LightingSettings.MinSamples, LightingSettings.MaxSamples)));
        NumberRow("Light size", "Radius of point and spot lights in uu, for soft shadow edges (0 = hard). Quality mode",
            () => L().LightSize, v => Lighting("Light size", l => l.LightSize = Math.Clamp(v, 0, LightingSettings.MaxLightSize)));
        NumberRow("Sun size°", "The sun's angular diameter, for soft sun shadows (0 = hard). Quality mode",
            () => L().SunSize, v => Lighting("Sun size", l => l.SunSize = Math.Clamp(v, 0, LightingSettings.MaxSunSize)));
        NumberRow("Bounce", "Gain of one bounce of light off lit surfaces (0 = none, as the original: corners lit only by bounced light stay dark for the spy)",
            () => L().Bounce, v => Lighting("Bounce light", l => l.Bounce = Math.Clamp(v, 0, LightingSettings.MaxBounce)));
        NumberRow("Bounce samples", "Rays per texel for bounce light",
            () => L().BounceSamples,
            v => Lighting("Bounce samples", l => l.BounceSamples = Math.Clamp((int)v, LightingSettings.MinBounceSamples, LightingSettings.MaxBounceSamples)));
        OptionRow("Atlas size", LightingSettings.AtlasSizes.Select(a => ($"{a} \u00d7 {a}", a.ToString(CultureInfo.InvariantCulture))).ToList(),
            () => L().AtlasSize.ToString(CultureInfo.InvariantCulture),
            v => Lighting("Lightmap atlas size", l => l.AtlasSize = int.Parse(v, CultureInfo.InvariantCulture)));
        OptionRow("Imported lightmaps", [("Keep them", "keep"), ("Relight with the project's lights", "relight")],
            () => L().RelightImported ? "relight" : "keep", v => Lighting("Relight imported lightmaps", l => l.RelightImported = v == "relight"));
        // The class default is 64, so 64 is offered once, as the default.
        OptionRow("Light radius", [("Class default (64)", "default"), ("32", "32")],
            () => L().BakeDefaultLightRadius is { } r and not 64 ? r.ToString("0", CultureInfo.InvariantCulture) : "default",
            v => Lighting("Default light radius", l => l.BakeDefaultLightRadius = v == "default" ? null : double.Parse(v, CultureInfo.InvariantCulture)),
            "The radius a light that stores no LightRadius bakes with");

        // An .sdc import's light recovery: what came back, and a warning when a relight will not reproduce the original.
        PanelContainer unsafeRelight = PanelUi.Callout(_content, Severity.Warning, "");
        Label unsafeText = unsafeRelight.GetChild(0).GetChild<Label>(1);
        Label recovery = PanelUi.Caption(_content, "");
        recovery.TooltipText = "Lights the stock cook stripped from a cooked .sdc, brought back at import into "
            + $"{LostLightRecovery.Folder}. Light radius is what a light that stores no LightRadius bakes with; "
            + "the import picks the value that matches the cooked lightmaps best";
        _syncers.Add(() =>
        {
            var report = Context.Project.Extra[LostLightRecovery.ReportKey] as SvJsonObject;
            string? summary = report?["summary"] is SvJsonString s ? s.Value : null;
            string? why = report?["unsafeReason"] is SvJsonString u ? u.Value : null;
            recovery.Visible = summary is not null;
            recovery.Text = summary is null ? "" : PanelUi.Sentence(summary) + ".";
            unsafeRelight.Visible = why is not null;
            unsafeText.Text = why is null ? "" : $"A relight will not match the original: {why}. The cooked lightmaps are kept until you relight.";
        });
    }

    /// <summary>Which layers of an imported map's baked world the build keeps. The one that is not kept is built from
    /// the map's brushes; a map whose baked world holds no brushes (an SVMap import) has nothing to build it from, so it
    /// is offered only both or neither.</summary>
    private void BakedWorldSection()
    {
        if (Context.Project.Baked is null)
        {
            return;
        }

        PanelUi.Header(_content, "Baked world (imported)");
        bool rebuildable = Context.Project.Brushes.Any(BakedContent.IsBaked);
        List<(string Text, string Value)> items = rebuildable
            ? [("Keep render and collision", "both"), ("Keep render, build collision from the brushes", "render"),
                ("Keep collision, build render from the brushes", "collision"), ("Build both from the brushes", "neither")]
            : [("Keep render and collision", "both"), ("Build both from the brushes", "neither")];
        OptionRow("Keep", items, () => Context.Project.Baked switch
            {
                null => "",
                { KeepRender: true, KeepCollision: true } => "both",
                { KeepRender: true } => "render",
                { KeepCollision: true } => "collision",
                _ => "neither",
            },
            v => new ProjectSettingsCommand("Baked world layers", p =>
            {
                if (p.Baked is { } b)
                {
                    b.KeepRender = v is "both" or "render";
                    b.KeepCollision = v is "both" or "collision";
                }
            }));

        // A cooked .sdc import's brush recovery: what it made from the compiled BSP.
        Label recovered = PanelUi.Caption(_content, "");
        recovered.TooltipText = $"Brushes recovered at import from the compiled BSP of a cooked .sdc, in the {BrushRecovery.Folder} layer. "
            + "They are what the baked world is made of: build from the brushes to play the map as they are";
        _syncers.Add(() =>
        {
            string? summary = (Context.Project.Extra[BrushRecovery.ReportKey] as SvJsonObject)?["summary"] is SvJsonString s ? s.Value : null;
            recovered.Visible = summary is not null;
            recovered.Text = summary is null ? "" : PanelUi.Sentence(summary) + ".";
        });
    }

    /// <summary>One lighting change as an undo step (the settings are replaced, never edited in place).</summary>
    private static ProjectSettingsCommand Lighting(string label, Action<LightingSettings> change) =>
        new(label, p =>
        {
            LightingSettings l = p.Lighting.Clone();
            change(l);
            p.Lighting = l;
        });

    // ---- play options and limits ----------------------------------------------------------------------------------

    private void PlaySection()
    {
        PanelUi.Header(_content, "Play options");
        OptionRow("Side", [("Spy", "spy"), ("Merc", "merc")], () => Context.Project.Workspace.Play.Side,
            v => PanelCommands.Play(Context.Project, $"Play as {v}", o => o.Side = v));
        OptionRow("Mode", s_modes.Select(m => (GameModeFilter.Display(m.Mode), m.Mode)).ToList(), () => Context.Project.Workspace.Play.Mode,
            v => PanelCommands.Play(Context.Project, $"Play mode {v}", o => o.Mode = v));
        OptionRow("Rules", [("Play Level rules", "playlevel"), ("The map's own settings", "map")], () => Context.Project.Workspace.Play.Rules,
            v => PanelCommands.Play(Context.Project, "Play rules", o => o.Rules = v));
        NumberRow("Bots", "Bot players added to the test match", () => Context.Project.Workspace.Play.Bots,
            v => PanelCommands.Play(Context.Project, "Play bots", o => o.Bots = Math.Max(0, (int)v)));
        NumberRow("Players", "The player count a playtest lays the map out for (its match-size rules), bots included; 0 counts the bots and you",
            () => Context.Project.Workspace.Play.Players,
            v => PanelCommands.Play(Context.Project, "Play players", o => o.Players = Math.Clamp((int)v, 0, OpenScct.Core.Match.MatchRuleSet.MostPlayers)));
    }

    private void MovementSection()
    {
        PanelUi.Header(_content, "Movement limits (design checks)");
        MovementLimits M() => Context.Project.Workspace.Movement;
        void Row(string label, string tip, Func<MovementLimits, double> get, Action<MovementLimits, double> set) =>
            NumberRow(label, tip, () => get(M()), v => PanelCommands.Movement(Context.Project, $"Set {label}", m => set(m, v)));
        Row("Step height", "Highest step a player walks up", m => m.StepHeight, (m, v) => m.StepHeight = v);
        Row("Walkable slope°", "Steepest walkable floor", m => m.WalkableSlopeDegrees, (m, v) => m.WalkableSlopeDegrees = v);
        Row("Stand height", "Standing collision height", m => m.StandHeight, (m, v) => m.StandHeight = v);
        Row("Spy crouch", "Spy crouched collision height", m => m.SpyCrouchHeight, (m, v) => m.SpyCrouchHeight = v);
        Row("Merc crouch", "Merc crouched collision height", m => m.MercCrouchHeight, (m, v) => m.MercCrouchHeight = v);
        Row("Radius", "Player collision radius", m => m.Radius, (m, v) => m.Radius = v);
        Row("Spy run speed", "Units per second", m => m.SpyRunSpeed, (m, v) => m.SpyRunSpeed = v);
        Row("Merc run speed", "Units per second", m => m.MercRunSpeed, (m, v) => m.MercRunSpeed = v);
    }

    // ---- rows -----------------------------------------------------------------------------------------------------

    private void TextRow(string label, string tooltip, Func<string> read, Func<string, IEditCommand?> commit)
    {
        HBoxContainer row = PanelUi.Row(_content, label, tooltip);
        var edit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, SelectAllOnFocus = true };
        row.AddChild(edit);
        // Only text that differs from what the box showed is an edit, and only on the map the row was built for:
        // opening another map removes a focused box, which fires FocusExited.
        MapProject owner = Context.Project;
        string shown = "";
        void Show()
        {
            shown = read();
            edit.Text = shown;
        }

        void Final(string text)
        {
            if (text == shown || !ReferenceEquals(Context.Project, owner))
            {
                return;
            }

            shown = text;
            if (text != read() && commit(text) is { } cmd)
            {
                Context.Execute(cmd);
            }
        }

        edit.TextSubmitted += Final;
        edit.FocusExited += () =>
        {
            Final(edit.Text);
            if (ReferenceEquals(Context.Project, owner))
            {
                Show();
            }
        };
        _syncers.Add(() =>
        {
            if (!edit.HasFocus())
            {
                Show();
            }
        });
    }

    private void NumberRow(string label, string tooltip, Func<double> read, Func<double, IEditCommand> commit)
    {
        HBoxContainer row = PanelUi.Row(_content, label, tooltip);
        AxisFields field = AxisFields.Number(tooltip);
        row.AddChild(field);
        MapProject owner = Context.Project;
        field.Edited += (_, v, final) =>
        {
            if (final && ReferenceEquals(Context.Project, owner) && Math.Abs(v - read()) > 1e-9)
            {
                Context.Execute(commit(v));
            }
        };
        _syncers.Add(() => field.SetValues([read()]));
    }

    private void OptionRow(string label, IReadOnlyList<(string Text, string Value)> items, Func<string> read, Func<string, IEditCommand> commit,
        string? tooltip = null)
    {
        HBoxContainer row = PanelUi.Row(_content, label, tooltip);
        OptionButton option = PanelUi.Options(row);
        option.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foreach ((string text, string _) in items)
        {
            option.AddItem(text);
        }

        option.ItemSelected += index =>
        {
            string value = items[(int)index].Value;
            if (!string.Equals(value, read(), StringComparison.OrdinalIgnoreCase))
            {
                Context.Execute(commit(value));
            }
        };
        _syncers.Add(() =>
        {
            string current = read();
            int index = items.ToList().FindIndex(i => string.Equals(i.Value, current, StringComparison.OrdinalIgnoreCase));
            PanelUi.Choose(option, index, string.IsNullOrEmpty(current) ? "(not set)" : current);
            option.TooltipText = index < 0 ? $"Stored value: {current}" : option.Text;
        });
    }
}
