using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Interop;

/// <summary>Group visibility is stored on LevelInfo as well as cached on each actor by UnrealEd.</summary>
internal static class EditorGroups
{
    public static PropSet LevelInfo(MapProject project)
    {
        var props = project.LevelInfo.Clone();
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        void Add(string? value)
        {
            foreach (string group in (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (groups.Add(group)) ordered.Add(group);
        }
        // Once VisibleGroups is populated, UnrealEd also filters actors without an explicit group.
        Add("None");
        Add(props.GetString("VisibleGroups"));
        foreach (PropSet item in project.Brushes.Select(b => b.Props).Concat(project.Actors.Select(a => a.Props)))
            Add(item.GetString("Group"));
        if (ordered.Count > 0) props.Set("VisibleGroups", SvJsonNode.String(string.Join(',', ordered)));
        return props;
    }

    public static bool IsCachedHiddenFlag(string key) => key.Equals("bHiddenEdGroup", StringComparison.OrdinalIgnoreCase);
}
