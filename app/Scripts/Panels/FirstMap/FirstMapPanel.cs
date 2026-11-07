using Godot;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Scripting;
using OpenScctEditor.Viewports;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;
using OpenScctEditor.Shell.Background;

namespace OpenScctEditor.Panels;

[Panel("Your first map", DockSide.Right, Order = -10)]
public partial class FirstMapPanel : EditorPanel
{
    private VBoxContainer _body = null!;
    private readonly DeferredAction _refresh = new(() => Instance?.Redraw());
    public static FirstMapPanel? Instance { get; private set; }
    private string _revision = "";
    private bool _revisionDirty = true;
    private static readonly string[] Titles = ["Save and name your map", "Explore the starter room", "Connect another room", "Position team starts", "Add an objective", "Check your map", "Play your map"];
    private static readonly string[] Hints =
    [
        "Give your creation a name, then choose where to save its project folder.",
        "Your map already has a room, a light and both team starts. Frame the room. Hold the right mouse button to look around; use W A S D while holding it to fly.",
        "Select a carved room, then connect a new room to its east wall. A doorway and light are included. Move or resize pieces with the usual tools afterwards.",
        "Select the team starts, then use Move to position them on clear floors. Green labels mark spies; red labels mark mercenaries. The starter map already has one of each.",
        "Drag an Objective computer from Building into a room. It includes the objective and its connected hackable terminal. Place it where a spy can reach it.",
        "Run Map Check. Open its results to locate and fix errors, then check again. Editing the map makes the old check incomplete.",
        "Build and Play opens your current map in OpenSCCT. Try reaching and hacking the computer. A successful launch completes this step; judge the gameplay yourself.",
    ];

    protected override void Build()
    {
        Instance = this;
        _body = PanelUi.Scroll(this);
        Context.ProjectChanged += _ => RefreshRevision();
        Context.Saved += () => _refresh.Queue();
        Context.GuideChanged += () => _refresh.Queue();
        Context.AddAction(new EditorAction("help.firstMap", "Your First Map", "Help", () =>
        {
            Context.UpdateGuide(g => g.Hidden = false);
            DockTabs.ShowPanel("Your first map");
        }) { Order = 20, Description = "Resume a step-by-step guide alongside your own map" });
        Context.AddAction(new EditorAction("guide.connectRoom", "Connect a Room", "Brush", ConnectRoom)
        { Order = 20, Description = "Add a room, connecting doorway and light to the east of the selected carved room" });
        Context.AddAction(new EditorAction("guide.setup", "Set Up Playtesting", "Help", ShowSetup) { Order = 25 });
        WorkspaceMode.Changed += () => _refresh.Queue();
    }

    public override void Refresh() => RefreshRevision();
    private void RefreshRevision()
    {
        _revisionDirty = true;
        _refresh.Queue();
    }

    private void Redraw()
    {
        if (_revisionDirty) { _revision = FirstMapGuide.Revision(Context.Project); _revisionDirty = false; }
        foreach (Node child in _body.GetChildren()) { _body.RemoveChild(child); child.QueueFree(); }
        PanelUi.Title(_body, "Your first map");
        if (Context.Project.Workspace.FirstMap.Hidden)
        {
            PanelUi.Caption(_body, "Guide hidden. Keep building at your own pace.");
            PanelUi.Button(_body, "Resume guide", "Continue this map's checklist", () => Context.UpdateGuide(g => g.Hidden = false));
            return;
        }
        PanelUi.Caption(_body, "Build a small playable map, one step at a time.");
        bool[] done = FirstMapGuide.Steps(Context.Project, Context.Session.Folder is not null, _revision);
        int next = Array.FindIndex(done, d => !d);
        PanelUi.Caption(_body, $"{done.Count(d => d)} of {done.Length} complete");
        for (int i = 0; i < Titles.Length; i++)
        {
            int step = i;
            PanelUi.Button(_body, $"{(done[i] ? "✓" : (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))}  {Titles[i]}", Hints[i], () => ShowStep(step));
        }
        if (next >= 0) ShowExplanation(next);
        else
        {
            PanelUi.Callout(_body, Severity.Info, "You have launched your first map! Keep testing both teams and improving the layout.");
            if (!AppSettings.GetBool("workspace", "first_map_complete", false)) AppSettings.Set("workspace", "first_map_complete", true);
        }
        PanelUi.Button(_body, "Hide guide", "Hide these instructions. Reopen them from Help > Your First Map.", () => Context.UpdateGuide(g => g.Hidden = true));
    }

