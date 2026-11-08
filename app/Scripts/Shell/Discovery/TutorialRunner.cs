using Godot;
using OpenScct.Editor.Core.Assets;
using OpenScct.Editor.Core.Model;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Tools;

namespace OpenScctEditor.Shell;

/// <summary>A cancellable, modeless lesson card. Lessons only navigate; users perform their own map edits.</summary>
[ShellPart(Order = 92)]
public partial class TutorialRunner : PanelContainer, IShellPart
{
    public static TutorialRunner? Instance { get; private set; }
    private EditorContext _ctx = null!;
    private EditorTutorial? _lesson;
    private int _step;
    private Label _title = null!, _progress = null!, _stepTitle = null!, _body = null!;
    private Button _back = null!, _next = null!;
    private ScrollContainer _instructions = null!;
    private HFlowContainer _buttons = null!;
    private Button _fold = null!;
    private bool _collapsed;
    private string? _subjectId;
    private readonly DeferredAction _refreshGuidance = new(() => Instance?.RefreshGuidance());
    public string? LessonId => _lesson?.Id;
    public int StepIndex => _step;

    public void Attach(MainWindow window, EditorContext context)
    {
        Instance = this;
        _ctx = context;
        Name = "TutorialCard";
        ThemeTypeVariation = "Palette";
        ZIndex = 900;
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        window.AddChild(this);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", EditorTheme.SpaceS);
        AddChild(box);
        var heading = new HBoxContainer(); box.AddChild(heading);
        _title = PanelUi.Caption(heading, "");
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _title.AddThemeColorOverride("font_color", EditorTheme.Accent);
        _fold = PanelUi.Button(heading, "−", "Minimise or expand the instructions while you work", ToggleCollapsed);
        PanelUi.Button(heading, "×", "Cancel tutorial at any point. Your map edits are kept.", Cancel);
        _progress = PanelUi.Caption(box, "");
        _stepTitle = PanelUi.Caption(box, "");
        _stepTitle.AddThemeFontOverride("font", EditorTheme.UiFontSemibold);
        _instructions = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        box.AddChild(_instructions);
        _body = PanelUi.Caption(_instructions, "");
        _body.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var buttons = new HFlowContainer(); _buttons = buttons; box.AddChild(buttons);
        _back = PanelUi.Button(buttons, "Back", "Return to the previous step", () => Move(-1));
        PanelUi.Button(buttons, "Show me again", "Reveal and highlight this step's control again", ShowTarget);
        _next = PanelUi.Button(buttons, "Next", "Continue at your own pace", () => Move(1));
        PanelUi.Button(buttons, "Cancel", "Close the tutorial and clear its highlight", Cancel);
        foreach (Button button in DiscoveryNavigation.Descendants(box).OfType<Button>()) button.FocusMode = FocusModeEnum.All;
        foreach (EditorTutorial lesson in TutorialCatalog.All)
        {
            EditorTutorial captured = lesson;
            context.AddAction(new EditorAction("tutorial." + lesson.Id, "Tutorial: " + lesson.Title, "Help/Tutorials", () => Start(captured))
            { Description = lesson.Keywords + ". Guided steps with highlighted controls; cancel at any point." });
        }
        context.AddAction(new EditorAction("help.tutorials", "Browse Tutorials", "Help", () => CommandPalette.Instance?.OpenSearch("tutorial"))
        { Description = "Step-by-step help for building, gameplay, assets and map setup", Order = 21 });
        context.AddAction(new EditorAction("help.resumeTutorial", "Resume Last Tutorial", "Help", Resume)
        { Description = "Continue the last lesson at its saved step", IsEnabled = () => Last is not null, Order = 22 });
        context.AddAction(new EditorAction("help.cancelTutorial", "Cancel Current Tutorial", "Help", Cancel)
        { Description = "Close the lesson and its highlight; keep your edits", IsEnabled = () => _lesson is not null, Order = 23 });
        context.SessionReplaced += Cancel;
        context.ProjectChanged += _ => _refreshGuidance.Queue();
        context.SelectionChanged += () => _refreshGuidance.Queue();
    }

    private static EditorTutorial? Last => TutorialCatalog.All.FirstOrDefault(t => t.Id == AppSettings.GetString("tutorials", "last", ""));
    public void Resume()
    {
        if (Last is { } lesson) Start(lesson, resume: true);
    }

    public void Start(EditorTutorial lesson, bool resume = false)
    {
        _lesson = lesson;
        _subjectId = null;
        GuideSpotlight.Clear();
        _collapsed = true; ToggleCollapsed();
        _step = resume ? Math.Clamp(AppSettings.GetInt("tutorials", lesson.Id, 0), 0, lesson.Steps.Length - 1) : 0;
        AppSettings.Set("tutorials", "last", lesson.Id);
        Visible = true;
        Render();
    }

    public void Cancel()
    {
        _lesson = null;
        _subjectId = null;
        Visible = false;
        DiscoveryNavigation.Invalidate();
    }

