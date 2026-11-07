using System.Globalization;
using Godot;
using OpenScct.Editor.Core.Services;
using OpenScct.Editor.Core.Updates;
using OpenScctEditor.Core;
using OpenScctEditor.Panels;
using OpenScctEditor.Shell;

namespace OpenScctEditor.Assets;

/// <summary>A catalogue of downloadable map asset libraries and shared packs.</summary>
public sealed partial class ContentPacksWindow : AcceptDialog
{
    private const int TextWidth = 600;
    private readonly ContentPacks _service = new();
    private readonly LineEdit _search;
    private readonly OptionButton _categories;
    private readonly OptionButton _states;
    private readonly Tree _tree;
    private readonly Label _status;
    private readonly Label _count;
    private readonly ProgressBar _progress;
    private readonly Button _refresh;
    private readonly Button _download;
    private readonly Button _cancel;
    private IReadOnlyList<ContentPackEntry>? _catalog;
    private CancellationTokenSource? _operation;
    private bool _installing;

    private static EditorContext Context => EditorContext.Instance;

    public ContentPacksWindow()
    {
        Name = "ContentPacks";
        Title = "OpenSCCT Content Packs";
        OkButtonText = "Close";
        MinSize = new Vector2I(650, 420);
        Unresizable = false;
        Exclusive = false;
        Visible = false;

        var body = new VBoxContainer { CustomMinimumSize = new Vector2(TextWidth, 0) };
        body.AddThemeConstantOverride("separation", EditorTheme.SpaceS);
        AddChild(body);
        PanelUi.Caption(body, "Download map asset libraries and shared game data for use in the editor.", wrap: false);
        PanelUi.Caption(body, "Map downloads can include required shared packs.", wrap: false);
        PanelUi.Caption(body, $"Install folder  {_service.Root}", wrap: false);
        HFlowContainer filters = PanelUi.Toolbar(body);
        _search = PanelUi.Search(filters, "Search packs", _ => Fill(), "Search pack names, map codes and categories");
        _search.Name = "PackSearch";
        _categories = PanelUi.Options(filters, "Filter by pack category");
        _categories.CustomMinimumSize = new Vector2(142, 0);
        _categories.AddItem("All Categories");
        _categories.ItemSelected += _ => Fill();
        _states = PanelUi.Options(filters, "Filter by installation status");
        _states.CustomMinimumSize = new Vector2(130, 0);
        _states.AddItem("All Statuses");
        foreach (ContentPackState state in Enum.GetValues<ContentPackState>()) _states.AddItem(StateName(state), (int)state + 1);
        _states.ItemSelected += _ => Fill();
        _refresh = PanelUi.Button(filters, "Refresh", "Read the latest OpenSCCT-Content catalogue", () => Refresh());
        _count = PanelUi.Caption(body, "", wrap: false);

        _tree = new Tree
        {
            Name = "Packs",
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 215),
        };
        PanelUi.Columns(_tree, ("Name", 190, true), ("Code", 72, false), ("Category", 84, false),
            ("Download", 78, false), ("Status", 120, false));
        _tree.ItemSelected += UpdateButtons;
        _tree.ItemActivated += Download;
        body.AddChild(_tree);
        _progress = new ProgressBar { Name = "PackProgress", MaxValue = 100, ShowPercentage = true, Visible = false };
        body.AddChild(_progress);
        _status = PanelUi.Caption(body, "");
        // A wrapped label needs a width before the dialog sizes itself for the first time.
        _status.CustomMinimumSize = new Vector2(TextWidth, 0);
        _download = AddButton("Download", right: false, action: "download");
        _download.ThemeTypeVariation = "PrimaryButton";
        _cancel = AddButton("Cancel Download", right: false, action: "cancel");
        _cancel.TooltipText = "Stop the download or remaining dependencies. A pack already being installed may finish.";
        Dialogs.ArrangeButtons(this, _download, _cancel);
        CustomAction += action =>
        {
            if (action == "download") Download();
            else if (action == "cancel") Cancel();
        };
        UpdateButtons();
    }

    public void Open(string? mapCode)
    {
        if (mapCode is { Length: > 0 })
        {
            _search.Text = mapCode;
            _categories.Select(0);
            _states.Select(0);
            Fill();
        }

        if (!Visible) PopupCenteredClamped(new Vector2I(760, 585), 0.9f);
        else GrabFocus();
        _search.GrabFocus();
        _search.Edit();
        _search.SelectAll();
        if (_catalog is null && _operation is null) Refresh();
    }

    private void Refresh(string? notice = null)
    {
        if (_operation is not null) return;
        _status.Text = notice is null ? "Reading the catalogue…" : $"{notice} Refreshing the catalogue…";
        Run(_service.ListAsync, entries =>
        {
            string? category = PickedCategory();
            _catalog = entries;
            _categories.Clear();
            _categories.AddItem("All Categories");
            foreach (string value in entries.Select(e => e.Category).Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(c => c == "shared" ? "" : c, StringComparer.OrdinalIgnoreCase))
            {
                _categories.AddItem(CategoryName(value));
                int index = _categories.ItemCount - 1;
                _categories.SetItemMetadata(index, value);
                if (string.Equals(value, category, StringComparison.OrdinalIgnoreCase)) _categories.Select(index);
            }

            Fill();
            _status.Text = notice ?? "Choose a pack to see whether it can be downloaded or updated.";
        }, error => Report(error, reading: true, notice));
    }

    private void Fill()
    {
        string? selected = PickedId();
        string? category = PickedCategory();
        ContentPackState? state = _states.Selected > 0 ? (ContentPackState)(_states.GetSelectedId() - 1) : null;
        string[] words = _search.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matches = (_catalog ?? []).Where(pack =>
            (category is null || string.Equals(category, pack.Category, StringComparison.OrdinalIgnoreCase)) &&
            (state is null || state == pack.State) && Matches(pack, words));
        _tree.Clear();
        TreeItem root = _tree.CreateItem();
        TreeItem? first = null;
        TreeItem? retained = null;
        int count = 0;
        foreach (ContentPackEntry pack in matches.OrderBy(p => SearchRank(p, _search.Text.Trim())).ThenBy(p => p.Category != "shared")
                     .ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Code, StringComparer.OrdinalIgnoreCase))
        {
            TreeItem row = _tree.CreateItem(root);
            first ??= row;
            row.SetMetadata(0, pack.Id);
            row.SetText(0, pack.Title);
            row.SetIcon(0, Icons.Get(pack.Category == "shared" ? "folder" : "map", EditorTheme.TextDim));
            row.SetText(1, pack.Code);
            row.SetText(2, CategoryName(pack.Category));
            row.SetText(3, UpdateChecker.Size(pack.Size));
            row.SetText(4, StateName(pack.State));
            row.SetTooltipText(0, $"{pack.Title} ({pack.Code})\nPack version {pack.Version}");
            row.SetTooltipText(4, StateDetail(pack.State));
            if (pack.State == ContentPackState.UpdateAvailable) row.SetCustomColor(4, EditorTheme.Accent);
            if (pack.Id == selected) retained = row;
            count++;
        }

        _count.Text = count == 0 ? (_catalog is null ? "" : "No packs match these filters.")
            : $"{count} {(count == 1 ? "pack" : "packs")}";
        if ((retained ?? first) is { } chosen)
        {
            chosen.Select(0);
            _tree.ScrollToItem(chosen);
        }

        UpdateButtons();
    }

    private string? PickedCategory() => _categories.Selected > 0 ? _categories.GetItemMetadata(_categories.Selected).AsString() : null;

    private string? PickedId() => _tree.GetSelected()?.GetMetadata(0).AsString();

    private ContentPackEntry? Selected => PickedId() is { } id ? _catalog?.FirstOrDefault(p => p.Id == id) : null;

    private static bool CanDownload(ContentPackEntry? pack) => pack?.State is ContentPackState.Available or ContentPackState.UpdateAvailable;

    private void UpdateButtons()
    {
        ContentPackEntry? selected = Selected;
        _refresh.Disabled = _operation is not null;
        _download.Disabled = _operation is not null || !CanDownload(selected);
        _download.Text = selected?.State == ContentPackState.UpdateAvailable ? "Update" : "Download";
        _cancel.Visible = _installing;
        _cancel.Disabled = _operation?.IsCancellationRequested != false;
        _progress.Visible = _installing;
    }

    private void Download()
    {
        if (_operation is not null || Selected is not { } pack || !CanDownload(pack)) return;
        _installing = true;
        _progress.Value = 0;
        _status.Text = $"Preparing {pack.Title}…";
        int revision = _service.Revision;
        long last = 0;
        Run(cancel => _service.InstallAsync(pack.Id, new PackProgress(update =>
        {
            long now = System.Environment.TickCount64;
            if (cancel.IsCancellationRequested || now - Interlocked.Read(ref last) < 160 && !update.Extracting && update.Received < update.Total) return;
            Interlocked.Exchange(ref last, now);
            BackgroundJobs.Post(() =>
            {
                if (!_installing || cancel.IsCancellationRequested) return;
                _progress.Indeterminate = update.Extracting || update.Total <= 0;
                if (!_progress.Indeterminate) _progress.Value = Math.Clamp((double)update.Received / update.Total * 100, 0, 100);
                _status.Text = update.Extracting ? $"Verifying and installing {update.Title}…" : $"Downloading {update.Title}…";
                Context.SetStatus(_status.Text);
            });
        }), cancel), result =>
        {
            if (result.Changed || _service.Revision != revision) AssetService.Instance.ContentPacksChanged(_service.Root);
            foreach (string warning in result.Warnings) Context.Log($"Content Packs: {warning}", Severity.Warning);
            string message = $"Installed {result.Pack.Title}.";
            if (result.Warnings.FirstOrDefault() is { } warningText) message += $" {warningText}";
            Context.SetStatus(message);
            Refresh(message);
        }, error => InstallFailed(pack, error, revision));
    }

    private void InstallFailed(ContentPackEntry pack, Exception error, int revision)
    {
        string action = pack.State == ContentPackState.UpdateAvailable ? "update" : "install";
        Report(error, reading: false, error is OperationCanceledException ? null : $"Could not {action} {pack.Title}.");
        if (_service.Revision != revision)
        {
            // An earlier dependency finished while this pack failed or was cancelled.
            AssetService.Instance.ContentPacksChanged(_service.Root);
            Refresh(_status.Text);
        }
    }

    private void Cancel()
    {
        _operation?.Cancel();
        _status.Text = "Cancelling download…";
        UpdateButtons();
    }

    private void Run<T>(Func<CancellationToken, Task<T>> work, Action<T> done, Action<Exception> failed)
    {
        var cancel = new CancellationTokenSource();
        _operation = cancel;
        UpdateButtons();
        if (!BackgroundJobs.Start(async quit =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(quit, cancel.Token);
            try
            {
                T result = await work(linked.Token).ConfigureAwait(false);
                BackgroundJobs.Post(() => Finish(cancel, () => done(result)));
            }
            catch (Exception ex)
            {
                Exception error = ex is OperationCanceledException && !linked.IsCancellationRequested
                    ? new TimeoutException("The content server did not respond in time.", ex) : ex;
                BackgroundJobs.Post(() => Finish(cancel, () => failed(error)));
            }
        }))
        {
            Finish(cancel, () => { });
        }
    }

    private void Finish(CancellationTokenSource cancel, Action next)
    {
        if (!ReferenceEquals(_operation, cancel)) return;
        _operation = null;
        _installing = false;
        cancel.Dispose();
        UpdateButtons();
        next();
    }

    private void Report(Exception error, bool reading, string? notice)
    {
        string reason = error switch
        {
            OperationCanceledException when !reading => "Download cancelled.",
            HttpRequestException or OperationCanceledException or TimeoutException or
                IOException { InnerException: HttpRequestException or OperationCanceledException } =>
                "Could not reach OpenSCCT-Content. Check your connection and try again.",
            UnauthorizedAccessException => "The content folder could not be written. Check its permissions and try again.",
            _ => error.Message,
        };
        _status.Text = notice is null ? reason : $"{notice} {reason}";
        Context.SetStatus(_status.Text);
        if (error is not OperationCanceledException) Context.Log($"Content Packs: {error.Message}", Severity.Warning);
    }

    private static bool Matches(ContentPackEntry pack, string[] words)
    {
        string searchable = $"{pack.Title} {pack.Code} {pack.Category}";
        return words.All(word => searchable.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static int SearchRank(ContentPackEntry pack, string text) =>
        text.Length == 0 || pack.Code.Equals(text, StringComparison.OrdinalIgnoreCase) || pack.Title.Equals(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static string CategoryName(string category) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(category);

    private static string StateName(ContentPackState state) => state switch
    {
        ContentPackState.UpdateAvailable => "Update Available",
        ContentPackState.Local => "Local Copy",
        _ => state.ToString(),
    };

    private static string StateDetail(ContentPackState state) => state switch
    {
        ContentPackState.UpdateAvailable => "A newer release of this installed pack is available.",
        ContentPackState.Local => "This content is already present locally.",
        ContentPackState.Installed => "This pack is installed and up to date.",
        _ => "This pack can be downloaded.",
    };

    private sealed class PackProgress(Action<ContentPackProgress> report) : IProgress<ContentPackProgress>
    {
        public void Report(ContentPackProgress value) => report(value);
    }
}
