using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ASync.Engine.Chunking;
using ASync.Engine.Crypto;
using ASync.Engine.Storage;

namespace ASync.Engine.Repository;

/// <summary>
/// The repository layout: a self-contained, encrypted, deduplicated store of snapshots on any
/// <see cref="IStorageBackend"/>. Everything needed to restore lives on the back-end, so a repository survives
/// losing the server and its database.
/// <code>
/// config               plaintext: format version, id, chunker parameters
/// keys/&lt;id&gt;            repository keys wrapped by a passphrase, recovery key or server key
/// data/&lt;xx&gt;/&lt;pack&gt;     packs of encrypted blobs (file chunks and directory trees)
/// index/&lt;id&gt;           encrypted: blob → pack locations (rebuildable from pack headers)
/// snapshots/&lt;id&gt;       encrypted snapshot records
/// locks/&lt;id&gt;           who is using the repository (prune needs it alone)
/// </code>
/// Not thread-safe for writers: one backup or prune per instance at a time. Reads may run concurrently.
/// </summary>
public sealed class Repository
{
    /// <summary>Packs are flushed once they reach this size.</summary>
    public const int TargetPackSize = 16 * 1024 * 1024;

    private readonly Dictionary<BlobId, BlobLocation> _index = [];
    private readonly Dictionary<BlobType, PackWriter> _writers = [];
    private readonly List<PackEntry> _unindexed = [];
    private readonly Lock _gate = new();

    public IStorageBackend Backend { get; }
    public RepositoryConfig Config { get; }
    public RepositoryKeys Keys { get; }
    public FastCdc Chunker { get; }

    private Repository(IStorageBackend backend, RepositoryConfig config, RepositoryKeys keys)
    {
        Backend = backend;
        Config = config;
        Keys = keys;
        Chunker = new FastCdc(config.Chunker);
    }

    // ── Create and open ───────────────────────────────────────────────────────

    public static async Task<bool> ExistsAsync(IStorageBackend backend, CancellationToken ct = default) =>
        await backend.ExistsAsync("config", ct);

    /// <summary>Creates a repository protected by the given key files (made from <paramref name="keys"/>).</summary>
    public static async Task<Repository> InitAsync(IStorageBackend backend, RepositoryKeys keys, IReadOnlyCollection<KeyFile> keyFiles,
        ChunkerParameters? chunker = null, CancellationToken ct = default)
    {
        if (keyFiles.Count == 0) throw new ArgumentException("A repository needs at least one key file.", nameof(keyFiles));
        if (await ExistsAsync(backend, ct)) throw new InvalidOperationException("A repository already exists here.");

        var config = new RepositoryConfig(RepositoryConfig.CurrentVersion, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            chunker ?? FastCdc.Default, DateTimeOffset.UtcNow);
        foreach (var keyFile in keyFiles) await AddKeyFileAsync(backend, keyFile, ct);
        // config last: a half-initialised repository has no config and is not mistaken for a real one.
        await backend.WriteAllBytesAsync("config", JsonSerializer.SerializeToUtf8Bytes(config, EngineJson.Default.RepositoryConfig), ct);
        return new Repository(backend, config, keys);
    }

    /// <summary>Opens with a passphrase or a recovery key (whichever key file accepts it).</summary>
    public static Task<Repository> OpenAsync(IStorageBackend backend, string passphraseOrRecoveryKey, CancellationToken ct = default) =>
        OpenAsync(backend, kf => kf.Kind == KeyKind.Server ? null : kf.TryOpen(passphraseOrRecoveryKey), ct);

    public static Task<Repository> OpenAsync(IStorageBackend backend, byte[] serverKey, CancellationToken ct = default) =>
        OpenAsync(backend, kf => kf.Kind == KeyKind.Server ? kf.TryOpen(serverKey) : null, ct);

    private static async Task<Repository> OpenAsync(IStorageBackend backend, Func<KeyFile, RepositoryKeys?> open, CancellationToken ct)
    {
        var configBytes = await TryReadAsync(backend, "config", ct) ?? throw new InvalidOperationException("No repository here.");
        var config = JsonSerializer.Deserialize(configBytes, EngineJson.Default.RepositoryConfig)!;
        if (config.Version > RepositoryConfig.CurrentVersion)
            throw new InvalidOperationException($"The repository uses format {config.Version}; this version of ASync reads up to {RepositoryConfig.CurrentVersion}.");

        foreach (var (_, keyFile) in await ListKeyFilesAsync(backend, ct))
            if (open(keyFile) is { } keys)
                return new Repository(backend, config, keys);
        throw new WrongKeyException();
    }

