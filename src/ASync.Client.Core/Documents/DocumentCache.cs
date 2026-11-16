using System.Security.Cryptography;
using System.Text;

namespace ASync.Client.Core.Documents;

/// <summary>
/// Files fetched for the documents provider, on disk, bounded in size, least recently used out first. A file is cached
/// under a key that names its content (folder, path, size and modification time; a snapshot's file is immutable), so a
/// changed file is simply a new key and the old one ages out. Files larger than <see cref="MaxFileBytes"/> are never
/// cached (the provider streams them). Two requests for the same key share one download. The cache survives process
/// restarts (it is rebuilt from the directory; half-written <c>.part</c> files are deleted).
/// </summary>
public sealed class DocumentCache
{
    private sealed class Entry(string key, string path, long size, DateTime used)
    {
        public string Key { get; } = key;
        public string Path { get; } = path;
        public long Size { get; } = size;
        public DateTime Used { get; set; } = used;
        public int Pins { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Dictionary<string, Task<Entry>> _filling = [];
    private long _total;

    public string Directory { get; }
    public long MaxBytes { get; }
    public long MaxFileBytes { get; }

    /// <summary>The clock for "least recently used" (tests move it).</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    public DocumentCache(string directory, long maxBytes, long? maxFileBytes = null)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        Directory = directory;
        MaxBytes = maxBytes;
        MaxFileBytes = Math.Min(maxFileBytes ?? maxBytes / 4, maxBytes);
        System.IO.Directory.CreateDirectory(directory);
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
        {
            if (file.Name.EndsWith(".part", StringComparison.Ordinal)) { TryDelete(file.FullName); continue; }
            // The key itself is not on disk; an entry from before is found again by its file name (the key's hash).
            _entries[file.Name] = new Entry(file.Name, file.FullName, file.Length, file.LastWriteTimeUtc);
            _total += file.Length;
        }
        lock (_gate) EvictLocked();
    }

    public long TotalBytes { get { lock (_gate) return _total; } }
    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>Whether a file of <paramref name="size"/> bytes is cached (otherwise it is streamed).</summary>
    public bool Fits(long size) => size <= MaxFileBytes;

    /// <summary>The cache key for a file's content.</summary>
    public static string Key(string documentKey, long size, DateTimeOffset? modified) =>
        $"{documentKey}\n{size}\n{modified?.ToUnixTimeMilliseconds() ?? 0}";

    public bool Contains(string key)
    {
        lock (_gate) return _entries.ContainsKey(FileName(key));
    }

    /// <summary>
    /// The cached file for <paramref name="key"/>, fetched with <paramref name="fill"/> when it is not there. The file is
    /// kept while the returned lease lives (open it, then dispose the lease: an open descriptor keeps the bytes even if
    /// the entry is evicted later). <paramref name="fill"/> must write exactly <paramref name="size"/> bytes, otherwise
    /// the file changed while it was read and nothing is kept.
    /// </summary>
    public async Task<CacheLease> GetAsync(string key, long size, Func<Stream, CancellationToken, Task> fill, CancellationToken ct = default)
    {
        if (!Fits(size)) throw new ArgumentOutOfRangeException(nameof(size), "Too large for the cache: stream it instead.");
        var name = FileName(key);
        while (true)
        {
            Task<Entry> filling;
            lock (_gate)
            {
                if (_entries.TryGetValue(name, out var hit))
                {
                    hit.Used = Clock();
                    hit.Pins++;
                    Touch(hit);
                    return new CacheLease(this, hit, hit.Path);
                }
                if (!_filling.TryGetValue(name, out filling!))
                {
                    filling = FillAsync(name, size, fill);
                    _filling[name] = filling;
                }
            }
            try
            {
                // A caller that gives up does not cancel the download others may wait for.
                await filling.WaitAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested && !filling.IsCompletedSuccessfully)
            {
                await filling; // the download's own error
            }
        }
    }

    private async Task<Entry> FillAsync(string name, long size, Func<Stream, CancellationToken, Task> fill)
    {
        await Task.Yield();
        var path = Path.Combine(Directory, name);
        var part = path + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
        try
        {
            await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await fill(output, CancellationToken.None);
                if (output.Length != size)
                    throw new IOException($"The file changed while it was being fetched ({output.Length} bytes instead of {size}). Try again.");
            }
            File.Move(part, path, overwrite: true);
            lock (_gate)
            {
                var entry = new Entry(name, path, size, Clock());
                if (_entries.Remove(name, out var old)) _total -= old.Size;
                _entries[name] = entry;
                _total += size;
                EvictLocked(keep: entry);
                return entry;
            }
        }
        catch
        {
            TryDelete(part);
            throw;
        }
        finally
        {
            lock (_gate) _filling.Remove(name);
        }
    }

    /// <summary>Drops one entry (a file known to have changed).</summary>
    public void Remove(string key)
    {
        lock (_gate)
            if (_entries.TryGetValue(FileName(key), out var entry) && entry.Pins == 0)
            {
                _entries.Remove(entry.Key);
                _total -= entry.Size;
                TryDelete(entry.Path);
            }
    }

    /// <summary>Removes everything that is not in use (sign-out).</summary>
    public void Clear()
    {
        lock (_gate)
            foreach (var entry in _entries.Values.Where(e => e.Pins == 0).ToList())
            {
                _entries.Remove(entry.Key);
                _total -= entry.Size;
                TryDelete(entry.Path);
            }
    }

    internal void Release(object entry)
    {
        lock (_gate)
        {
            ((Entry)entry).Pins--;
            EvictLocked();
        }
    }

    private void EvictLocked(Entry? keep = null)
    {
        if (_total <= MaxBytes) return;
        foreach (var entry in _entries.Values.Where(e => e.Pins == 0 && !ReferenceEquals(e, keep)).OrderBy(e => e.Used).ToList())
        {
            if (_total <= MaxBytes) break;
            _entries.Remove(entry.Key);
            _total -= entry.Size;
            TryDelete(entry.Path);
        }
    }

    private void Touch(Entry entry)
    {
        try { File.SetLastWriteTimeUtc(entry.Path, entry.Used); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string FileName(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40];

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>A cached file in use; dispose it once the file is opened.</summary>
public sealed class CacheLease : IDisposable
{
    private DocumentCache? _cache;
    private readonly object _entry;

    internal CacheLease(DocumentCache cache, object entry, string path)
    {
        _cache = cache;
        _entry = entry;
        Path = path;
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _cache, null) is { } cache) cache.Release(_entry);
    }
}
