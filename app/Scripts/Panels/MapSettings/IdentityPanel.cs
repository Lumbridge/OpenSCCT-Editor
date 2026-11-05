using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Identity;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Session;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Panels;

/// <summary>
/// The map's identity (SVMap 1.2 <c>ui/identity.json</c>): name, dossier, description, authors, recommended players,
/// tags, strategic map floors, the numbered places of the briefing plan, a recipe per picture slot (a file with its
/// focal point, a camera bookmark rendered by OpenSCCT with a look, or the generated plan), and translations of the
/// texts (<c>ui/text/&lt;lang&gt;.json</c>). Every change is one undo step; Render now renders the camera pictures (as
/// <c>ose mapshot</c> does).
/// </summary>
[Panel("Identity", DockSide.Right, Order = 2)]
public partial class IdentityPanel : EditorPanel
{
    public const string RenderJob = "Render pictures";

    private readonly List<Action> _syncers = new();
    private VBoxContainer _content = null!;
    private VBoxContainer _pictures = null!;
    private VBoxContainer _places = null!;
    private VBoxContainer _texts = null!;
    private readonly List<Action> _textSyncers = new();
    private string? _language;
    private DeferredAction _sync = null!;
    private string? _listsSignature;

    protected override void Build()
    {
        _sync = new DeferredAction(Sync);
        _content = PanelUi.Scroll(this);
        Context.ProjectChanged += _ => _sync.Queue();
        Context.AddAction(new EditorAction("identity.render", "Render Map Pictures", "Build", RenderPictures)
        {
            Description = "Render the identity's camera pictures with OpenSCCT (in a background window) and rebuild the map",
            IsEnabled = () => !BackgroundJobs.IsRunning(RenderJob),
        });
        Context.AddAction(new EditorAction("identity.placeAtCamera", "Add Place at Camera", "Edit", AddPlaceAtCamera)
        {
            Description = "Add the next numbered briefing place on the floor under the camera",
            Order = 900,
        });
    }

    public override void Refresh()
    {
        foreach (Node child in _content.GetChildren())
        {
            _content.RemoveChild(child);
            child.QueueFree();
        }

        _syncers.Clear();
        _listsSignature = null;
        NameSection();
        PanelUi.Header(_content, "Pictures");
        _pictures = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _content.AddChild(_pictures);
        HFlowContainer bar = PanelUi.Toolbar(_content);
        WorkbenchUi.Guard(PanelUi.Button(bar, "Render now", "Render the camera pictures with OpenSCCT in a background window that never takes "
            + "the focus, then rebuild", RenderPictures),
            () => !BackgroundJobs.IsRunning(RenderJob)).Icon = Icons.Get("play", EditorTheme.Text);
        PanelUi.Header(_content, "Places");
        _places = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _content.AddChild(_places);
        HFlowContainer placeBar = PanelUi.Toolbar(_content);
        PanelUi.Button(placeBar, "Add at camera", "Add the next number on the floor under the camera", AddPlaceAtCamera).Icon =
            Icons.Get("plus", EditorTheme.Text);
        PanelUi.Header(_content, "Translations");
        _texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _content.AddChild(_texts);
        Sync();
    }

    private void Sync()
    {
        string lists = ListsSignature();
        if (lists != _listsSignature)
        {
            _listsSignature = lists;
            FillPictures();
            FillPlaces();
            FillTexts();
        }

        foreach (Action s in _syncers.Concat(_textSyncers))
        {
            s();
        }
    }

    private ProjectIdentity Id => Context.Project.Identity;

    private void Edit(string name, Action<ProjectIdentity> edit) => Context.Execute(new ProjectSettingsCommand(name, p => edit(p.Identity)));

    // ---- name and facts -------------------------------------------------------------------------------------------

