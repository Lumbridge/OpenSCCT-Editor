using OpenScct.Editor.Core.Geometry;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Viewports;

/// <summary>
/// The storey filter (RE+ Map Design's storey slider, which its Storeys palette also applied to the stock views). With
/// a storey picked, every view draws and picks only the objects that reach into that storey's height range. The Top
/// view also cuts away what is above and below it, and every view cuts the world (an imported map's baked world, the
/// CSG preview and the Lit Preview) to the storey as it draws it (<see cref="Render.StoreyClip"/>), so it shows one
/// floor at a time while the CSG and the lighting still come from the whole map.
/// The storeys are the map's own (workspace storeys) or the ones found from its floors.
/// </summary>
public static class StoreyFilter
{
    private static readonly HashSet<EditorMeta> s_hidden = new(ReferenceEqualityComparer.Instance);
    private static MapProject? s_hiddenFor;
    private static bool s_stale = true;

    /// <summary>The map's storeys, lowest first.</summary>
    public static IReadOnlyList<Storey> List { get; private set; } = [];

    /// <summary>The storey shown (index into <see cref="List"/>), or -1 when every storey shows.</summary>
    public static int Active { get; private set; } = -1;

    public static Storey? Current => Active >= 0 && Active < List.Count ? List[Active] : null;

    /// <summary>Raised when the storey shown or the list of storeys changes.</summary>
    public static event Action? Changed;

    /// <summary>Takes a new list of storeys. The view stays at the height it showed: an edit that adds or removes a
    /// floor renumbers the storeys, so the one shown is kept by its height, not its name.</summary>
    public static void SetList(IReadOnlyList<Storey> storeys)
    {
        Storey? shown = Current;
        List = storeys;
        Active = shown is null ? -1 : storeys.ToList().FindIndex(s => s.Contains(shown.Level));
        ObjectsChanged();
        Changed?.Invoke();
    }

    /// <summary>Shows one storey (-1 shows them all).</summary>
    public static void Show(int index)
    {
        int next = index >= 0 && index < List.Count ? index : -1;
        if (next == Active)
        {
            return;
        }

        Active = next;
        ObjectsChanged();
        Changed?.Invoke();
    }

    /// <summary>Steps up (+1) or down (-1) a storey. Past the top or the bottom every storey shows again, and the next
    /// step goes on from the other end.</summary>
    public static void Step(int delta)
    {
        if (List.Count == 0)
        {
            return;
        }

        // Positions 0..n-1 are the storeys and n is "all", so stepping wraps through "all".
        int n = List.Count + 1;
        int at = Active < 0 ? List.Count : Active;
        int next = ((at + delta) % n + n) % n;
        Show(next == List.Count ? -1 : next);
    }

    /// <summary>Call when objects were added, changed or removed: which ones the storey hides is worked out again.</summary>
    public static void ObjectsChanged() => s_stale = true;

    /// <summary>True when the storey filter hides this object.</summary>
    public static bool Hides(MapProject p, EditorMeta meta)
    {
        if (Current is null)
        {
            return false;
        }

        if (s_stale || !ReferenceEquals(s_hiddenFor, p))
        {
            Collect(p, Current);
        }

        return s_hidden.Contains(meta);
    }

    /// <summary>The name of the storey shown, or "All storeys".</summary>
    public static string Describe() => Current is { } s ? s.Name : "All storeys";

    private static void Collect(MapProject p, Storey storey)
    {
        s_hidden.Clear();
        s_hiddenFor = p;
        s_stale = false;
        foreach (Brush b in p.Brushes)
        {
            Hide(b.Editor, ObjectGeometry.BrushBounds(b));
        }

        foreach (Actor a in p.Actors)
        {
            Hide(a.Editor, ObjectGeometry.ActorBounds(a));
        }

        foreach (Ge g in p.Ges.Where(g => g.Points.Count > 0))
        {
            Hide(g.Editor, ObjectGeometry.GeBounds(g));
        }

        void Hide(EditorMeta meta, (Vec3 Min, Vec3 Max) box)
        {
            if (!storey.Overlaps(box.Min.Z, box.Max.Z))
            {
                s_hidden.Add(meta);
            }
        }
    }
}