    private void ShowExplanation(int step)
    {
        PanelUi.Header(_body, Titles[step]);
        _body.AddChild(new FirstMapIllustration(step));
        PanelUi.Caption(_body, Hints[step]);
        string[] buttons = ["Name and save", "Show starter room", "Connect a room", "Select team starts", "Choose objective computer", "Check map", "Build and play"];
        PanelUi.Primary(_body, buttons[step], Hints[step], () => Act(step));
        if (step == 1) PanelUi.Button(_body, "I've explored the room", "Mark the navigation lesson complete", () => Context.UpdateGuide(g => g.Explored = true));
        if (step >= 5) PanelUi.Button(_body, "Show check results", "See any errors and jump to the affected objects", () => DockTabs.ShowPanel("Map Check"));
        if (step == 6 && PrerequisiteProblem() is { } problem)
        {
            PanelUi.Callout(_body, Severity.Warning, problem);
            PanelUi.Button(_body, "Set up playtesting", "Find the game and content required to play your map", ShowSetup);
        }
    }

    private void ShowStep(int step)
    {
        // Keep the checklist in place and replace only its explanation.
        Redraw();
        while (_body.GetChildCount() > Titles.Length + 3)
        {
            Node last = _body.GetChild(_body.GetChildCount() - 1); _body.RemoveChild(last); last.QueueFree();
        }
        ShowExplanation(step);
        PanelUi.Button(_body, "Hide guide", "Reopen from Help > Your First Map", () => Context.UpdateGuide(g => g.Hidden = true));
    }

    private void Act(int step)
    {
        switch (step)
        {
            case 0:
                var dialog = new AcceptDialog { Title = "Name your map", OkButtonText = "Save map" };
                var name = new LineEdit { Text = Context.Project.Title == "New map" ? "My first map" : Context.Project.Title, MaxLength = ProjectIdentity.MaxName, CustomMinimumSize = new Vector2(EditorTheme.PreferencesNavWidth * 2, 0) };
                dialog.AddChild(name);
                name.TextChanged += text => dialog.GetOkButton().Disabled = string.IsNullOrWhiteSpace(text);
                dialog.Confirmed += () =>
                {
                    if (string.IsNullOrWhiteSpace(name.Text)) return;
                    string title = name.Text.Trim();
                    Context.Execute(new ProjectSettingsCommand("Name map", p => p.Title = title));
                    Context.Run("file.save");
                };
                MainWindow.Instance!.AddChild(dialog); dialog.PopupCentered(); name.GrabFocus();
                dialog.Confirmed += dialog.QueueFree;
                dialog.Canceled += dialog.QueueFree;
                break;
            case 1:
                Context.Run("view.layout.perspective");
                var room = Context.Project.Brushes.FirstOrDefault(b => b.Oper == CsgOper.Subtract);
                if (room is not null) Context.Focus([new ObjectRef(ObjectKind.Brush, room.Id)]);
                break;
            case 2: ConnectRoom(); break;
            case 3:
                Context.Selection.Set(Context.Project.Actors.Where(FirstMapGuide.IsStart).Select(a => new ObjectRef(ObjectKind.Actor, a.Id)));
                Context.Focus(Context.Selection.Items); Context.Run("tools.move"); DockTabs.ShowPanel("Inspector");
                break;
            case 4: BuildingPanel.Instance?.Pick("objective_computer"); break;
            case 5: Context.Run("build.check"); DockTabs.ShowPanel("Map Check"); break;
            case 6:
                if (PrerequisiteProblem() is null) Context.Run("build.play"); else ShowSetup();
                break;
        }
    }

    private void ConnectRoom()
    {
        Brush? selected = Context.Selection.Items.Where(r => r.Kind == ObjectKind.Brush).Select(r => Context.Project.FindBrush(r.Id)).FirstOrDefault(b => b?.Oper == CsgOper.Subtract);
        Brush? room = selected ?? Context.Project.Brushes.FirstOrDefault(b => b.Oper == CsgOper.Subtract);
        if (room is null) { Context.SetStatus("Add a room from Building first."); return; }
        var bounds = BrushGeometry.Of(room);
        double floor = bounds.Min.Z, y = (bounds.Min.Y + bounds.Max.Y) / 2;
        var at = new Vec3(bounds.Max.X + 64 + 512, y, floor);
        var ops = new[]
        {
            ApiJson.Obj(("op", ApiJson.Str("create")), ("id", ApiJson.Str("$room")),
                ("brush", ApiJson.Obj(("kind", ApiJson.Str("room")), ("height", ApiJson.Num(512)))), ("at", ApiJson.Vec(at))),
            ApiJson.Obj(("op", ApiJson.Str("create")),
                ("brush", ApiJson.Obj(("kind", ApiJson.Str("doorway")), ("depth", ApiJson.Num(192)), ("width", ApiJson.Num(192)))),
                ("at", ApiJson.Vec(new Vec3(bounds.Max.X + 32, y, floor)))),
            ApiJson.Obj(("op", ApiJson.Str("create")), ("class", ApiJson.Str("Light")),
                ("at", ApiJson.Vec(at + new Vec3(0, 0, 448))))
        };
        BeginnerEdits.Apply(Context, "Connect a room", ops);
    }

