using OpenScct.Content.Json;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Scripting.Live;

public enum ProposalStatus
{
    Pending,
    Accepted,
    Rejected,
    Superseded,
    Failed,
}

/// <summary>A change document shown as a ghost, waiting for the user to accept or reject it.</summary>
public sealed class Proposal
{
    private readonly TaskCompletionSource<ProposalStatus> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Proposal(int id, ChangePreview preview, string? author)
    {
        Id = id;
        Preview = preview;
        Author = author;
    }

    public int Id { get; }

    /// <summary>The dry run as of the map now (redone after every edit while pending).</summary>
    public ChangePreview Preview { get; internal set; }

    /// <summary>Why the document no longer applies to the map as it is now (an edit since it was proposed), or null.
    /// The ghost keeps showing the last preview that applied; Accept stays off until it applies again.</summary>
    public string? Problem { get; internal set; }

    /// <summary>The author stamped on accept when the document names none.</summary>
    public string? Author { get; }

    public ProposalStatus Status { get; private set; } = ProposalStatus.Pending;

    /// <summary>The result of applying it (accepted), or the error (failed).</summary>
    public SvJsonObject? Outcome { get; private set; }

    /// <summary>Completes when the proposal is no longer pending.</summary>
    public Task<ProposalStatus> Done => _done.Task;

    internal void Finish(ProposalStatus status, SvJsonObject? outcome = null)
    {
        if (Status != ProposalStatus.Pending)
        {
            return;
        }

        Status = status;
        Outcome = outcome;
        _done.TrySetResult(status);
    }

    /// <summary>The status as scripts read it.</summary>
    public SvJsonObject ToJson()
    {
        SvJsonObject o = ApiJson.Obj(
            ("proposal", SvJsonNode.Integer(Id)),
            ("status", ApiJson.Str(Status.ToString().ToLowerInvariant())),
            ("label", ApiJson.Str(Preview.Label)));
        if (Problem is not null && Status == ProposalStatus.Pending)
        {
            o.Add("problem", ApiJson.Str(Problem));
        }

        if (Outcome is not null)
        {
            o.Add(Status == ProposalStatus.Accepted ? "applied" : "error", Outcome);
        }

        return o;
    }
}

/// <summary>
/// The editor's proposals: at most one pending at a time (a new one supersedes it) and a short history so scripts can
/// ask how theirs went. Not thread-safe: the editor uses it on its main thread only.
/// </summary>
public sealed class ProposalBook
{
    private const int Keep = 32;
    private readonly List<Proposal> _history = new();
    private int _nextId;

    /// <summary>The pending proposal, or null.</summary>
    public Proposal? Pending { get; private set; }

    /// <summary>Raised when the pending proposal changes (opened, accepted, rejected, superseded).</summary>
    public event Action? Changed;

    /// <summary>Raised after <see cref="Refresh"/> redid the pending proposal's dry run.</summary>
    public event Action<Proposal>? Refreshed;

    public Proposal? Find(int? id) => id is { } i ? _history.FirstOrDefault(p => p.Id == i) : _history.LastOrDefault();

    /// <summary>Dry-runs the document and, when it is valid, makes it the pending proposal.</summary>
    public Proposal Open(EditorSession session, SvJsonNode document, string? author = null)
    {
        ChangePreview preview = ChangePreview.Run(session, document, author);
        var proposal = new Proposal(++_nextId, preview, author);
        _history.Add(proposal);
        if (_history.Count > Keep)
        {
            _history.RemoveAt(0);
        }

        if (!preview.Ok)
        {
            proposal.Finish(ProposalStatus.Failed, preview.Result.Json["error"] as SvJsonObject);
            return proposal;
        }

        Pending?.Finish(ProposalStatus.Superseded);
        Pending = proposal;
        Changed?.Invoke();
        return proposal;
    }

    /// <summary>Dry-runs the pending proposal again against the map as it is now (call after edits), so the ghost
    /// follows the map. When it no longer applies, the old preview stays and <see cref="Proposal.Problem"/> says why.</summary>
    public void Refresh(EditorSession session)
    {
        if (Pending is not { } p)
        {
            return;
        }

        ChangePreview preview = ChangePreview.Run(session, p.Preview.Document, p.Author);
        if (preview.Ok)
        {
            p.Preview = preview;
            p.Problem = null;
        }
        else
        {
            p.Problem = ErrorText(preview.Result.Json["error"] as SvJsonObject);
        }

        Refreshed?.Invoke(p);
    }

    /// <summary>Applies the pending proposal to the map as it is now, as one undo step.</summary>
    public ChangeResult? Accept(EditorSession session)
    {
        if (Pending is not { } p)
        {
            return null;
        }

        ChangeResult result = ChangeApplier.Apply(session, p.Preview.Document, dryRun: false, author: p.Author);
        Pending = null;
        p.Finish(result.Ok ? ProposalStatus.Accepted : ProposalStatus.Failed, result.Ok ? result.Json : result.Json["error"] as SvJsonObject);
        Changed?.Invoke();
        return result;
    }

    public void Reject()
    {
        if (Pending is not { } p)
        {
            return;
        }

        Pending = null;
        p.Finish(ProposalStatus.Rejected);
        Changed?.Invoke();
    }

    /// <summary>Drops the pending proposal without an answer (another map was opened).</summary>
    public void Withdraw()
    {
        if (Pending is not { } p)
        {
            return;
        }

        Pending = null;
        p.Finish(ProposalStatus.Superseded);
        Changed?.Invoke();
    }

    /// <summary>A change document error as one line: "op 2 field at: message".</summary>
    public static string ErrorText(SvJsonObject? error)
    {
        if (error is null)
        {
            return "unknown error";
        }

        string message = ApiJson.GetString(error, "message") ?? ApiJson.Write(error);
        string where = error["op"] is { } op ? $"op {ApiJson.Write(op)}" : "";
        string field = ApiJson.GetString(error, "field") is { } f ? $" field {f}" : "";
        return where.Length + field.Length > 0 ? $"{where.Trim()}{field}: {message}".Trim() : message;
    }
}
