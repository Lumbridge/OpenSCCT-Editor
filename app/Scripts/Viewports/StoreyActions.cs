using System.Globalization;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;
using OpenScct.Editor.Core.Scripting.Live;
using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Services;
using OpenScctEditor.Core;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Viewports;

/// <summary>
/// View > Storeys: step up and down a storey (PgUp, PgDn; past either end every storey shows), show all, pick one, or
/// copy the one shown to a new storey on top or underneath. The storeys are found again off the main thread when a map
/// opens and a moment after the brushes change; the map's own storeys are taken at once. The storey shown is kept in
/// the workspace (saved with the map, like the camera), and the views follow it when a script or the undo of a
/// <c>shown</c> edit changes it.
/// </summary>
[ShellPart(Order = -19)]
public sealed class StoreyActions : IShellPart
{
    private const string PickPrefix = "view.storey.pick.";
    private const double SettleSeconds = 1.5;
    private EditorContext _ctx = null!;
    private Godot.Timer _settle = null!;
    private int _generation;

    /// <summary>True while the filter changes because a map opened or the workspace asked for it: the workspace
    /// already holds the storey to show, so the change is not written back.</summary>
    private bool _following;

    public void Attach(MainWindow window, EditorContext context)
    {
        _ctx = context;
        _settle = new Godot.Timer { Name = "StoreyDetect", OneShot = true, WaitTime = SettleSeconds };
        _settle.Timeout += Detect;
        window.AddChild(_settle);

        bool HasStoreys() => StoreyFilter.List.Count > 0;
        context.AddAction(new EditorAction("view.storey.up", "Storey Up", "View/Storeys", () => Step(1))
        {
            Shortcut = "PgUp", Order = 100, IsEnabled = HasStoreys,
            Description = "Show the storey above (past the top storey, every storey shows)",
        });
        context.AddAction(new EditorAction("view.storey.down", "Storey Down", "View/Storeys", () => Step(-1))
        {
            Shortcut = "PgDn", Order = 110, IsEnabled = HasStoreys,
            Description = "Show the storey below (past the bottom storey, every storey shows)",
        });
        context.AddAction(new EditorAction("view.storey.all", "All Storeys", "View/Storeys", () => StoreyFilter.Show(-1))
        {
            Order = 120, RadioGroup = "view.storey", IsChecked = () => StoreyFilter.Active < 0,
        });
        context.AddAction(new EditorAction("view.storey.duplicate", "Duplicate Storey Above", "View/Storeys", () => DuplicateShown(below: false))
        {
            Order = 130, IsEnabled = () => StoreyFilter.Current is not null,
            Description = "Copy everything on the storey shown to a new storey on top of the highest one",
        });
        context.AddAction(new EditorAction("view.storey.duplicateBelow", "Duplicate Storey Below", "View/Storeys", () => DuplicateShown(below: true))
        {
            Order = 135, IsEnabled = () => StoreyFilter.Current is not null,
            Description = "Copy everything on the storey shown to a new storey under the lowest one",
        });
        context.AddAction(new EditorAction("view.storey.detect", "Find Storeys Again", "View/Storeys", Detect)
        {
            Order = 900,
            Description = "Work the storeys out again (the map's own storeys, or its floors: within 64 units is one floor, " +
                "floors under 6% of the largest are left out, at most 24)",
        });

        context.SessionReplaced += () =>
        {
            _following = true;
            StoreyFilter.SetList([]);
            _following = false;
            Detect();
        };
        context.ProjectChanged += _ =>
        {
            if (_ctx.Project.Workspace.Storeys.Count > 0)
            {
                // The map's own storeys need no detection: take them at once, so the next edit builds on this one.
                ++_generation;
                _settle.Stop();
                TakeList(Storeys.Of(_ctx.Project, _ctx.Session.Folder));
            }
            else if (StoreyFilter.List.Any(s => s.Floor is null))
            {
                // The map's own storeys were just dropped: find the floors now rather than after the settle.
                Detect();
            }
            else
            {
                _settle.Start();
            }

            FollowWorkspace();
        };
        StoreyFilter.Changed += () =>
        {
            RefreshPicks();
            if (!_following && StoreyFilter.List.Count > 0)
            {
                _ctx.Project.Workspace.ShownStorey = StoreyFilter.Current?.Level;
            }
        };
        Detect();
    }

