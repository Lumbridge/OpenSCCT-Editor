using OpenScct.Content;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Commands;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Gameplay;

/// <summary>
/// Undoable wiring edits: link a sender to a receiver (Event to Tag, or an alarm's lists of movers and objectives to
/// lock), unlink, and rename a Tag together with every event that names it. Each is one undo step.
/// </summary>
public static class Wiring
{
    /// <summary>What a link from one actor to another does, in words (for previews and the status line).</summary>
    public static string Describe(MapProject p, string fromId, string toId)
    {
        if (p.FindActor(fromId) is not { } from || p.FindActor(toId) is not { } to)
        {
            return "nothing to link";
        }

        (string tag, bool isNew) = TagFor(p, to);
        int others = isNew ? 0 : p.Actors.Count(a => !ReferenceEquals(a, to) && Same(a.Tag, tag) && GameplayGraph.IsGameplay(a));
        string reach = others == 0 ? to.Id : $"{to.Id} and {others} more with that tag";
        return LinkTarget(from, to) switch
        {
            "MoversToLock" => $"{from.Id} locks mover {to.Id} when it goes off",
            "ObjectiveTriggersToLock" => $"{from.Id} locks objective {to.Id} when it goes off",
            "Events" => $"{from.Id} fires {tag} ({reach}) when it goes off",
            _ => $"{from.Id}.Event = {tag} ({reach})",
        };
    }

    /// <summary>What linking one sender to several receivers does, in words: each lock or alarm event, and for
    /// several Event receivers the shared Tag and the receivers whose Tag changes to it.</summary>
    public static string Describe(MapProject p, string fromId, IReadOnlyList<string> toIds)
    {
        if (toIds.Count == 1 || p.FindActor(fromId) is not { } from)
        {
            return toIds.Count == 0 ? "nothing to link" : Describe(p, fromId, toIds[0]);
        }

        List<Actor> receivers = toIds.Select(p.FindActor).OfType<Actor>().Where(a => !ReferenceEquals(a, from)).ToList();
        List<Actor> events = receivers.Where(a => LinkTarget(from, a) == "Event").ToList();
        var parts = receivers.Except(events).Select(a => Describe(p, from.Id, a.Id)).ToList();
        if (events.Count == 1)
        {
            parts.Add(Describe(p, from.Id, events[0].Id));
        }
        else if (events.Count > 1)
        {
            string shared = StoredTag(events[0]) ?? events[0].Id;
            List<string> retagged = events.Where(a => !Same(a.Tag, shared)).Select(a => $"{a.Id} (was {a.Tag})").ToList();
            parts.Add($"{from.Id}.Event = {shared}" + (retagged.Count > 0 ? $"; Tag {shared} given to {string.Join(", ", retagged)}" : ""));
        }

        return string.Join("; ", parts);
    }

    /// <summary>Links <paramref name="fromId"/> to one receiver (see the list form).</summary>
    public static ReplaceObjectsCommand? Link(MapProject p, string fromId, string toId) => Link(p, fromId, [toId]);

