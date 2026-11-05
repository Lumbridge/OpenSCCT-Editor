using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Panels;

/// <summary>Objects by id (ignoring case, as the model does), built in one pass, so panels that read many selected
/// objects stay linear on big maps instead of searching the lists once per object.</summary>
internal sealed class ObjectIndex
{
    private readonly Dictionary<string, Brush> _brushes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Actor> _actors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Ge> _ges = new(StringComparer.OrdinalIgnoreCase);

    public ObjectIndex(MapProject p)
    {
        foreach (Brush b in p.Brushes)
        {
            _brushes.TryAdd(b.Id, b);
        }

        foreach (Actor a in p.Actors)
        {
            _actors.TryAdd(a.Id, a);
        }

        foreach (Ge g in p.Ges)
        {
            _ges.TryAdd(g.Id, g);
        }
    }

    public Brush? Brush(ObjectRef r) => r.Kind == ObjectKind.Brush ? _brushes.GetValueOrDefault(r.Id) : null;

    public Actor? Actor(ObjectRef r) => r.Kind == ObjectKind.Actor ? _actors.GetValueOrDefault(r.Id) : null;

    public Ge? Ge(ObjectRef r) => r.Kind == ObjectKind.Ge ? _ges.GetValueOrDefault(r.Id) : null;

    public bool Exists(ObjectRef r) => Brush(r) is not null || Actor(r) is not null || Ge(r) is not null;

    public EditorMeta? Meta(ObjectRef r) => Brush(r)?.Editor ?? Actor(r)?.Editor ?? Ge(r)?.Editor;

    /// <summary>The short class the lists show: the actor's class, "Brush" or "GE kind".</summary>
    public string? Class(ObjectRef r) =>
        Actor(r)?.ShortClass ?? (Brush(r) is not null ? ObjectInfo.BrushClass : Ge(r) is { } g ? ObjectInfo.GeClass(g.Kind) : null);
}