    private void NameSection()
    {
        PanelUi.Header(_content, "Name and story");
        TextRow(_content, "Name", $"The name menus show (at most {ProjectIdentity.MaxName} characters); also the project title",
            () => Context.Project.Title, text => text.Trim().Length is > 0 and <= ProjectIdentity.MaxName
                ? new ProjectSettingsCommand("Map name", p => p.Title = text.Trim())
                : null);
        TextRow(_content, "Family", "The name without its variant word (lists sort by it); empty: the whole name",
            () => Id.Family ?? "", text => Cmd("Map family", i => i.Family = Opt(text)));
        TextRow(_content, "Variant", "The variant word (Enhanced), dimmed in lists", () => Id.Variant ?? "", text => Cmd("Map variant", i => i.Variant = Opt(text)));
        TextRow(_content, "When", "Dossier: when the map is set (14 June 2009, 11:20 PM)", () => Id.Dossier.When, text => Cmd("Dossier", i => i.Dossier.When = text.Trim()));
        TextRow(_content, "Where", "Dossier: where it is set (Portugal, a harbour warehouse)", () => Id.Dossier.Where, text => Cmd("Dossier", i => i.Dossier.Where = text.Trim()));
        TextRow(_content, "Summary", "Dossier: one line about the map; the description when that is empty", () => Id.Dossier.Summary,
            text => Cmd("Dossier", i => i.Dossier.Summary = text.Trim()), prose: true);
        TextRow(_content, "Description", $"One or two sentences for map browsers (at most {ProjectIdentity.MaxDescription} characters); empty: the summary",
            () => Id.Description ?? "", text => Cmd("Map description", i => i.Description = Opt(text)), prose: true);
        TextRow(_content, "Authors", "People or teams, in credit order, separated by commas", () => string.Join(", ", Id.Authors),
            text => Cmd("Map authors", i => i.Authors = List(text)));
        TextRow(_content, "Version", "The map's version (1.0.0)", () => Id.Version ?? "", text => Cmd("Map version", i => i.Version = Opt(text)));
        PlayersRow();
        TextRow(_content, "Tags", "Lowercase facts separated by commas; known: " + string.Join(", ", ProjectIdentity.KnownTags),
            () => string.Join(", ", Id.Tags), text =>
            {
                List<string> tags = List(text).Select(t => t.ToLowerInvariant()).Distinct().ToList();
                return tags.All(ProjectIdentity.IsTag) ? Cmd("Map tags", i => i.Tags = tags) : null;
            });
        TextRow(_content, "Language", "The language of the map's texts (en, fr, de...); not one it has a translation into", () => Id.Language,
            text => ProjectIdentity.IsLanguage(text.Trim()) && !Id.Texts.ContainsKey(text.Trim()) ? Cmd("Map language", i => i.Language = text.Trim()) : null);
        TextRow(_content, "Floors", "Strategic map floors: auto (found from the map), heights separated by commas, or none",
            () => Id.Floors is null ? "auto" : Id.Floors.Count == 0 ? "none" : string.Join(", ", Id.Floors.Select(PanelUi.Num)),
            text => FloorsCommand(text.Trim()));
        Label legend = PanelUi.Caption(_content, "");
        legend.MouseFilter = MouseFilterEnum.Pass;
        _syncers.Add(() =>
        {
            string line = Id.Dossier.IsEmpty ? "" : SideFiles.LegendOf(Id.Dossier);
            legend.Text = line.Length == 0 ? "" : "In game: " + Readable(line);
            legend.TooltipText = "The legend line the game shows, built from the dossier\nLegendMap: " + line;
            legend.Visible = line.Length > 0;
        });
    }

    private ProjectSettingsCommand Cmd(string name, Action<ProjectIdentity> edit) => new(name, p => edit(p.Identity));

    private void PlayersRow()
    {
        string tooltip = $"The lobby size the map plays best at (1 to {ProjectIdentity.MaxPlayers}); exactly 6 also lists it with the 3v3 maps";
        HBoxContainer row = PanelUi.Row(_content, "Players", tooltip);
        OptionButton players = PanelUi.Options(row, tooltip);
        players.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        players.AddItem("Not set", 0);
        for (int n = 1; n <= ProjectIdentity.MaxPlayers; n++)
        {
            players.AddItem(n.ToString(CultureInfo.InvariantCulture), n);
        }

        players.ItemSelected += index =>
        {
            int id = players.GetItemId((int)index);
            int? value = id == 0 ? null : id;
            if (value != Id.RecommendedPlayers)
            {
                Edit("Recommended players", i => i.RecommendedPlayers = value);
            }
        };
        _syncers.Add(() => players.Select(players.GetItemIndex(Id.RecommendedPlayers ?? 0)));
    }

    private ProjectSettingsCommand? FloorsCommand(string text)
    {
        if (text is "" or "auto")
        {
            return Cmd("Strategic map floors", i => i.Floors = null);
        }

        if (text == "none")
        {
            return Cmd("Strategic map floors", i => i.Floors = []);
        }

        var heights = new List<double>();
        foreach (string part in List(text))
        {
            if (!PanelUi.TryNum(part, out double z))
            {
                return null;
            }

            heights.Add(z);
        }

        return Cmd("Strategic map floors", i => i.Floors = heights.Order().ToList());
    }

