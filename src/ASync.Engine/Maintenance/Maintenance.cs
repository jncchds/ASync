using System.Security.Cryptography;
using ASync.Engine.Repository;
using ASync.Engine.Storage;

namespace ASync.Engine.Maintenance;

public sealed record PruneResult(int PacksDeleted, int PacksRepacked, int BlobsRemoved, long BytesFreed)
{
    /// <summary>One line for people: "2 packs removed, 1 repacked, 5.2 MB freed".</summary>
    public string Summary => PacksDeleted == 0 && PacksRepacked == 0
        ? "Nothing to remove"
        : $"{PacksDeleted} {(PacksDeleted == 1 ? "pack" : "packs")} removed, {PacksRepacked} repacked, {ByteSize.Format(BytesFreed)} freed";
}

public sealed record CheckResult(List<string> Errors, List<string> Warnings, int Snapshots, int Packs, int PacksRead)
{
    public bool Ok => Errors.Count == 0;

    /// <summary>One line for people: what was checked, or how many problems and the first one.</summary>
    public string Summary => Ok
        ? $"{Snapshots} {(Snapshots == 1 ? "snapshot" : "snapshots")}, {Packs} {(Packs == 1 ? "pack" : "packs")}" +
          (PacksRead > 0 ? $", {PacksRead} read back" : "") + (Warnings.Count > 0 ? $", {Warnings.Count} {(Warnings.Count == 1 ? "warning" : "warnings")}" : "")
        : $"{Errors.Count} {(Errors.Count == 1 ? "problem" : "problems")}: {Errors[0]}";
}

public static class ByteSize
{
    /// <summary>1536 → "1.5 KB" (powers of 1024).</summary>
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}

/// <summary>
/// Repository housekeeping: <see cref="ForgetAsync"/> drops snapshots by policy, <see cref="PruneAsync"/> removes
/// data no snapshot uses, <see cref="CheckAsync"/> verifies that everything is there and intact.
/// </summary>
public sealed class RepositoryMaintenance(Repository.Repository repository)
{
    /// <summary>Packs with more unused bytes than this share are rewritten with just their live blobs.</summary>
    public const double RepackThreshold = 0.3;

    /// <summary>
    /// Packs that no index lists are deleted by a prune only when they are older than this: a backup whose lock
    /// went stale may still be uploading packs it has not indexed yet.
    /// </summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromDays(1);

    public async Task<List<Snapshot>> ForgetAsync(RetentionPolicy policy, CancellationToken ct = default)
    {
        var (_, forget) = policy.Apply(await repository.ListSnapshotsAsync(ct));
        foreach (var s in forget) await repository.DeleteSnapshotAsync(s.Id, ct);
        return forget;
    }

    /// <summary>
    /// Deletes packs whose blobs no snapshot uses, repacks mostly-unused ones, and removes packs no index knows
    /// (left by an interrupted backup or prune) once they are older than <see cref="OrphanGrace"/>. Needs the
    /// repository alone (exclusive lock, refreshed while it works). Crash-safe order: new packs → new index →
    /// delete old index files → delete old packs. A crash leaves only extra data behind, never missing data.
    /// Any unreadable snapshot or tree aborts the prune before anything is deleted.
    /// </summary>
    public async Task<PruneResult> PruneAsync(CancellationToken ct = default)
    {
        await using var repoLock = await repository.LockAsync(exclusive: true, "prune", ct);
        var refresher = new LockRefresher(repoLock);
        await repository.LoadIndexAsync(ct);
        var used = await UsedBlobsAsync(await repository.ListSnapshotsAsync(ct), ct, refresher: refresher);

        var indexFiles = await repository.ReadIndexFilesAsync(ct);
        var packs = indexFiles.SelectMany(f => f.File.Packs).GroupBy(p => p.Id).Select(g => g.First()).ToList();

        var keepPacks = new List<PackEntry>();
        var deletePacks = new List<PackEntry>();
        int repacked = 0, blobsRemoved = 0;
        long freed = 0;
        foreach (var pack in packs)
        {
            await refresher.RefreshIfDueAsync(ct);
            var live = pack.Blobs.Where(b => used.Contains(b.Id)).ToList();
            var liveBytes = live.Sum(b => (long)b.Length);
            var totalBytes = pack.Blobs.Sum(b => (long)b.Length);
            if (live.Count == 0)
            {
                deletePacks.Add(pack);
                blobsRemoved += pack.Blobs.Count;
                freed += pack.Size;
            }
            else if (totalBytes > 0 && 1 - (double)liveBytes / totalBytes > RepackThreshold)
            {
                // Move the live blobs into new packs (decrypted and verified against their ids on the way).
                var content = await repository.Backend.ReadAllBytesAsync(PackWriter.PathOf(pack.Id), ct);
                foreach (var blob in live)
                {
                    var plaintext = BlobCodec.Open(repository.Keys, content.AsSpan((int)blob.Offset, blob.Length), blob.RawLength);
                    await repository.ResaveBlobAsync(blob.Id, blob.Type, plaintext, ct);
                }
                deletePacks.Add(pack);
                repacked++;
                blobsRemoved += pack.Blobs.Count - live.Count;
                freed += totalBytes - liveBytes;
            }
            else
            {
                keepPacks.Add(pack);
            }
        }

        if (deletePacks.Count > 0)
        {
            // New packs from repacking get their own index file on flush; the kept packs are rewritten into one.
            await repository.FlushAsync(ct);
            if (keepPacks.Count > 0) await repository.WriteIndexFileAsync(new IndexFile(keepPacks), ct);
            // Everything the old index files said is now in the new ones (kept packs) or gone (deleted packs).
            foreach (var (id, _) in indexFiles) await repository.Backend.DeleteAsync($"index/{id}", ct);
            foreach (var pack in deletePacks)
            {
                await refresher.RefreshIfDueAsync(ct);
                await repository.Backend.DeleteAsync(PackWriter.PathOf(pack.Id), ct);
            }
            await repository.LoadIndexAsync(ct);
        }

        // Packs no index file lists. Nobody else is writing (exclusive lock), but a backup whose lock went stale
        // could still be uploading, so only packs that have been lying around for a while are removed.
        var indexedNow = (await repository.ReadIndexFilesAsync(ct)).SelectMany(f => f.File.Packs).Select(p => p.Id).ToHashSet();
        var cutoff = DateTimeOffset.UtcNow - OrphanGrace;
        var stored = new List<StorageEntry>();
        await foreach (var entry in repository.Backend.ListAsync("data", recursive: true, ct)) stored.Add(entry);
        var orphans = 0;
        foreach (var entry in stored)
        {
            if (indexedNow.Contains(entry.Name) || entry.LastModified is not { } modified || modified > cutoff) continue;
            await repository.Backend.DeleteAsync(entry.Path, ct);
            orphans++;
            freed += entry.Size;
        }

        return new PruneResult(deletePacks.Count - repacked + orphans, repacked, blobsRemoved, freed);
    }

