namespace OpenScct.Editor.Core.Model;

/// <summary>
/// An id lookup over one of the project's object lists (ignoring case, first object wins). The lists are plain
/// <see cref="List{T}"/>s that commands, importers and tools edit directly, so the index never trusts itself: a hit is
/// checked against the live list, and a miss is only believed after a cheap reference compare shows the list is
/// unchanged since the index was built. Appends extend the index; any other change rebuilds it on the next miss.
/// Lookups lock, so threads that only read the lists may share it (as they could share a plain list search).
/// </summary>
internal sealed class IdIndex<T>
    where T : class
{
    private readonly Lock _lock = new();
    private readonly Func<T, string> _idOf;
    private readonly Dictionary<string, int> _map = new(StringComparer.OrdinalIgnoreCase);
    private T[] _items = [];
    private string[] _ids = [];
    private int _count;

    public IdIndex(Func<T, string> idOf) => _idOf = idOf;

    /// <summary>The index of the object with this id in <paramref name="list"/>, or -1.</summary>
    public int IndexOf(List<T> list, string id)
    {
        lock (_lock)
        {
            int hit = HitLocked(list, id);
            if (hit >= 0 || !Refresh(list))
            {
                return hit;
            }

            return _map.TryGetValue(id, out int i) ? i : -1;
        }
    }

    /// <summary>The index when the current index already finds it (no refresh), else -1.</summary>
    public int Hit(List<T> list, string id)
    {
        lock (_lock)
        {
            return HitLocked(list, id);
        }
    }

    private int HitLocked(List<T> list, string id) =>
        _map.TryGetValue(id, out int i) && i < list.Count && string.Equals(_idOf(list[i]), id, StringComparison.OrdinalIgnoreCase) ? i : -1;

    /// <summary>Brings the index up to date with the list; false when it already was.</summary>
    private bool Refresh(List<T> list)
    {
        int same = 0;
        int limit = Math.Min(_count, list.Count);
        while (same < limit && ReferenceEquals(list[same], _items[same]) && ReferenceEquals(_idOf(list[same]), _ids[same]))
        {
            same++;
        }

        if (same == _count && same == list.Count)
        {
            return false;
        }

        if (same < _count)
        {
            _map.Clear();
            same = 0;
        }

        if (_items.Length < list.Count)
        {
            Array.Resize(ref _items, Math.Max(list.Count, _items.Length * 2));
            Array.Resize(ref _ids, _items.Length);
        }

        for (int i = same; i < list.Count; i++)
        {
            T item = list[i];
            string id = _idOf(item);
            _items[i] = item;
            _ids[i] = id;
            _map.TryAdd(id, i);
        }

        Array.Clear(_items, list.Count, _count > list.Count ? _count - list.Count : 0);
        _count = list.Count;
        return true;
    }
}