    // ---- pictures -------------------------------------------------------------------------------------------------

    private string ListsSignature()
    {
        IdentityPictures p = Id.Pictures;
        string Of(PictureRecipe? r) => r is null ? "-" : $"{r.From}:{r.File}:{r.FocusX}:{r.FocusY}:{r.Bookmark}:{r.Look}:{r.Fov}";
        return string.Join("|", new[] { Of(p.Menu), Of(p.Briefing), Of(p.Banner) }.Concat(p.LoadingSpy.Select(Of)).Append("/").Concat(p.LoadingMerc.Select(Of)))
            + "#" + string.Join("|", Id.Places.Select(pl => $"{pl.Number}:{pl.Name}:{pl.At}:{pl.Floor}"))
            + "#" + string.Join("|", Context.Project.Workspace.Bookmarks.Select(b => b.Name))
            + "#" + string.Join("|", Id.Texts.Keys) + "#" + Id.Language;
    }

    private void FillPictures()
    {
        Clear(_pictures);
        Slot("Menu", "Map lists and the lobby briefing's first page", "menu", p => p.Menu, (p, r) => p.Menu = r);
        LoadingSlots("Loading (spies)", true);
        LoadingSlots("Loading (mercs)", false);
        Slot("Briefing", "The lobby briefing's plan page; the plan gets the place numbers", "briefing", p => p.Briefing, (p, r) => p.Briefing = r);
        Slot("Banner", "A wide 16:9 picture for map browsers (optional)", "banner", p => p.Banner, (p, r) => p.Banner = r);
    }

    private void Slot(string label, string tooltip, string slot, Func<IdentityPictures, PictureRecipe?> get, Action<IdentityPictures, PictureRecipe?> set)
    {
        HBoxContainer row = PanelUi.Row(_pictures, label, tooltip);
        PictureRecipe? current = get(Id.Pictures);
        RecipeLabel(row, current);
        HBoxContainer actions = WorkbenchUi.Actions(row);
        SourceMenu(actions, "Set", r => Edit($"{label} picture", i => set(i.Pictures, r)), allowPlan: IdentityBuild.AspectOf(slot) == 1);
        Button clear = PanelUi.IconButton(actions, "trash", "Clear this picture", () => Edit($"{label} picture", i => set(i.Pictures, null)), danger: true);
        clear.Disabled = current is null;
        if (current is not null)
        {
            RecipeDetails(current, slot, change => Edit($"{label} picture", i => change(get(i.Pictures)!)));
        }
    }

    private void LoadingSlots(string label, bool spy)
    {
        const string tooltip = "Loading pictures: one is picked each time the map loads";
        List<PictureRecipe> list = spy ? Id.Pictures.LoadingSpy : Id.Pictures.LoadingMerc;
        List<PictureRecipe> Of(IdentityPictures p) => spy ? p.LoadingSpy : p.LoadingMerc;
        string stem = spy ? "loading-spy" : "loading-merc";
        for (int i = 0; i < list.Count; i++)
        {
            int index = i;
            HBoxContainer row = PanelUi.Row(_pictures, i == 0 ? label : "", tooltip);
            RecipeLabel(row, list[i]);
            HBoxContainer actions = WorkbenchUi.Actions(row);
            SourceMenu(actions, "Set", r => Edit($"{label} picture", p => Of(p.Pictures)[index] = r), allowPlan: false);
            PanelUi.IconButton(actions, "trash", "Remove this picture", () => Edit($"{label} picture", p => Of(p.Pictures).RemoveAt(index)), danger: true);
            RecipeDetails(list[i], $"{stem}-{i + 1}", change => Edit($"{label} picture", p => change(Of(p.Pictures)[index])));
        }

        if (list.Count < ProjectIdentity.MaxPictures)
        {
            // The same row as a set picture: the value (dim when there is none yet), then the trailing slot, whose
            // delete button keeps its place empty so every row's right edge lines up.
            HBoxContainer add = PanelUi.Row(_pictures, list.Count == 0 ? label : "", tooltip);
            WorkbenchUi.Value(add, list.Count == 0 ? "Not set" : "Another picture", "Dim");
            HBoxContainer actions = WorkbenchUi.Actions(add);
            SourceMenu(actions, "Add", r => Edit($"{label} picture", p => Of(p.Pictures).Add(r)), allowPlan: false);
            actions.AddChild(new Control { CustomMinimumSize = new Vector2(EditorTheme.IconButtonSize, 0), MouseFilter = MouseFilterEnum.Ignore });
        }
    }

