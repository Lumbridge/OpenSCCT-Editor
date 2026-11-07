using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Scripting.Live;

/// <summary>An object a change document touched: its id after the document (<see cref="Id"/>) and before it
/// (<see cref="BeforeId"/>, differs after a rename), whether it existed before and whether it exists after.</summary>
public readonly record struct ChangedObject(string Id, string BeforeId, ObjectKind Kind, bool Existed, bool Exists)
{
    public bool IsCreated => !Existed && Exists;

    public bool IsDeleted => Existed && !Exists;

    public bool IsUpdated => Existed && Exists;
}

/// <summary>
/// A dry run of a change document with copies of what it would make: the objects it creates or edits as they would
/// be afterwards, and the ids of those it deletes. The editor draws these as a translucent "ghost" with Accept and
/// Reject; nothing in the map changes until it is accepted.
/// </summary>
public sealed class ChangePreview
{
    private ChangePreview(SvJsonNode document, ChangeResult result)
    {
        Document = document;
        Result = result;
    }

    /// <summary>The change document as given (accepting applies it again, against the map as it is then).</summary>
    public SvJsonNode Document { get; }

    /// <summary>The dry run's result: the diff, or the error.</summary>
    public ChangeResult Result { get; }

    public bool Ok => Result.Ok;

    /// <summary>The document's label, or a generic name.</summary>
    public string Label => Document is SvJsonObject o && ApiJson.GetString(o, "label") is { Length: > 0 } l ? l : "Script changes";

    public List<Brush> Brushes { get; } = new();

    public List<Actor> Actors { get; } = new();

    public List<Ge> Ges { get; } = new();

    /// <summary>Every touched object, in the order the document touched them.</summary>
    public List<ChangedObject> Changes { get; } = new();

    public IEnumerable<ObjectRef> Created => Changes.Where(c => c.IsCreated).Select(c => new ObjectRef(c.Kind, c.Id));

    public IEnumerable<ObjectRef> Updated => Changes.Where(c => c.IsUpdated).Select(c => new ObjectRef(c.Kind, c.Id));

    /// <summary>Deleted objects by the ids they have in the map now.</summary>
    public IEnumerable<ObjectRef> Deleted => Changes.Where(c => c.IsDeleted).Select(c => new ObjectRef(c.Kind, c.BeforeId));

    /// <summary>A short line for the log and the accept bar: "3 created, 1 updated, 2 deleted".</summary>
    public string Counts => CountsText(Changes.Count(c => c.IsCreated), Changes.Count(c => c.IsUpdated), Changes.Count(c => c.IsDeleted));

    /// <summary>"3 created, 1 updated, 2 deleted", leaving out the zeros ("no object changes" when all are).</summary>
    public static string CountsText(int created, int updated, int deleted)
    {
        var parts = new List<string>(3);
        if (created > 0)
        {
            parts.Add($"{created} created");
        }

        if (updated > 0)
        {
            parts.Add($"{updated} updated");
        }

        if (deleted > 0)
        {
            parts.Add($"{deleted} deleted");
        }

        return parts.Count == 0 ? "no object changes" : string.Join(", ", parts);
    }

    /// <summary>Dry-runs <paramref name="document"/> on the session and copies the objects it would create or edit.</summary>
    public static ChangePreview Run(EditorSession session, SvJsonNode document, string? author = null)
    {
        var brushes = new List<Brush>();
        var actors = new List<Actor>();
        var ges = new List<Ge>();
        var changes = new List<ChangedObject>();
        ChangeResult result = ChangeApplier.Apply(session, document, dryRun: true, author: author, inspect: (project, touched) =>
        {
            foreach (ChangedObject c in touched)
            {
                if (!c.Existed && !c.Exists)
                {
                    continue; // made and removed again in the same document
                }

                changes.Add(c);
                if (!c.Exists)
                {
                    continue;
                }

                switch (c.Kind)
                {
                    case ObjectKind.Brush or ObjectKind.Stair when project.FindBrush(c.Id) is { } b:
                        brushes.Add(b.Clone());
                        break;
                    case ObjectKind.Actor when project.FindActor(c.Id) is { } a:
                        actors.Add(a.Clone());
                        break;
                    case ObjectKind.Ge when project.FindGe(c.Id) is { } g:
                        ges.Add(g.Clone());
                        break;
                }
            }
        });
        var preview = new ChangePreview(document, result);
        preview.Brushes.AddRange(brushes);
        preview.Actors.AddRange(actors);
        preview.Ges.AddRange(ges);
        preview.Changes.AddRange(changes);
        return preview;
    }
}