    /// <summary>
    /// Checks the repository: every snapshot and its trees load, every referenced blob is indexed, every indexed
    /// pack exists with its recorded size. With <paramref name="readDataFraction"/> &gt; 0, also downloads that
    /// share of packs (at least one) and verifies every blob in them decrypts and matches its id. Problems are
    /// returned, not thrown. Holds a shared lock, so backups may run meanwhile but a prune may not (a prune moving
    /// data under a check would look like damage).
    /// </summary>
    public async Task<CheckResult> CheckAsync(double readDataFraction = 0, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        await using var repoLock = await repository.LockAsync(exclusive: false, "check", ct);
        var refresher = new LockRefresher(repoLock);

        // Snapshots first, the index after: a backup finishing meanwhile writes its snapshot last, so every snapshot
        // listed here has its data in the index loaded next.
        var snapshotFiles = new List<string>();
        await foreach (var entry in repository.Backend.ListAsync("snapshots", ct: ct))
            if (!entry.IsDirectory) snapshotFiles.Add(entry.Name);
        var snapshots = new List<Snapshot>();
        foreach (var id in snapshotFiles)
        {
            try { snapshots.Add(await repository.LoadSnapshotAsync(id, ct)); }
            catch (StorageNotFoundException) { /* forgotten meanwhile */ }
            catch (Exception e) when (IsDamage(e)) { errors.Add($"Snapshot {Repository.Repository.ShortSnapshotId(id)} cannot be read: {e.Message}"); }
        }

        List<PackEntry> indexed = [];
        IReadOnlyDictionary<BlobId, BlobLocation> index = new Dictionary<BlobId, BlobLocation>();
        try
        {
            await repository.LoadIndexAsync(ct);
            index = repository.IndexSnapshot();
            indexed = [.. (await repository.ReadIndexFilesAsync(ct)).SelectMany(f => f.File.Packs).GroupBy(p => p.Id).Select(g => g.First())];
        }
        catch (Exception e) when (IsDamage(e)) { errors.Add($"The index cannot be read: {e.Message}"); }

        var used = await UsedBlobsAsync(snapshots, ct, errors, refresher);
        foreach (var id in used.Where(id => !index.ContainsKey(id)))
            errors.Add($"Blob {id.Short} is used by a snapshot but missing from the index.");

        var stored = new Dictionary<string, long>();
        await foreach (var entry in repository.Backend.ListAsync("data", recursive: true, ct))
            stored[entry.Name] = entry.Size;
        foreach (var pack in indexed)
        {
            if (!stored.TryGetValue(pack.Id, out var size)) errors.Add($"Pack {pack.Id[..8]} is indexed but missing from storage.");
            else if (size != pack.Size) errors.Add($"Pack {pack.Id[..8]} is {size} bytes, expected {pack.Size}.");
        }
        foreach (var orphan in stored.Keys.Except(indexed.Select(p => p.Id)))
            warnings.Add($"Pack {orphan[..Math.Min(8, orphan.Length)]} is not indexed (a backup in progress or interrupted; prune removes leftovers).");

        var packsRead = 0;
        if (readDataFraction > 0 && indexed.Count > 0)
        {
            var count = Math.Max(1, (int)Math.Ceiling(indexed.Count * Math.Min(1, readDataFraction)));
            foreach (var pack in indexed.Where(p => stored.ContainsKey(p.Id)).OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).Take(count))
            {
                await refresher.RefreshIfDueAsync(ct);
                packsRead++;
                byte[] content;
                try { content = await repository.Backend.ReadAllBytesAsync(PackWriter.PathOf(pack.Id), ct); }
                catch (StorageNotFoundException) { errors.Add($"Pack {pack.Id[..8]} is indexed but missing from storage."); continue; }
                if (Convert.ToHexStringLower(SHA256.HashData(content)) != pack.Id)
                {
                    errors.Add($"Pack {pack.Id[..8]} is damaged (its content does not match its id).");
                    continue;
                }
                foreach (var blob in pack.Blobs)
                {
                    try
                    {
                        var plaintext = BlobCodec.Open(repository.Keys, content.AsSpan((int)blob.Offset, blob.Length), blob.RawLength);
                        if (repository.Keys.Id(plaintext) != blob.Id) errors.Add($"Blob {blob.Id.Short} in pack {pack.Id[..8]} does not match its id.");
                    }
                    catch (Exception e) when (e is CryptographicException or InvalidDataException or ArgumentOutOfRangeException)
                    {
                        errors.Add($"Blob {blob.Id.Short} in pack {pack.Id[..8]} cannot be decrypted: {e.Message}");
                    }
                }
            }
        }
        return new CheckResult(errors, warnings, snapshots.Count, indexed.Count, packsRead);
    }

    /// <summary>What reading a damaged object throws: reported by a check, fatal for a prune.</summary>
    private static bool IsDamage(Exception e) =>
        e is CryptographicException or InvalidDataException or System.Text.Json.JsonException or StorageNotFoundException
            or ArgumentException or IndexOutOfRangeException;

    /// <summary>Keeps a long operation's lock from going stale.</summary>
    private sealed class LockRefresher(RepositoryLock repoLock)
    {
        private DateTimeOffset _last = DateTimeOffset.UtcNow;

        public async Task RefreshIfDueAsync(CancellationToken ct)
        {
            if (DateTimeOffset.UtcNow - _last < TimeSpan.FromMinutes(5)) return;
            _last = DateTimeOffset.UtcNow;
            await repoLock.RefreshAsync(ct);
        }
    }

    /// <summary>Every blob (trees and data) reachable from the snapshots.</summary>
    private async Task<HashSet<BlobId>> UsedBlobsAsync(List<Snapshot> snapshots, CancellationToken ct, List<string>? errors = null,
        LockRefresher? refresher = null)
    {
        var used = new HashSet<BlobId>();
        var stack = new Stack<BlobId>(snapshots.Select(s => s.Tree));
        while (stack.TryPop(out var treeId))
        {
            if (!used.Add(treeId)) continue; // a shared subtree is walked once
            if (refresher is not null) await refresher.RefreshIfDueAsync(ct);
            Tree tree;
            try { tree = await repository.LoadTreeAsync(treeId, ct); }
            catch (Exception e) when (errors is not null && e is not OperationCanceledException)
            {
                errors.Add($"Tree {treeId.Short} cannot be read: {e.Message}");
                continue;
            }
            foreach (var node in tree.Nodes)
            {
                if (node.Subtree is BlobId sub) stack.Push(sub);
                foreach (var id in node.Content ?? []) used.Add(id);
            }
        }
        return used;
    }
}