    /// <summary>A preview of what the picture will show (the file's crop, the camera's last render) and the recipe's
    /// settings: the focal point of a file, the look and field of view of a camera.</summary>
    private void RecipeDetails(PictureRecipe r, string slot, Action<Action<PictureRecipe>> change)
    {
        // Under the slot's row, from the panel's edge: the label column would push the settings past a narrow dock.
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", EditorTheme.SpaceM);
        _pictures.AddChild(row);
        Vector2 size = IdentityBuild.AspectOf(slot) == 1 ? new Vector2(72, 72) : new Vector2(128, 72);
        (Texture2D? preview, string note) = Preview(r, slot, size);
        // On a tile, so a missing or unrendered picture still shows its frame.
        var tile = new PanelContainer { ThemeTypeVariation = "PictureTile", SizeFlagsVertical = SizeFlags.ShrinkBegin, TooltipText = note };
        row.AddChild(tile);
        tile.AddChild(new TextureRect
        {
            Texture = preview, CustomMinimumSize = size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore,
        });
        var settings = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(settings);
        switch (r.From)
        {
            case PictureSource.File:
                Spin(settings, "Across", "The focal point the crop keeps, 0 (left) to 1 (right)", r.FocusX, 0, 1, 0.05, v => change(x => x.FocusX = v));
                Spin(settings, "Down", "The focal point the crop keeps, 0 (top) to 1 (bottom)", r.FocusY, 0, 1, 0.05, v => change(x => x.FocusY = v));
                break;
            case PictureSource.Camera:
                HBoxContainer lookRow = PanelUi.Row(settings, "Look", "How the camera sees the map", EditorTheme.LabelColumnNarrow);
                OptionButton look = PanelUi.Options(lookRow, "How the camera sees the map");
                look.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                foreach (string l in PictureRecipe.Looks)
                {
                    look.AddItem(PanelUi.Words(l));
                }

                look.Select(Math.Max(0, PictureRecipe.Looks.ToList().IndexOf(r.Look)));
                look.ItemSelected += index => change(x => x.Look = PictureRecipe.Looks[(int)index]);
                Spin(settings, "FOV", "The camera's horizontal field of view in degrees", r.Fov, PictureRecipe.MinFov, PictureRecipe.MaxFov, 1, v => change(x => x.Fov = v));
                break;
        }

        PanelUi.Caption(settings, note);
    }

    /// <summary>The preview of a recipe at <paramref name="size"/>, with a line saying where it comes from.</summary>
    private (Texture2D? Texture, string Note) Preview(PictureRecipe r, string slot, Vector2 size)
    {
        string? folder = Context.Session.Folder;
        switch (r.From)
        {
            case PictureSource.File:
                string? path = r.File is { } file ? ProjectPictures.Resolve(folder, file) : null;
                if (path is null || !File.Exists(path) || Image.LoadFromFile(path) is not { } image || image.IsEmpty())
                {
                    return (null, $"{r.File} cannot be read");
                }

                (int x, int y, int w, int h) = RgbaImage.CropArea(image.GetWidth(), image.GetHeight(), r.FocusX, r.FocusY, IdentityBuild.AspectOf(slot));
                Image crop = image.GetRegion(new Rect2I(x, y, w, h));
                crop.Resize((int)size.X * 2, (int)size.Y * 2, Image.Interpolation.Lanczos);
                return (ImageTexture.CreateFromImage(crop), $"{image.GetWidth()}\u00d7{image.GetHeight()} \u00b7 the preview shows the part the map keeps");
            case PictureSource.Camera:
                (int width, int height) = MapShots.SizeOf(slot);
                Bookmark? bookmark = Context.Project.Workspace.Bookmarks.FirstOrDefault(b => b.Name == r.Bookmark);
                string? render = bookmark is not null && folder is not null ? MapShots.CachePath(folder, MapShots.Key(r, bookmark, width, height)) : null;
                if (render is null || !File.Exists(render) || Image.LoadFromFile(render) is not { } shot || shot.IsEmpty())
                {
                    return (null, bookmark is null ? $"No bookmark named \u201c{r.Bookmark}\u201d" : "Not rendered yet: Render now renders it");
                }

                return (ImageTexture.CreateFromImage(shot),
                    $"Rendered {File.GetLastWriteTime(render).ToString("d MMM HH:mm", CultureInfo.InvariantCulture)}; Render now updates it after the map changes");
            default:
                return (null, "Drawn from the map on every build");
        }
    }