    private void ToggleCollapsed()
    {
        _collapsed = !_collapsed;
        _progress.Visible = _stepTitle.Visible = _instructions.Visible = _buttons.Visible = !_collapsed;
        _fold.Text = _collapsed ? "+" : "−";
        Size = new Vector2(Size.X, 0);
    }

    private void Move(int delta)
    {
        if (_lesson is null) return;
        if (_step + delta >= _lesson.Steps.Length)
        {
            string id = _lesson.Id;
            AppSettings.Set("tutorials", "completed", AppSettings.GetStrings("tutorials", "completed").Append(id).Distinct().ToArray());
            AppSettings.Set("tutorials", id, 0);
            _ctx.SetStatus($"Tutorial complete: {_lesson.Title}. Reopen it any time from Ctrl+Shift+P.");
            Cancel();
            return;
        }
        _step = Math.Max(0, _step + delta);
        Render();
    }

    private void Render()
    {
        if (_lesson is null) return;
        TutorialStep step = ResolveStep();
        _title.Text = _lesson.Title;
        _progress.Text = $"Step {_step + 1} of {_lesson.Steps.Length} · work in your map, then Next";
        _stepTitle.Text = step.Title;
        _body.Text = step.Body;
        _instructions.ScrollVertical = 0;
        _back.Disabled = _step == 0;
        _next.Text = _step == _lesson.Steps.Length - 1 ? "Finish" : "Next";
        AppSettings.Set("tutorials", _lesson.Id, _step);
        ShowTarget();
    }

    private void ShowTarget()
    {
        if (_lesson is null) return;
        DiscoveryNavigation.Invalidate();
        string[] target = ResolveStep().Target.Split(':', 2);
        string[] detail = target[1].Split('|', 2);
        switch (target[0])
        {
            case "setup":
                Actor? subject = _subjectId is null ? null : _ctx.Project.FindActor(_subjectId);
                if (detail[0] is "inspect" or "dropzone")
                {
                    Actor? inspect = detail[0] == "dropzone" ? TutorialMapGuidance.DropZones(_ctx.Project, subject).FirstOrDefault() : subject;
                    if (inspect is not null) _ctx.Selection.Set([ObjectRef.Actor(inspect.Id)]);
                    DiscoveryNavigation.ShowPanel("Inspector", lesson: true);
                }
                else
                {
                    Actor? objective = TutorialMapGuidance.Objective(_ctx.Project, subject);
                    MissionsPanel.RevealTutorialObjective(objective?.Id, detail[0]);
                    if (detail[0] == "new-mission") DiscoveryNavigation.ShowPanel("Missions", "New mission", lesson: true);
                }
                break;
            case "panel": DiscoveryNavigation.ShowPanel(detail[0], detail.Length > 1 ? detail[1] : null, lesson: true); break;
            case "prefab":
                PrefabsPanel.ShowPrefab(detail[0]);
                DiscoveryNavigation.ShowPanel("Prefabs", detail.Length > 1 ? detail[1] : null, lesson: true);
                break;
            case "placeable": PlaceablesPanel.Instance?.RevealClass(detail[0], lesson: true); break;
            case "assetkind": AssetBrowserPanel.Instance?.RevealKind(Enum.Parse<AssetKind>(detail[0]), lesson: true); break;
            case "action":
                if (_ctx.FindAction(detail[0]) is { } action) DiscoveryNavigation.ShowAction(action, lesson: true);
                else _ctx.SetStatus("Find this command in the editor menus: " + detail[0]);
                break;
            case "tool":
                DiscoveryNavigation.ShowGe(detail[1], lesson: true);
                break;
            case "view":
                if (MainWindow.Instance is { } window) NavigationHighlight.Instance?.PointAt(window.ViewportArea, dismissOnLeave: false);
                break;
        }
    }

    private TutorialStep ResolveStep()
    {
        Actor? subject = TutorialMapGuidance.Subject(_ctx.Project, _lesson!.Id, _ctx.Selection.Items, _subjectId);
        _subjectId = subject?.Id;
        return TutorialMapGuidance.Resolve(_lesson, _step, _ctx.Project, subject);
    }

    private void RefreshGuidance()
    {
        if (_lesson is null || !Visible) return;
        TutorialStep step = ResolveStep();
        _stepTitle.Text = step.Title;
        _body.Text = step.Body;
    }

    public override void _Process(double delta)
    {
        if (!Visible || MainWindow.Instance is not { } window) return;
        Rect2 views = window.ViewportArea.GetGlobalRect();
        float width = Math.Min(360, Math.Max(280, window.Size.X - 32));
        float y = Math.Max(views.Position.Y + 12, 70);
        _instructions.CustomMinimumSize = new Vector2(0, Math.Clamp(window.Size.Y - y - 180, 48, 130));
        CustomMinimumSize = new Vector2(width, 0);
        Size = new Vector2(width, 0);
        Position = new Vector2(Math.Max(8, Math.Min(views.End.X - width - 12, window.Size.X - width - 12)), y);
    }

    public override void _Input(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true, Keycode: Key.Escape }) return;
        Control? focus = GetViewport().GuiGetFocusOwner();
        if (focus is not null && IsAncestorOf(focus)) { Cancel(); GetViewport().SetInputAsHandled(); }
    }
}