    // ── Key files ─────────────────────────────────────────────────────────────

    public static async Task<string> AddKeyFileAsync(IStorageBackend backend, KeyFile keyFile, CancellationToken ct = default)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await backend.WriteAllBytesAsync($"keys/{id}", JsonSerializer.SerializeToUtf8Bytes(keyFile, EngineJson.Default.KeyFile), ct);
        return id;
    }

    public Task<string> AddKeyFileAsync(KeyFile keyFile, CancellationToken ct = default) => AddKeyFileAsync(Backend, keyFile, ct);

    public static async Task<List<(string Id, KeyFile File)>> ListKeyFilesAsync(IStorageBackend backend, CancellationToken ct = default)
    {
        var result = new List<(string, KeyFile)>();
        await foreach (var entry in backend.ListAsync("keys", ct: ct))
        {
            if (entry.IsDirectory) continue;
            var keyFile = JsonSerializer.Deserialize(await backend.ReadAllBytesAsync(entry.Path, ct), EngineJson.Default.KeyFile);
            if (keyFile is not null) result.Add((entry.Name, keyFile));
        }
        return result;
    }

    /// <summary>Removes a key file, refusing to remove the last one (that would lock everyone out for good).</summary>
    public async Task RemoveKeyFileAsync(string id, CancellationToken ct = default)
    {
        var all = await ListKeyFilesAsync(Backend, ct);
        if (all.All(k => k.Id != id)) throw new ArgumentException($"No key file {id}.");
        if (all.Count == 1) throw new InvalidOperationException("Cannot remove the repository's last key.");
        await Backend.DeleteAsync($"keys/{id}", ct);
    }

    // ── Index ─────────────────────────────────────────────────────────────────

    public int BlobCount { get { lock (_gate) return _index.Count; } }

    public async Task LoadIndexAsync(CancellationToken ct = default)
    {
        var loaded = new Dictionary<BlobId, BlobLocation>();
        foreach (var (_, file) in await ReadIndexFilesAsync(ct))
            foreach (var pack in file.Packs)
                foreach (var blob in pack.Blobs)
                    loaded[blob.Id] = new BlobLocation(pack.Id, blob.Type, blob.Offset, blob.Length, blob.RawLength);
        lock (_gate)
        {
            _index.Clear();
            foreach (var (id, location) in loaded) _index[id] = location;
        }
    }

    internal async Task<List<(string Id, IndexFile File)>> ReadIndexFilesAsync(CancellationToken ct = default)
    {
        var result = new List<(string, IndexFile)>();
        await foreach (var entry in Backend.ListAsync("index", ct: ct))
        {
            if (entry.IsDirectory) continue;
            result.Add((entry.Name, await ReadEncryptedAsync(entry.Path, EngineJson.Default.IndexFile, "index", ct)));
        }
        return result;
    }

    public bool HasBlob(BlobId id) { lock (_gate) return _index.ContainsKey(id); }

    public BlobLocation? Locate(BlobId id) { lock (_gate) return _index.GetValueOrDefault(id); }

    internal IReadOnlyDictionary<BlobId, BlobLocation> IndexSnapshot() { lock (_gate) return new Dictionary<BlobId, BlobLocation>(_index); }

    // ── Blobs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores a blob unless one with the same content already exists (deduplication). Returns its id and the number
    /// of bytes it added to the repository (0 when deduplicated). Call <see cref="FlushAsync"/> before relying on it.
    /// </summary>
    public async Task<(BlobId Id, long Stored)> SaveBlobAsync(BlobType type, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        var id = Keys.Id(plaintext.Span);
        if (HasBlob(id)) return (id, 0);

        var sealedBlob = BlobCodec.Seal(Keys, plaintext.Span, tryCompress: true);
        PackWriter writer;
        lock (_gate)
        {
            if (_index.ContainsKey(id)) return (id, 0);
            if (!_writers.TryGetValue(type, out writer!)) _writers[type] = writer = new PackWriter(Keys);
            writer.Add(id, type, sealedBlob, plaintext.Length);
            // Visible to dedup at once; the pack id is filled in when the pack is written.
            _index[id] = new BlobLocation("", type, 0, sealedBlob.Length, plaintext.Length);
        }
        if (writer.Size >= TargetPackSize) await WritePackAsync(type, ct);
        return (id, sealedBlob.Length);
    }

    /// <summary>Stores a blob again even though the index has it (prune moves live blobs out of a pack it deletes).</summary>
    internal async Task ResaveBlobAsync(BlobId id, BlobType type, byte[] plaintext, CancellationToken ct)
    {
        lock (_gate) _index.Remove(id);
        var (saved, _) = await SaveBlobAsync(type, plaintext, ct);
        if (saved != id) throw new InvalidDataException($"Blob {id.Short} does not match its id.");
    }

    public async Task<byte[]> ReadBlobAsync(BlobId id, CancellationToken ct = default)
    {
        var location = Locate(id) ?? throw new BlobNotFoundException(id);
        if (location.Pack.Length == 0) await FlushAsync(ct); // still in a pack being written
        location = Locate(id)!;

        var sealedBlob = await Backend.ReadRangeAsync(PackWriter.PathOf(location.Pack), location.Offset, location.Length, ct);
        var plaintext = BlobCodec.Open(Keys, sealedBlob, location.RawLength);
        if (Keys.Id(plaintext) != id) throw new InvalidDataException($"Blob {id.Short} does not match its id: the repository is damaged.");
        return plaintext;
    }

    /// <summary>Writes buffered packs and an index file for them. Blobs saved before this call are then durable.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        BlobType[] pending;
        lock (_gate) pending = [.. _writers.Keys];
        foreach (var type in pending) await WritePackAsync(type, ct);

        List<PackEntry> toIndex;
        lock (_gate)
        {
            toIndex = [.. _unindexed];
            _unindexed.Clear();
        }
        if (toIndex.Count > 0) await WriteIndexFileAsync(new IndexFile(toIndex), ct);
    }

    private async Task WritePackAsync(BlobType type, CancellationToken ct)
    {
        PackWriter? writer;
        lock (_gate)
        {
            if (!_writers.Remove(type, out writer) || writer.Count == 0) return;
        }
        var (packId, content, entry) = writer.Finish();
        await Backend.WriteAllBytesAsync(PackWriter.PathOf(packId), content, ct);
        lock (_gate)
        {
            foreach (var blob in entry.Blobs)
                _index[blob.Id] = new BlobLocation(packId, blob.Type, blob.Offset, blob.Length, blob.RawLength);
            _unindexed.Add(entry);
        }
    }

    internal async Task<string> WriteIndexFileAsync(IndexFile file, CancellationToken ct = default)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await WriteEncryptedAsync($"index/{id}", file, EngineJson.Default.IndexFile, "index", ct);
        return id;
    }

    // ── Trees ─────────────────────────────────────────────────────────────────

    public async Task<BlobId> SaveTreeAsync(Tree tree, CancellationToken ct = default)
    {
        var sorted = new Tree([.. tree.Nodes.OrderBy(n => n.Name, StringComparer.Ordinal)]);
        return (await SaveBlobAsync(BlobType.Tree, JsonSerializer.SerializeToUtf8Bytes(sorted, EngineJson.Default.Tree), ct)).Id;
    }

    public async Task<Tree> LoadTreeAsync(BlobId id, CancellationToken ct = default) =>
        JsonSerializer.Deserialize(await ReadBlobAsync(id, ct), EngineJson.Default.Tree)
        ?? throw new InvalidDataException($"Tree {id.Short} is empty.");

    // ── Snapshots ─────────────────────────────────────────────────────────────

    public static string NewSnapshotId() => Guid.CreateVersion7().ToString("N");

    public Task SaveSnapshotAsync(Snapshot snapshot, CancellationToken ct = default) =>
        WriteEncryptedAsync($"snapshots/{snapshot.Id}", snapshot, EngineJson.Default.Snapshot, "snapshot", ct);

    public Task<Snapshot> LoadSnapshotAsync(string id, CancellationToken ct = default) =>
        ReadEncryptedAsync($"snapshots/{id}", EngineJson.Default.Snapshot, "snapshot", ct);

    /// <summary>All snapshots, oldest first.</summary>
    public async Task<List<Snapshot>> ListSnapshotsAsync(CancellationToken ct = default)
    {
        var result = new List<Snapshot>();
        await foreach (var entry in Backend.ListAsync("snapshots", ct: ct))
            if (!entry.IsDirectory)
                result.Add(await LoadSnapshotAsync(entry.Name, ct));
        return [.. result.OrderBy(s => s.Time)];
    }

    /// <summary>Forgets a snapshot. Its data stays until a prune removes what no other snapshot uses.</summary>
    public Task DeleteSnapshotAsync(string id, CancellationToken ct = default) => Backend.DeleteAsync($"snapshots/{id}", ct);

    // ── Locks ─────────────────────────────────────────────────────────────────

    /// <summary>Locks not refreshed for this long belong to crashed processes and are ignored.</summary>
    public static readonly TimeSpan LockStaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Takes a shared lock (backups, restores) or an exclusive one (prune). Fails if it conflicts with a live lock.
    /// Dispose the result to release it; long operations should call <see cref="RepositoryLock.RefreshAsync"/>.
    /// </summary>
    public async Task<RepositoryLock> LockAsync(bool exclusive, string owner, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var path = $"locks/{id}";
        await WriteEncryptedAsync(path, new LockFile(exclusive, owner, now, now), EngineJson.Default.LockFile, "lock", ct);

        // Write first, then look: two processes racing both see each other and both back off, never both proceed.
        await foreach (var entry in Backend.ListAsync("locks", ct: ct))
        {
            if (entry.IsDirectory || entry.Name == id) continue;
            LockFile other;
            try { other = await ReadEncryptedAsync(entry.Path, EngineJson.Default.LockFile, "lock", ct); }
            catch (StorageNotFoundException) { continue; }
            if (now - other.Refreshed > LockStaleAfter) continue;
            if (exclusive || other.Exclusive)
            {
                await Backend.DeleteAsync(path, ct);
                throw new RepositoryLockedException(other.Owner, other.Exclusive);
            }
        }
        return new RepositoryLock(this, path, new LockFile(exclusive, owner, now, now));
    }

    internal Task WriteLockAsync(string path, LockFile file, CancellationToken ct) =>
        WriteEncryptedAsync(path, file, EngineJson.Default.LockFile, "lock", ct);

    // ── Encrypted JSON objects ────────────────────────────────────────────────

    private async Task WriteEncryptedAsync<T>(string path, T value, JsonTypeInfo<T> type, string kind, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, type);
        await Backend.WriteAllBytesAsync(path, BlobCodec.Seal(Keys, json, tryCompress: true, ObjectAd(kind)), ct);
    }

    private async Task<T> ReadEncryptedAsync<T>(string path, JsonTypeInfo<T> type, string kind, CancellationToken ct)
    {
        var json = BlobCodec.Open(Keys, await Backend.ReadAllBytesAsync(path, ct), rawLength: null, ObjectAd(kind));
        return JsonSerializer.Deserialize(json, type) ?? throw new InvalidDataException($"{path} is empty.");
    }

    /// <summary>Binds each object to its kind, so (say) a lock file cannot be swapped in as a snapshot.</summary>
    private static byte[] ObjectAd(string kind) => System.Text.Encoding.ASCII.GetBytes("async-object:" + kind);

    private static async Task<byte[]?> TryReadAsync(IStorageBackend backend, string path, CancellationToken ct)
    {
        try { return await backend.ReadAllBytesAsync(path, ct); }
        catch (StorageNotFoundException) { return null; }
    }
}

public sealed class RepositoryLock : IAsyncDisposable
{
    private readonly Repository _repository;
    private readonly string _path;
    private LockFile _file;

    internal RepositoryLock(Repository repository, string path, LockFile file)
    {
        _repository = repository;
        _path = path;
        _file = file;
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        _file = _file with { Refreshed = DateTimeOffset.UtcNow };
        await _repository.WriteLockAsync(_path, _file, ct);
    }

    public async ValueTask DisposeAsync() => await _repository.Backend.DeleteAsync(_path);
}

public class WrongKeyException() : CryptographicException("The passphrase or key does not open this repository.");

public class BlobNotFoundException(BlobId id) : IOException($"Blob {id.Short} is not in the repository index.")
{
    public BlobId Id { get; } = id;
}

public class RepositoryLockedException(string owner, bool exclusive)
    : InvalidOperationException($"The repository is in use by {owner}{(exclusive ? " (exclusively, e.g. a prune)" : "")}. Try again later.")
{
    public string Owner { get; } = owner;
}