    private static void Spin(Control parent, string label, string tooltip, double value, double min, double max, double step, Action<double> changed)
    {
        HBoxContainer row = PanelUi.Row(parent, label, tooltip, EditorTheme.LabelColumnNarrow);
        SpinBox spin = PanelUi.Spin(row, min, max, step, value, tooltip, EditorTheme.SpinCompactWidth);
        spin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        spin.ValueChanged += v =>
        {
            if (Math.Abs(v - value) > step / 4)
            {
                changed(v);
            }
        };
    }

    private void RecipeLabel(HBoxContainer row, PictureRecipe? r)
    {
        string text = r?.From switch
        {
            PictureSource.File => Path.GetFileName(r.File) ?? "",
            PictureSource.Camera => $"Camera: {r.Bookmark}",
            PictureSource.Plan => "Generated plan",
            _ => "Not set",
        };
        Label label = WorkbenchUi.Value(row, text, r is null ? "Dim" : "");
        label.TooltipText = r?.File ?? text;
    }

    /// <summary>A menu of sources: a file, each bookmark in each look, the plan.</summary>
    private void SourceMenu(HBoxContainer row, string text, Action<PictureRecipe> chosen, bool allowPlan)
    {
        MenuButton button = WorkbenchUi.DropButton(row, text, "From a file, a camera bookmark or the generated plan");
        button.CustomMinimumSize = new Vector2(EditorTheme.SlotMenuWidth, 0);
        PopupMenu menu = button.GetPopup();
        var actions = new List<Action>();
        menu.AboutToPopup += () =>
        {
            menu.Clear();
            actions.Clear();
            void Add(string item, Action run)
            {
                menu.AddItem(item);
                actions.Add(run);
            }

            Add("From file\u2026", () => PickFile(chosen));
            foreach (Bookmark b in Context.Project.Workspace.Bookmarks)
            {
                foreach (string look in PictureRecipe.Looks)
                {
                    string name = b.Name;
                    Add($"Camera: {name} ({PanelUi.Words(look).ToLowerInvariant()})", () => chosen(PictureRecipe.OfCamera(name, look)));
                }
            }

            if (allowPlan)
            {
                Add("Generated plan", () => chosen(PictureRecipe.OfPlan()));
            }
        };
        menu.IndexPressed += index => actions[(int)index]();
    }