/// <summary>What one prune or check came to, in the form the server records and the UI shows.</summary>
/// <param name="Busy">Not run: someone else holds the repository's lock. Not a failure; try again later.</param>
public sealed record MaintenanceOutcome(bool Ok, bool Busy, string Message, PruneResult? Prune = null, CheckResult? Check = null);

/// <summary>
/// Runs a prune or a check and turns the result, a busy repository or a failure into a
/// <see cref="MaintenanceOutcome"/>. The server (Transport folders) and devices (end-to-end folders) share it.
/// </summary>
public static class MaintenanceRunner
{
    public static Task<MaintenanceOutcome> PruneAsync(Repository.Repository repository, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var result = await new RepositoryMaintenance(repository).PruneAsync(ct);
            return new MaintenanceOutcome(true, false, result.Summary, Prune: result);
        });

    /// <param name="readDataPercent">0–100: the share of packs to download and verify.</param>
    public static Task<MaintenanceOutcome> CheckAsync(Repository.Repository repository, double readDataPercent, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var result = await new RepositoryMaintenance(repository).CheckAsync(Math.Clamp(readDataPercent, 0, 100) / 100, ct);
            return new MaintenanceOutcome(result.Ok, false, result.Summary, Check: result);
        });

    private static async Task<MaintenanceOutcome> RunAsync(Func<Task<MaintenanceOutcome>> run)
    {
        try { return await run(); }
        catch (RepositoryLockedException e) { return new MaintenanceOutcome(false, true, e.Message); }
        // Storage SDKs throw their own exception types; whatever it was, it is a failed run with a message.
        catch (Exception e) when (e is not OperationCanceledException) { return new MaintenanceOutcome(false, false, e.Message); }
    }
}
