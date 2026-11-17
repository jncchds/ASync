using System.Collections.Concurrent;
using ASync.Client.Core.Media;
using ASync.Contracts;

namespace ASync.Tests.Client;

/// <summary>
/// A photo library in memory, shaped like Android's MediaStore (one resource per asset, one album = bucket per asset) unless a test
/// gives an asset several albums or resources (iOS). Counts the reads of each asset's bytes, so tests see what a backup really read.
/// Plain C# (no test framework): <c>ASync.App.Tests</c> links it too.
/// </summary>
public sealed class FakeMediaLibrary : IMediaLibrary
{
    private readonly ConcurrentDictionary<string, (MediaAsset Asset, Dictionary<string, byte[]> Bytes)> _assets = new();
    private readonly ConcurrentDictionary<string, string> _albums = new(); // id → name
    private int _nextId = 1000;

    public MediaAccess Access { get; set; } = MediaAccess.Full;
    /// <summary>Android 14's "Select photos": only these ids are visible while <see cref="Access"/> is Partial.</summary>
    public HashSet<string> Visible { get; } = [];
    public ConcurrentDictionary<string, int> Reads { get; } = new();
    public List<(MediaInsert Insert, byte[] Bytes)> Inserted { get; } = [];
    public Dictionary<string, MediaDetails> DetailsById { get; } = [];
    /// <summary>Assets whose files are "in the cloud only": a read without network access throws <see cref="MediaUnavailableException"/>.</summary>
    public HashSet<string> CloudOnly { get; } = [];
    /// <summary>The roles a restore hands over beside the original (iOS: "live").</summary>
    public IReadOnlyCollection<string> RestoredRoles { get; set; } = [];
    public List<(string FileName, string Role, string Name, byte[] Bytes)> ExtrasInserted { get; } = [];
    public List<(string Id, IReadOnlyList<MediaAlbumRef> Albums)> AlbumCalls { get; } = [];

    public string Album(string name, string? id = null)
    {
        id ??= $"b{Math.Abs(name.GetHashCode(StringComparison.Ordinal)) % 100000}";
        _albums[id] = name;
        return id;
    }

    public void RenameAlbum(string id, string name) => _albums[id] = name;

    /// <summary>Adds a photo (or video) to <paramref name="albums"/>; returns it.</summary>
    public MediaAsset Add(string fileName, byte[] bytes, DateTime taken, string[] albums, MediaKind kind = MediaKind.Photo, bool screenshot = false,
        bool favourite = false, DateTime? modified = null, string? id = null, (string Role, string FileName, byte[] Bytes)[]? extras = null)
    {
        var n = Interlocked.Increment(ref _nextId);
        id ??= $"android:external_primary:{n}";
        var mime = kind == MediaKind.Video ? "video/mp4" : "image/jpeg";
        var resources = new List<MediaResource> { new(MediaResource.Original, fileName, bytes.Length, mime) };
        var data = new Dictionary<string, byte[]> { [MediaResource.Original] = bytes };
        foreach (var (role, name, extra) in extras ?? [])
        {
            resources.Add(new MediaResource(role, name, extra.Length, "video/quicktime"));
            data[role] = extra;
        }
        var asset = new MediaAsset(id, id.Split(':')[^1], kind, fileName, mime, Ms(modified ?? taken), Ms(taken), resources, albums,
            Width: 4000, Height: 3000, DurationMs: kind == MediaKind.Video ? 1500 : null, Favourite: favourite, Screenshot: screenshot,
            RelativePath: albums.Length > 0 && _albums.TryGetValue(albums[0], out var a) ? $"DCIM/{a}/" : null);
        _assets[id] = (asset, data);
        return asset;
    }

    public void Update(string id, Func<MediaAsset, MediaAsset> change) =>
        _assets.AddOrUpdate(id, _ => throw new KeyNotFoundException(id), (_, v) => (change(v.Asset), v.Bytes));

    public void Remove(string id) => _assets.TryRemove(id, out _);

    public void Clear() => _assets.Clear();

    public static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private IEnumerable<MediaAsset> VisibleAssets() =>
        _assets.Values.Select(v => v.Asset).Where(a => Access == MediaAccess.Full || (Access == MediaAccess.Partial && Visible.Contains(a.Id)));

    public IReadOnlyList<MediaAlbum> Albums() =>
        [.. _albums.Select(kv => new MediaAlbum(kv.Key, kv.Value,
            VisibleAssets().Count(a => a.AlbumIds.Contains(kv.Key) && a.Kind == MediaKind.Photo),
            VisibleAssets().Count(a => a.AlbumIds.Contains(kv.Key) && a.Kind == MediaKind.Video), $"DCIM/{kv.Value}"))];

    public IReadOnlyList<MediaAsset> Assets() =>
        Access is MediaAccess.None or MediaAccess.Unsupported ? throw new UnauthorizedAccessException("No permission.") : [.. VisibleAssets()];

    public Stream OpenRead(MediaAsset asset, MediaResource resource)
    {
        if (!_assets.TryGetValue(asset.Id, out var v)) throw new FileNotFoundException($"{asset.Id} is gone.");
        Reads.AddOrUpdate(asset.Id, 1, (_, c) => c + 1);
        return new MemoryStream(v.Bytes[resource.Role], writable: false);
    }

    public Stream OpenRead(MediaAsset asset, MediaResource resource, MediaReadOptions options)
    {
        if (!options.AllowNetwork && CloudOnly.Contains(asset.Id)) throw new MediaUnavailableException($"{resource.FileName} is in the cloud only");
        return OpenRead(asset, resource);
    }

    public Task AddToAlbumsAsync(MediaAsset existing, IReadOnlyList<MediaAlbumRef> albums, CancellationToken ct)
    {
        lock (AlbumCalls) AlbumCalls.Add((existing.Id, albums));
        return Task.CompletedTask;
    }

    public MediaDetails Details(MediaAsset asset) => DetailsById.GetValueOrDefault(asset.Id) ?? new MediaDetails();

    public async Task<string> InsertAsync(MediaInsert insert, Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        lock (Inserted) Inserted.Add((insert, bytes));
        foreach (var extra in insert.Extras ?? [])
        {
            await using var s = extra.Open();
            using var b = new MemoryStream();
            await s.CopyToAsync(b, ct);
            lock (ExtrasInserted) ExtrasInserted.Add((insert.FileName, extra.Role, extra.FileName, b.ToArray()));
        }
        var album = _albums.FirstOrDefault(kv => kv.Value == insert.AlbumName).Key ?? Album(insert.AlbumName ?? "Restored");
        var added = Add(insert.FileName, bytes, DateTimeOffset.FromUnixTimeMilliseconds(insert.Taken ?? insert.Modified).UtcDateTime, [album], insert.Kind,
            favourite: insert.Favourite, modified: DateTimeOffset.FromUnixTimeMilliseconds(insert.Modified).UtcDateTime);
        return added.Id;
    }
}