    /// <summary>Copies the picked image into the project's pictures/ folder; a format Core does not read (TGA) is saved
    /// there as PNG.</summary>
    private void PickFile(Action<PictureRecipe> chosen)
    {
        if (Context.Session.Folder is not { } folder)
        {
            Context.Log("Save the map first: its pictures are kept in the project folder.", Severity.Warning);
            return;
        }

        Dialogs.OpenFile("Map picture", ["*.png, *.jpg, *.jpeg, *.webp, *.bmp, *.tga ; Images"], path =>
        {
            try
            {
                chosen(PictureRecipe.OfFile(ImportPicture(folder, path)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                Context.Log($"Cannot use {path}: {e.Message}", Severity.Error);
            }
        });
    }

    private static string ImportPicture(string projectFolder, string path)
    {
        if (ProjectPictures.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            return ProjectPictures.Import(projectFolder, path);
        }

        Image image = Image.LoadFromFile(path) ?? throw new InvalidOperationException("the image cannot be read");
        string png = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(path) + ".png");
        if (image.SavePng(png) != Error.Ok)
        {
            throw new IOException("the image cannot be saved as PNG");
        }

        try
        {
            return ProjectPictures.Import(projectFolder, png);
        }
        finally
        {
            File.Delete(png);
        }
    }

    // ---- places ---------------------------------------------------------------------------------------------------

    private void FillPlaces()
    {
        Clear(_places);
        foreach (Place place in Id.Places)
        {
            int number = place.Number;
            HBoxContainer row = PanelUi.Row(_places, $"{number}.", place.At is { } at ? $"At {at}" : "No position: listed in the legend only", EditorTheme.SpaceXxl);
            var name = new LineEdit { Text = place.Name, SizeFlagsHorizontal = SizeFlags.ExpandFill, SelectAllOnFocus = true, CustomMinimumSize = new Vector2(80, 0) };
            name.TextSubmitted += text => RenamePlace(number, text);
            name.FocusExited += () => RenamePlace(number, name.Text);
            row.AddChild(name);
            HBoxContainer actions = WorkbenchUi.Actions(row);
            PanelUi.IconButton(actions, "pin", "Move here: the floor under the camera (its strategic map floor follows the height)", () =>
            {
                Vec3 at = FloorUnderCamera();
                Edit($"Move place {number}", i =>
                {
                    Place moved = i.Places.First(p => p.Number == number);
                    moved.At = at;
                    moved.Floor = null;
                });
            });
            Button go = PanelUi.IconButton(actions, "goto", "Look at the place from above", () =>
            {
                if (place.At is { } where)
                {
                    Context.ViewFrom(where + new Vec3(0, 0, 1024), new Rot(-16384, 0, 0));
                }
            });
            go.Disabled = place.At is null;
            PanelUi.IconButton(actions, "trash", "Remove the place", () => Edit($"Remove place {number}", i => i.Places.RemoveAll(p => p.Number == number)), danger: true);
        }

        if (Id.Places.Count == 0)
        {
            PanelUi.Caption(_places, "Number the places the briefing plan points out: look at one, then Add at camera.");
        }
        else
        {
            string brief = SideFiles.LegendOf(Id.Places);
            Label legend = PanelUi.Caption(_places, "In game: " + Readable(brief));
            legend.TooltipText = "The briefing plan's legend, built from the places\nLegendMapInBrief: " + brief;
            legend.MouseFilter = MouseFilterEnum.Pass;
        }
    }

    private void RenamePlace(int number, string text)
    {
        if (Id.Places.FirstOrDefault(p => p.Number == number) is { } place && place.Name != text.Trim() && text.Trim().Length <= ProjectIdentity.MaxName)
        {
            Edit($"Rename place {number}", i => i.Places.First(p => p.Number == number).Name = text.Trim());
        }
    }

    private void AddPlaceAtCamera()
    {
        int number = Enumerable.Range(1, 99).FirstOrDefault(n => Id.Places.All(p => p.Number != n));
        if (number == 0)
        {
            Context.Log("The map has 99 places already.", Severity.Warning);
            return;
        }

        Vec3 at = FloorUnderCamera();
        Edit($"Add place {number}", i =>
        {
            i.Places.Add(new Place { Number = number, Name = $"Place {number}", At = at });
            i.Places.Sort((a, b) => a.Number.CompareTo(b.Number));
        });
        Context.SetStatus($"Place {number} added at {at}.");
    }

    /// <summary>The floor under the camera (the brushes' floor; the camera's own height when there is none, as over a
    /// baked world).</summary>
    private Vec3 FloorUnderCamera()
    {
        Vec3 camera = Context.Project.Workspace.CameraLocation;
        double? drop = OpenScct.Editor.Core.Geometry.SolidQuery.Of(Context.Project).DropToFloor(camera, 8192);
        return drop is { } d ? camera - new Vec3(0, 0, d) : camera;
    }

    // ---- translations ---------------------------------------------------------------------------------------------

    /// <summary>The translation picker and, for the picked language, its texts; an empty box shows the map's own text
    /// (the fallback) greyed.</summary>
    private void FillTexts()
    {
        Clear(_texts);
        _textSyncers.Clear();
        string tooltip = "The map's texts in other languages (ui/text/<lang>.json); OpenSCCT shows the one for the game's language ("
            + string.Join(", ", ProjectIdentity.GameLanguages) + ")";
        HBoxContainer row = PanelUi.Row(_texts, "Language", tooltip);
        OptionButton languages = PanelUi.Options(row, tooltip);
        languages.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        List<string> codes = Id.Texts.Keys.ToList();
        foreach (string code in codes)
        {
            languages.AddItem(code);
        }

        if (_language is null || !Id.Texts.ContainsKey(_language))
        {
            _language = codes.FirstOrDefault();
        }

        languages.Selected = _language is null ? -1 : codes.IndexOf(_language);
        if (codes.Count == 0)
        {
            languages.Text = "None";
            languages.Disabled = true;
        }

        languages.ItemSelected += index =>
        {
            _language = codes[(int)index];
            FillTexts();
            Sync();
        };
        var add = new LineEdit
        {
            PlaceholderText = "fr", MaxLength = 3, CustomMinimumSize = new Vector2(EditorTheme.SpinCompactWidth, 0),
            TooltipText = "A language code; OpenSCCT shows " + string.Join(", ", ProjectIdentity.GameLanguages),
        };
        add.TextSubmitted += AddLanguage;
        row.AddChild(add);
        PanelUi.IconButton(row, "plus", "Add a translation into the language typed", () => AddLanguage(add.Text));
        if (_language is not { } lang)
        {
            PanelUi.Caption(_texts, $"No translations: every game shows the map's own texts ({Id.Language}). Type a language code and press + to add one.");
            return;
        }

        if (!ProjectIdentity.GameLanguages.Contains(lang))
        {
            PanelUi.Callout(_texts, Severity.Info, $"OpenSCCT does not show '{lang}' texts: it reads {string.Join(", ", ProjectIdentity.GameLanguages)}.");
        }

        TranslationRow(lang, "Name", "The name in this language", ProjectIdentity.MaxName, Context.Project.Title, t => t.Name, (t, v) => t.Name = v);
        TranslationRow(lang, "Description", "The description in this language", ProjectIdentity.MaxDescription, Id.Description ?? Id.Dossier.Summary,
            t => t.Description, (t, v) => t.Description = v, prose: true);
        TranslationRow(lang, "When", "Dossier: when the map is set", ProjectIdentity.MaxDossierLine, Id.Dossier.When, t => t.Dossier.When,
            (t, v) => t.Dossier.When = v);
        TranslationRow(lang, "Where", "Dossier: where it is set", ProjectIdentity.MaxDossierLine, Id.Dossier.Where, t => t.Dossier.Where,
            (t, v) => t.Dossier.Where = v);
        TranslationRow(lang, "Summary", "Dossier: one line about the map", ProjectIdentity.MaxDescription, Id.Dossier.Summary, t => t.Dossier.Summary,
            (t, v) => t.Dossier.Summary = v, prose: true);
        foreach (Place place in Id.Places)
        {
            int number = place.Number;
            TranslationRow(lang, $"{number}.", $"The name of place {number} in this language", ProjectIdentity.MaxName, place.Name,
                t => t.Places.GetValueOrDefault(number, ""), (t, v) =>
                {
                    if (v.Length == 0)
                    {
                        t.Places.Remove(number);
                    }
                    else
                    {
                        t.Places[number] = v;
                    }
                });
        }

        HFlowContainer bar = PanelUi.Toolbar(_texts);
        PanelUi.Button(bar, $"Remove {lang}", "Remove this translation", () =>
        {
            _language = null;
            Edit($"Remove the {lang} texts", i => i.Texts.Remove(lang));
        }).Icon = Icons.Get("trash", EditorTheme.Text);
    }

    /// <summary>One translated text, at most <paramref name="max"/> characters as the map's own; empty, it shows the
    /// map's own text and the game falls back to it. <paramref name="prose"/> as for the map's own text.</summary>
    private void TranslationRow(string lang, string label, string tooltip, int max, string fallback, Func<IdentityText, string> read,
        Action<IdentityText, string> write, bool prose = false)
    {
        Control edit = TextRow(_texts, label, $"{tooltip} (at most {max} characters)", () => Id.Texts.TryGetValue(lang, out IdentityText? t) ? read(t) : "",
            text => text.Trim().Length <= max
                ? Cmd($"{lang} texts", i =>
                {
                    if (!i.Texts.TryGetValue(lang, out IdentityText? t))
                    {
                        i.Texts[lang] = t = new IdentityText();
                    }

                    write(t, text.Trim());
                })
                : null, _textSyncers, prose);
        if (edit is TextEdit box)
        {
            box.PlaceholderText = fallback;
        }
        else
        {
            ((LineEdit)edit).PlaceholderText = fallback;
        }
    }

    private void AddLanguage(string text)
    {
        string lang = text.Trim().ToLowerInvariant();
        if (!ProjectIdentity.IsLanguage(lang))
        {
            Context.SetStatus("A language is a 2 or 3 letter code (fr, de, es).");
        }
        else if (lang == Id.Language)
        {
            Context.SetStatus($"'{lang}' is the map's own language: edit its texts above.");
        }
        else
        {
            _language = lang;
            if (!Id.Texts.ContainsKey(lang))
            {
                Edit($"Add {lang} texts", i => i.Texts[lang] = new IdentityText());
            }
        }
    }

    // ---- render ---------------------------------------------------------------------------------------------------

    /// <summary>Builds the map, renders the camera pictures OpenSCCT has not rendered for this version of it, then
    /// builds again so the map carries them.</summary>
    private void RenderPictures()
    {
        if (EditorServices.Compiler is not { } compiler || Context.Session.Folder is not { } folder)
        {
            Context.Log("Save the map first: rendered pictures are kept in the project folder.", Severity.Warning);
            return;
        }

        if (GameLauncher.FindGame() is not { } exe)
        {
            Context.Log("No OpenSCCT game found: set OSE_GAME_EXE (or OPENSCCT_EXE), or install OpenSCCT with its launcher.", Severity.Error);
            return;
        }

        if (GameExe.TooOld(exe) is { } tooOld)
        {
            Context.Log(tooOld, Severity.Error);
            return;
        }

        MapProject snapshot = ProjectSnapshot.Take(Context.Project);
        string output = BuildActions.OutputFolder(snapshot.Id);
        var options = new CompileOptions
        {
            ContentRoot = EditorServices.ContentRoot,
            ProjectFolder = folder,
            Progress = line => BackgroundJobs.Post(() => Context.SetStatus(line)),
        };
        Context.Log($"Rendering the pictures of {snapshot.Id} with {exe}");
        BackgroundJobs.Run(RenderJob, cancel =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            CompileResult first = compiler.Compile(snapshot, output, options);
            if (!first.Ok)
            {
                return (Shots: new List<MapShotResult>(), Build: first);
            }

            List<CameraShot> shots = MapShots.CameraSlots(snapshot, out _);
            // On the job's worker thread, not the main thread; the game is stopped if the editor quits.
            List<MapShotResult> results = MapShots.RenderAsync(snapshot, folder, output, shots, exe, force: false,
                line => BackgroundJobs.Post(() => Context.SetStatus(line)), cancel).GetAwaiter().GetResult();
            return (Shots: results, Build: compiler.Compile(snapshot, output, options));
        }, done =>
        {
            foreach (MapShotResult r in done.Shots)
            {
                Context.Log(r.Ok ? $"Picture {r.Slot}: {(r.Cached ? "cached" : "rendered")}" : $"Picture {r.Slot}: {r.Error}",
                    r.Ok ? Severity.Info : Severity.Error);
            }

            Context.SetFindings(done.Build.Findings);
            Context.SetStatus(done.Build.Ok ? $"Pictures ready in {output}" : "Build failed");
            // The previews show the new renders.
            _listsSignature = null;
            _sync.Queue();
        });
    }

