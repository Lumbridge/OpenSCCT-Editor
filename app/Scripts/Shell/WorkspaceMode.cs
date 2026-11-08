using Godot;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Viewports;

namespace OpenScctEditor.Shell;

/// <summary>Workspace choices are local UI state and never edit a map.</summary>
[ShellPart(Order = 80)]
public sealed class WorkspaceMode : IShellPart
{
    public static bool Simple => AppSettings.GetString("workspace", "mode", "advanced") == "simple";
    public static event Action? Changed;
    public static bool Allows(string panel) => !Simple || panel is "Building" or "Your first map" or "Inspector" or "Outliner" or "Assets" or "Prefabs" or "Map Check" or "Lights" or "Stairs" or "Geometry" or "Windows" or "Missions";
    public static bool AllowsAction(string id) => !Simple || !id.StartsWith("tools.", StringComparison.Ordinal) ||
        id is "tools.select" or "tools.move" or "tools.rotate" or "tools.scale" or "tools.brush" or "tools.place" or "tools.face" or "tools.stairs" or "tools.windows";

    /// <summary>Run before any UI component writes settings, so an existing profile never changes defaults.</summary>
    public static void Initialize()
    {
        if (!AppSettings.Has("workspace", "mode"))
        {
            bool existing = AppSettings.Exists;
            AppSettings.Set("workspace", "mode", existing ? "advanced" : "simple");
        }
    }

    public void Attach(MainWindow window, EditorContext context)
    {
        context.AddAction(new EditorAction("workspace.simple", "Simple Workspace", "View/Workspace", () => Set(true))
        { IsChecked = () => Simple, RadioGroup = "workspace", Order = 100 });
        context.AddAction(new EditorAction("workspace.advanced", "Full Workspace", "View/Workspace", () => Set(false))
        { IsChecked = () => !Simple, RadioGroup = "workspace", Order = 110 });
        context.AddAction(new EditorAction("workspace.expand", "Expand workspace", "", () => Set(false))
        { IsEnabled = () => Simple, Description = "Show all editor tools and panels" });
        context.AddAction(new EditorAction("workspace.collapse", "Collapse workspace", "", () => Set(true))
        { IsEnabled = () => !Simple, Description = "Show the simpler set of tools and panels" });
        var expand = new ActionButton(context, "workspace.expand", Icons.Get("workspace-expand", EditorTheme.Text), variation: "IconButton")
        { VisibleWhen = () => Simple };
        var collapse = new ActionButton(context, "workspace.collapse", Icons.Get("workspace-collapse", EditorTheme.Text), variation: "IconButton")
        { VisibleWhen = () => !Simple };
        window.StatusRow.AddChild(expand);
        window.StatusRow.AddChild(collapse);
        void Refresh() { expand.Refresh(); collapse.Refresh(); }
        Changed += Refresh;
        Refresh();
        Documents.BeforeSave += () => window.SaveWorkspaceLayout(Simple ? "simple" : "advanced");
        window.CloseRequested += () => window.SaveWorkspaceLayout(Simple ? "simple" : "advanced");
        Apply(window);
    }

    public static void Set(bool simple)
    {
        if (Simple == simple || MainWindow.Instance is not { } window) return;
        window.SaveWorkspaceLayout(Simple ? "simple" : "advanced");
        AppSettings.Set("workspace", "mode", simple ? "simple" : "advanced");
        Apply(window);
        Changed?.Invoke();
    }

    private static void Apply(MainWindow window)
    {
        window.RestoreWorkspaceLayout(Simple ? "simple" : "advanced");
        foreach (TabContainer dock in window.Docks) DockTabs.Fit(dock);
        window.UpdateWorkspaceDocks();
        if (Simple)
        {
            if (!AppSettings.Has("workspace.simple", "LeftDock")) DockTabs.ShowPanel("Building");
            if (!AppSettings.Has("workspace.simple", "RightDock"))
                DockTabs.ShowPanel(DockTabs.TutorialOpen && !EditorContext.Instance.Project.Workspace.FirstMap.Hidden ? "Your first map" : "Inspector");
        }
        else
        {
            if (!AppSettings.Has("workspace.advanced", "RightDock")) DockTabs.ShowPanel("Inspector");
            if (!AppSettings.Has("workspace.advanced", "LeftDock")) DockTabs.ShowPanel("Outliner");
        }
    }
}
