using System.Collections;

namespace ASync.App.Tests;

/// <summary>
/// What a fake recorded, safe to read while the code under test still adds to it: the shell calls the fakes from
/// thread-pool continuations (no UI thread in the view-model tests), while the test enumerates the list in
/// <c>Assert.Contains</c>/<c>Assert.Equal</c>. A plain <see cref="List{T}"/> there can throw "Collection was modified"
/// (or read a torn array) when an add lands mid-enumeration: a rare, load-dependent failure. Reads take a snapshot.
/// </summary>
internal sealed class CallLog<T> : IReadOnlyList<T>
{
    private readonly List<T> _items = [];
    private readonly Lock _lock = new();

    public void Add(T item)
    {
        lock (_lock) _items.Add(item);
    }

    public void Clear()
    {
        lock (_lock) _items.Clear();
    }

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    public T this[int index]
    {
        get { lock (_lock) return _items[index]; }
    }

    public bool Contains(T item)
    {
        lock (_lock) return _items.Contains(item);
    }

    private List<T> Snapshot()
    {
        lock (_lock) return [.. _items];
    }

    public IEnumerator<T> GetEnumerator() => Snapshot().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