    // ---- rows -----------------------------------------------------------------------------------------------------

    /// <summary>A text field in the label column, or with <paramref name="prose"/> a wrapping box under its name for a
    /// sentence or two (its line breaks become spaces: the texts are single lines). Returns the field: a LineEdit, or
    /// a TextEdit for prose.</summary>
    private Control TextRow(Control parent, string label, string tooltip, Func<string> read, Func<string, IEditCommand?> commit,
        List<Action>? syncers = null, bool prose = false)
    {
        // Only text that differs from what the box showed is an edit, and only on the map the row was built for.
        MapProject owner = Context.Project;
        string shown = "";
        Control edit;
        Func<string> text;
        Action<string> setText;
        if (prose)
        {
            TextEdit box = PanelUi.Prose(parent, label, tooltip);
            (edit, text, setText) = (box, () => box.Text.ReplaceLineEndings(" "), t => box.Text = t);
        }
        else
        {
            HBoxContainer row = PanelUi.Row(parent, label, tooltip);
            var line = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, SelectAllOnFocus = true, TooltipText = tooltip };
            row.AddChild(line);
            line.TextSubmitted += t => Final(t);
            (edit, text, setText) = (line, () => line.Text, t => line.Text = t);
        }

        void Show()
        {
            shown = read();
            setText(shown);
        }

        void Final(string value)
        {
            // Every commit trims, so text that differs only in spaces at its ends is no edit (Enter in a prose box adds
            // a line break, which becomes a space).
            if (value.Trim() == shown.Trim() || !ReferenceEquals(Context.Project, owner))
            {
                return;
            }

            shown = value;
            if (commit(value) is { } cmd)
            {
                Context.Execute(cmd);
            }
            else
            {
                Context.SetStatus($"{label}: not a valid value");
            }
        }

        edit.FocusExited += () =>
        {
            Final(text());
            if (ReferenceEquals(Context.Project, owner))
            {
                Show();
            }
        };
        (syncers ?? _syncers).Add(() =>
        {
            if (!edit.HasFocus())
            {
                Show();
            }
        });
        return edit;
    }

    /// <summary>A legend line as people read it: its "|"-separated parts joined with " · ".</summary>
    private static string Readable(string legend) =>
        string.Join(" · ", legend.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string? Opt(string text) => text.Trim() is { Length: > 0 } t ? t : null;

    private static List<string> List(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static void Clear(Control box)
    {
        foreach (Node child in box.GetChildren())
        {
            box.RemoveChild(child);
            child.QueueFree();
        }
    }
}