    /// <summary>
    /// Links <paramref name="fromId"/> to every receiver, one undo step: an alarm adds a mover to MoversToLock, an
    /// objective trigger to ObjectiveTriggersToLock and anything else to its Events; any other sender gets Event = the
    /// receivers' Tag. A receiver whose Tag is only its class name, shared with other actors, first gets its own Tag
    /// (its id) so the event reaches it alone. Several receivers of one Event share the first one's Tag (its stored
    /// Tag, else its id), as the scripting API's link op does. Null when nothing links (unknown ids, or only the sender).
    /// </summary>
    public static ReplaceObjectsCommand? Link(MapProject p, string fromId, IReadOnlyList<string> toIds)
    {
        if (p.FindActor(fromId) is not { } from)
        {
            return null;
        }

        List<Actor> receivers = toIds.Select(p.FindActor).OfType<Actor>().Where(a => !ReferenceEquals(a, from))
            .DistinctBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();
        if (receivers.Count == 0)
        {
            return null;
        }

        var cmd = new ReplaceObjectsCommand(receivers.Count == 1 ? $"Link {from.Id} to {receivers[0].Id}" : $"Link {from.Id} to {receivers.Count} actors");
        Actor after = from.Clone();
        var eventReceivers = new List<Actor>();
        foreach (Actor to in receivers)
        {
            switch (LinkTarget(from, to))
            {
                case "MoversToLock":
                    var entry = new SvJsonObject();
                    entry.Add("ActionBeforeLock", SvJsonNode.Integer(0));
                    entry.Add("MoverToLock", PropValues.ActorRef(to.Id));
                    Append(after, "MoversToLock", entry, n => n is SvJsonObject o && Same(PropValues.ActorId(o["MoverToLock"]), to.Id));
                    break;
                case "ObjectiveTriggersToLock":
                    Append(after, "ObjectiveTriggersToLock", PropValues.ActorRef(to.Id), n => Same(PropValues.ActorId(n), to.Id));
                    break;
                case "Events":
                    string tag = Retag(cmd, to, TagFor(p, to));
                    Append(after, "Events", SvJsonNode.String(tag), n => n is SvJsonString s && Same(s.Value, tag));
                    break;
                default:
                    eventReceivers.Add(to);
                    break;
            }
        }

        if (eventReceivers.Count == 1)
        {
            after.Props.Set("Event", SvJsonNode.String(Retag(cmd, eventReceivers[0], TagFor(p, eventReceivers[0]))));
        }
        else if (eventReceivers.Count > 1)
        {
            string shared = StoredTag(eventReceivers[0]) ?? eventReceivers[0].Id;
            foreach (Actor to in eventReceivers)
            {
                Retag(cmd, to, (shared, !Same(to.Tag, shared)));
            }

            after.Props.Set("Event", SvJsonNode.String(shared));
        }

        cmd.Actor(from.Clone(), after);
        return cmd;
    }

    /// <summary>Clears an actor's Event (and an alarm's Events).</summary>
    public static ReplaceObjectsCommand Unlink(MapProject p, IEnumerable<string> ids) =>
        ReplaceObjectsCommand.Edit(p, ids.Select(ObjectRef.Actor), "Unlink", actor: a =>
        {
            a.Props.Remove("Event");
            if (a.ShortClass == "SAlarm")
            {
                a.Props.Remove("Events");
            }
        });

    /// <summary>
    /// Removes every link from <paramref name="fromId"/> to <paramref name="toId"/>: top-level event properties naming
    /// the receiver's Tag (and those names in event arrays), and references to it (an alarm's movers and objectives to
    /// lock included). Other receivers of the same Tag stop hearing those events too. Null when nothing links them.
    /// </summary>
    public static ReplaceObjectsCommand? Unlink(MapProject p, string fromId, string toId)
    {
        if (p.FindActor(fromId) is not { } from || p.FindActor(toId) is not { } to)
        {
            return null;
        }

        Actor after = from.Clone();
        bool changed = false;
        foreach (KeyValuePair<string, SvJsonNode> kv in from.Props.ToList())
        {
            SvJsonNode? kept = Without(kv.Value, GameplayDoc.IsEventKey(kv.Key), to);
            if (!ReferenceEquals(kept, kv.Value))
            {
                after.Props[kv.Key] = kept;
                changed = true;
            }
        }

        return changed ? new ReplaceObjectsCommand($"Unlink {from.Id} from {to.Id}").Actor(from.Clone(), after) : null;
    }

    /// <summary>Renames a Tag on every actor that carries it (stored, or its class-name default) and in every event
    /// property that names it.</summary>
    public static ReplaceObjectsCommand RenameTag(MapProject p, string oldTag, string newTag)
    {
        var cmd = new ReplaceObjectsCommand($"Rename tag {oldTag} to {newTag}");
        foreach (Actor a in p.Actors)
        {
            Actor after = a.Clone();
            bool changed = false;
            if (Same(a.Tag, oldTag))
            {
                after.Props.Set("Tag", SvJsonNode.String(newTag));
                changed = true;
            }

            foreach (KeyValuePair<string, SvJsonNode> kv in a.Props.ToList())
            {
                if (Rename(kv.Value, kv.Key, oldTag, newTag) is { } renamed)
                {
                    after.Props.Set(kv.Key, renamed);
                    changed = true;
                }
            }

            if (changed)
            {
                cmd.Actor(a.Clone(), after);
            }
        }

        return cmd;
    }