    public static string? PrerequisiteProblem()
    {
        if (GameExe.Find() is not { } exe) return "Install OpenSCCT through its launcher before playing your map.";
        if (GameExe.TooOld(exe) is { } old) return old;
        if (EditorServices.ContentRoot is not { } content || !File.Exists(Path.Combine(content, "shared", "classes.json")))
            return "Install the shared content pack to give gameplay objects their models and settings.";
        return null;
    }

    public static void ShowSetup()
    {
        var dialog = new AcceptDialog { Title = "Set up playtesting", OkButtonText = "Close" };
        var body = new VBoxContainer { CustomMinimumSize = new Vector2(EditorTheme.PreferencesNavWidth * 3, 0) };
        dialog.AddChild(body);
        PanelUi.Caption(body, PrerequisiteProblem() ?? "The game and shared content are available.");
        PanelUi.Caption(body, "Use the OpenSCCT launcher to install or update the game and shared content. Use Get Packs in Assets for map asset libraries. Restart the editor after installing the game if it is not found yet.");
        PanelUi.Button(body, "Get OpenSCCT", "Open the OpenSCCT download and installation page", () => ExternalPrograms.Open("https://github.com/Lumbridge/OpenSCCT#play"));
        if (ContextForSetup().FindAction("content.packs") is not null)
            PanelUi.Button(body, "Get content packs", "Open the editor's content pack installer", () => ContextForSetup().Run("content.packs"));
        MainWindow.Instance!.AddChild(dialog); dialog.PopupCentered(); dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree;
    }
    private static EditorContext ContextForSetup() => EditorContext.Instance;
}

/// <summary>Small original diagram, drawn with editor theme tokens at any UI scale.</summary>
public partial class FirstMapIllustration : Control
{
    private readonly int _step;
    public FirstMapIllustration() { }
    public FirstMapIllustration(int step) { _step = step; CustomMinimumSize = new Vector2(0, EditorTheme.RowHeight * 3); Resized += QueueRedraw; }
    public override void _Draw()
    {
        float w = Math.Max(1, Size.X), h = Size.Y;
        if (_step is 0 or 1 or 5 or 6)
        {
            string[] labels = _step switch
            {
                0 => ["Name", "Save"], 1 => ["Look", "Move"], 5 => ["Check", "Fix"], _ => ["Build", "Play"],
            };
            DrawRect(new Rect2(4, 8, w * .37f, h - 16), EditorTheme.Accent, false, 2);
            DrawRect(new Rect2(w * .60f, 8, w * .37f, h - 16), EditorTheme.TextDim, false, 2);
            DrawLine(new Vector2(w * .37f + 4, h / 2), new Vector2(w * .60f, h / 2), EditorTheme.Accent, 2);
            DrawString(GetThemeDefaultFont(), new Vector2(12, h / 2 + 4), labels[0], fontSize: EditorTheme.FontSizeSmall, modulate: EditorTheme.Text);
            DrawString(GetThemeDefaultFont(), new Vector2(w * .60f + 8, h / 2 + 4), labels[1], fontSize: EditorTheme.FontSizeSmall, modulate: EditorTheme.Text);
            return;
        }
        DrawRect(new Rect2(4, 8, w * .37f, h - 16), EditorTheme.Accent, false, 2);
        DrawRect(new Rect2(w * .60f, 8, w * .37f, h - 16), EditorTheme.TextDim, false, 2);
        DrawLine(new Vector2(w * .37f + 4, h / 2), new Vector2(w * .60f, h / 2), EditorTheme.Accent, 4);
        DrawCircle(new Vector2(w * .15f, h * .5f), 5, EditorTheme.Accent);
        DrawCircle(new Vector2(w * .28f, h * .5f), 5, EditorTheme.Warning);
        if (_step >= 4) DrawRect(new Rect2(w * .76f, h * .35f, 12, 12), EditorTheme.Success);
    }
}
