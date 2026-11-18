using System.Text.Json;
using ASync.Client.Core;
using ASync.Client.Core.Documents;

namespace ASync.App.iOS.FileProvider;

/// <summary>What changed in the containers the system knows since an anchor.</summary>
/// <param name="Expired">The anchor is of another epoch (or unreadable): the system must enumerate from scratch.</param>
public sealed record FileProviderChanges(IReadOnlyList<FileProviderItem> Updated, IReadOnlyList<string> Deleted, FileProviderAnchor Anchor, bool Expired = false);

/// <summary>A container's listing for the change log: its items, or gone (the directory or folder no longer exists).</summary>
public sealed record ContainerListing(IReadOnlyList<FileProviderItem>? Items)
{
    public static readonly ContainerListing Gone = new((IReadOnlyList<FileProviderItem>?)null);
}

/// <summary>
/// The containers the extension has handed out, with the version of each item it listed (fileprovider/known.json in the
/// group), so a change can be reported as exactly the items that were added, changed or removed. The server has no change
/// feed per directory, and the plain layout keeps no history: what the system was told is the only base to compare with.
/// <para>
/// <b>How changes reach the system:</b> the app raises <see cref="FileProviderSignals"/> (another device's
/// <c>folder.changed</c>, its own runs, the folder list) and calls <c>signalEnumerator</c> on the working set; the system then
/// asks the working set for the changes since its anchor; <see cref="ChangesAsync"/> lists again the known containers of the
/// folders that changed after that anchor (every container when the folder list or the account did), compares and
/// remembers. A failed listing fails the whole request (nothing is reported as deleted because the server could not be
/// reached), and the system asks again with the same anchor. Thread-safe; written after every change.
/// </para>
/// </summary>
public sealed class FileProviderChangeLog
{
    public const string FileName = "known.json";
    private readonly string _directory;
    private readonly Lock _gate = new();
    private Dictionary<string, Dictionary<string, string>> _known;

    public FileProviderChangeLog(string directory)
    {
        _directory = directory;
        _known = Load();
    }

    private string FilePath => Path.Combine(_directory, FileName);

    private Dictionary<string, Dictionary<string, string>> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllBytes(FilePath), AsyncApiClient.Json) ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { /* starts empty: the system re-enumerates what it browses */ }
        return [];
    }

    private void Save()
    {
        try { SharedFiles.WriteAtomically(_directory, FileName, JsonSerializer.SerializeToUtf8Bytes(_known, AsyncApiClient.Json)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* kept in memory */ }
    }

    /// <summary>The containers (item identifiers) the system has been given listings of.</summary>
    public IReadOnlyList<string> Containers
    {
        get { lock (_gate) return [.. _known.Keys.Order(StringComparer.Ordinal)]; }
    }

    public IReadOnlyDictionary<string, string>? Known(string container)
    {
        lock (_gate) return _known.TryGetValue(container, out var items) ? new Dictionary<string, string>(items) : null;
    }

    /// <summary>A container's listing as handed to the system (a directory enumeration's first page).</summary>
    public void Remember(string container, IEnumerable<FileProviderItem> items)
    {
        lock (_gate)
        {
            _known[container] = items.ToDictionary(i => i.Identifier, i => i.Version);
            Save();
        }
    }

    /// <summary>An item the system now has from a create or modify (so the next comparison does not report it again).</summary>
    public void Learned(FileProviderItem item, string? formerIdentifier = null)
    {
        lock (_gate)
        {
            if (formerIdentifier is not null && formerIdentifier != item.Identifier) ForgetLocked(formerIdentifier);
            if (_known.TryGetValue(item.ParentIdentifier, out var siblings)) siblings[item.Identifier] = item.Version;
            Save();
        }
    }

    /// <summary>An item the system deleted (and, for a directory, every container below it).</summary>
    public void Forget(string identifier)
    {
        lock (_gate)
        {
            ForgetLocked(identifier);
            Save();
        }
    }

    private void ForgetLocked(string identifier)
    {
        foreach (var items in _known.Values) items.Remove(identifier);
        var below = DocumentId.TryParse(identifier);
        foreach (var container in _known.Keys.ToList())
            if (container == identifier || (below is not null && DocumentId.TryParse(container) is { } c && below.Contains(c)))
                _known.Remove(container);
    }

    /// <summary>Signed out, or a new epoch: nothing the system had counts any more.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _known = [];
            Save();
        }
    }

    /// <summary>
    /// Compares a container's listing now with what the system was told, remembers the new one, and returns the
    /// differences. A gone container's items are all deleted (and the containers below it forgotten).
    /// </summary>
    public (List<FileProviderItem> Updated, List<string> Deleted) Compare(string container, ContainerListing listing)
    {
        lock (_gate)
        {
            var before = _known.TryGetValue(container, out var known) ? known : [];
            var updated = new List<FileProviderItem>();
            var deleted = new List<string>();
            if (listing.Items is not { } items)
            {
                deleted.AddRange(before.Keys);
                foreach (var id in before.Keys) ForgetLocked(id);
                ForgetLocked(container);
                Save();
                return (updated, deleted);
            }
            var now = new Dictionary<string, string>();
            foreach (var item in items)
            {
                now[item.Identifier] = item.Version;
                if (!before.TryGetValue(item.Identifier, out var version) || version != item.Version) updated.Add(item);
            }
            foreach (var id in before.Keys.Where(id => !now.ContainsKey(id)))
            {
                deleted.Add(id);
                ForgetLocked(id);
            }
            _known[container] = now;
            Save();
            return (updated, deleted);
        }
    }

    /// <summary>
    /// The working set's changes since <paramref name="anchor"/> (see the class). <paramref name="list"/> lists one container
    /// afresh (the caller drops the catalog's cached listings of the changed folders first); it throws when the listing
    /// fails for any other reason than the container being gone.
    /// </summary>
    public async Task<FileProviderChanges> ChangesAsync(FileProviderSignals signals, byte[]? anchor,
        Func<string, CancellationToken, Task<ContainerListing>> list, CancellationToken ct = default)
    {
        var now = new FileProviderAnchor(signals.Epoch, signals.Generation);
        if (FileProviderAnchor.Parse(anchor) is not { } from || from.Epoch != signals.Epoch || from.Generation > signals.Generation)
            return new FileProviderChanges([], [], now, Expired: true);
        if (from.Generation == signals.Generation) return new FileProviderChanges([], [], now);
        var changed = signals.ChangedSince(from.Generation);
        var updated = new Dictionary<string, FileProviderItem>();
        var deleted = new HashSet<string>();
        foreach (var container in Containers)
        {
            if (changed is not null && !(FileProviderIds.Parse(container).Document is { } doc && changed.Contains(doc.FolderId))) continue;
            if (Known(container) is null) continue; // forgotten meanwhile (below a deleted directory)
            var (u, d) = Compare(container, await list(container, ct));
            foreach (var item in u) updated[item.Identifier] = item;
            foreach (var id in d) deleted.Add(id);
        }
        foreach (var id in deleted) updated.Remove(id);
        return new FileProviderChanges([.. updated.Values], [.. deleted.Order(StringComparer.Ordinal)], now);
    }
}