    /// <summary>The Tag an event must name to reach <paramref name="to"/> alone, and whether it must be set first.</summary>
    public static (string Tag, bool IsNew) TagFor(MapProject p, Actor to)
    {
        if (StoredTag(to) is { } t)
        {
            return (t, false);
        }

        bool shared = p.Actors.Any(a => !ReferenceEquals(a, to) && Same(a.Tag, to.Tag) && GameplayGraph.IsGameplay(a));
        return shared ? (to.Id, true) : (to.Tag, false);
    }

    private static string? StoredTag(Actor a) =>
        a.Props.GetString("Tag") is { Length: > 0 } t && !t.Equals("None", StringComparison.OrdinalIgnoreCase) ? t : null;

    /// <summary>Gives <paramref name="to"/> the Tag when it is new (as part of <paramref name="cmd"/>); returns the Tag.</summary>
    private static string Retag(ReplaceObjectsCommand cmd, Actor to, (string Tag, bool IsNew) tag)
    {
        if (tag.IsNew)
        {
            Actor tagged = to.Clone();
            tagged.Props.Set("Tag", SvJsonNode.String(tag.Tag));
            cmd.Actor(to.Clone(), tagged);
        }

        return tag.Tag;
    }

    /// <summary>The value with links to <paramref name="to"/> taken out (the same instance when there were none, null
    /// when nothing is left of it): its Tag in an event value, references to it, and lock entries naming it.</summary>
    private static SvJsonNode? Without(SvJsonNode value, bool eventKey, Actor to)
    {
        switch (value)
        {
            case SvJsonString s when eventKey && Same(s.Value, to.Tag):
                return null;
            case SvJsonObject o when Same(PropValues.ActorId(o), to.Id) || Same(PropValues.ActorId(o["MoverToLock"]), to.Id):
                return null;
            case SvJsonArray arr:
                var items = arr.Where(n => Without(n, eventKey, to) is not null).ToList();
                return items.Count == arr.Count ? arr : items.Count == 0 ? null : new SvJsonArray(items);
            default:
                return value;
        }
    }

    private static string LinkTarget(Actor from, Actor to)
    {
        if (from.ShortClass != "SAlarm")
        {
            return "Event";
        }

        if (ClassDefaults.Catalog.IsA(to.Class, "Engine.Mover") || to.ShortClass == "Mover")
        {
            return "MoversToLock";
        }

        return ClassDefaults.Catalog.IsA(to.Class, "SBase.SObjectiveTrigger") ? "ObjectiveTriggersToLock" : "Events";
    }

    /// <summary>Adds an item to an array property unless one matching <paramref name="exists"/> is there. Empty
    /// struct entries (no mover set) are reused first.</summary>
    private static void Append(Actor a, string key, SvJsonNode item, Func<SvJsonNode, bool> exists)
    {
        var list = a.Props[key] is SvJsonArray arr ? arr.ToList() : [];
        if (list.Any(exists))
        {
            return;
        }

        int empty = list.FindIndex(n => n is SvJsonObject o && o["MoverToLock"] is null or SvJsonLiteral { Kind: SvJsonKind.Null });
        if (key == "MoversToLock" && empty >= 0)
        {
            list[empty] = item;
        }
        else
        {
            list.Add(item);
        }

        a.Props.Set(key, new SvJsonArray(list));
    }

    /// <summary>The value with event names equal to <paramref name="oldTag"/> renamed, or null when none were.</summary>
    private static SvJsonNode? Rename(SvJsonNode value, string key, string oldTag, string newTag)
    {
        switch (value)
        {
            case SvJsonString s when GameplayDoc.IsEventKey(key) && Same(s.Value, oldTag):
                return SvJsonNode.String(newTag);
            case SvJsonArray arr:
                var items = arr.ToList();
                bool any = false;
                for (int i = 0; i < items.Count; i++)
                {
                    if (Rename(items[i], key, oldTag, newTag) is { } r)
                    {
                        items[i] = r;
                        any = true;
                    }
                }

                return any ? new SvJsonArray(items) : null;
            case SvJsonObject o when PropValues.ActorId(o) is null:
                var copy = new SvJsonObject();
                bool changed = false;
                foreach (KeyValuePair<string, SvJsonNode> m in o)
                {
                    SvJsonNode? r = Rename(m.Value, m.Key, oldTag, newTag);
                    copy.Add(m.Key, r ?? m.Value);
                    changed |= r is not null;
                }

                return changed ? copy : null;
            default:
                return null;
        }
    }

    private static bool Same(string? a, string? b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