    /// <summary>Shows the storey the workspace names, when that is not the one shown.</summary>
    private void FollowWorkspace()
    {
        int wanted = Storeys.Shown(StoreyFilter.List, _ctx.Project.Workspace.ShownStorey);
        if (wanted != StoreyFilter.Active)
        {
            _following = true;
            StoreyFilter.Show(wanted);
            _following = false;
        }
    }

    private void DuplicateShown(bool below)
    {
        if (StoreyFilter.Current is not { } storey)
        {
            return;
        }

        SvJsonObject op = ApiJson.Obj(("op", ApiJson.Str("storey.duplicate")), ("storey", ApiJson.Str(storey.Name)));
        if (below)
        {
            op.Add("below", ApiJson.Bool(true));
        }

        SvJsonObject doc = ApiJson.Obj(("format", ApiJson.Str(ChangeApplier.Format)), ("label", ApiJson.Str($"Duplicate {storey.Name}")),
            ("ops", ApiJson.Arr([op])));
        ChangeResult result = ChangeApplier.Apply(_ctx.Session, doc);
        foreach (string warning in result.Warnings)
        {
            _ctx.Log($"Duplicate {storey.Name}: {warning}", Severity.Warning);
        }

        _ctx.SetStatus(result.Ok ? $"Duplicated {storey.Name} {(below ? "under the lowest" : "above the highest")} storey"
            : $"Duplicate {storey.Name} failed: {ProposalBook.ErrorText(result.Json["error"] as SvJsonObject)}");
    }

    private void Step(int delta)
    {
        StoreyFilter.Step(delta);
        _ctx.SetStatus(StoreyFilter.Current is { } s
            ? $"Showing {s.Name} ({Height(s.Bottom)} to {Height(s.Top)})"
            : "Showing all storeys");
    }

    /// <summary>Works the storeys out on a copy of the brushes, off the main thread; the newest run wins.</summary>
    private void Detect()
    {
        _settle.Stop();
        MapProject p = _ctx.Project;
        var input = new MapProject
        {
            Brushes = p.Brushes.Select(b => b.Clone()).ToList(),
            Baked = p.Baked,
            Workspace = p.Workspace.Clone(),
        };
        string? folder = _ctx.Session.Folder;
        int generation = ++_generation;
        BackgroundJobs.Start(_ =>
        {
            IReadOnlyList<Storey> storeys;
            try
            {
                storeys = Storeys.Of(input, folder);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                BackgroundJobs.Post(() => _ctx.Log($"Could not find the storeys: {ex.Message}", Severity.Warning));
                return;
            }

            BackgroundJobs.Post(() =>
            {
                if (generation == _generation)
                {
                    TakeList(storeys);
                    FollowWorkspace();
                }
            });
        });
    }

    /// <summary>Gives the filter a new list of storeys, when it differs from the one it has.</summary>
    private void TakeList(IReadOnlyList<Storey> storeys)
    {
        if (!Same(storeys, StoreyFilter.List))
        {
            _following = true;
            StoreyFilter.SetList(storeys);
            _following = false;
        }
    }

    /// <summary>One checkable entry per storey under View > Storeys.</summary>
    private void RefreshPicks()
    {
        foreach (string id in _ctx.Actions.Select(a => a.Id).Where(id => id.StartsWith(PickPrefix, StringComparison.Ordinal)).ToList())
        {
            _ctx.RemoveAction(id);
        }

        IReadOnlyList<Storey> list = StoreyFilter.List;
        for (int i = 0; i < list.Count; i++)
        {
            int index = i;
            Storey s = list[i];
            string n = (i + 1).ToString(CultureInfo.InvariantCulture);
            _ctx.AddAction(new EditorAction(PickPrefix + n, $"{s.Name} ({Height(s.Bottom)}–{Height(s.Top)})", "View/Storeys",
                () => StoreyFilter.Show(index))
            {
                Order = 200 + i,
                RadioGroup = "view.storey",
                IsChecked = () => StoreyFilter.Active == index,
            });
        }
    }

    private static bool Same(IReadOnlyList<Storey> a, IReadOnlyList<Storey> b) => a.SequenceEqual(b);

    private static string Height(double z) =>
        double.IsFinite(z) ? z.ToString("0.#", CultureInfo.InvariantCulture).Replace('-', '−') : "open";
}
