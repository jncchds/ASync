using System.Security.Cryptography;
using ASync.Engine.Repository;
using ASync.Engine.Storage;

namespace ASync.Engine.Maintenance;

public sealed record PruneResult(int PacksDeleted, int PacksRepacked, int BlobsRemoved, long BytesFreed);

public sealed record CheckResult(List<string> Errors, List<string> Warnings, int Snapshots, int Packs, int PacksRead)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Repository housekeeping: <see cref="ForgetAsync"/> drops snapshots by policy, <see cref="PruneAsync"/> removes
/// data no snapshot uses, <see cref="CheckAsync"/> verifies that everything is there and intact.
/// </summary>
public sealed class RepositoryMaintenance(Repository.Repository repository)
{
    /// <summary>Packs with more unused bytes than this share are rewritten with just their live blobs.</summary>
    public const double RepackThreshold = 0.3;

    public async Task<List<Snapshot>> ForgetAsync(RetentionPolicy policy, CancellationToken ct = default)
    {
        var (_, forget) = policy.Apply(await repository.ListSnapshotsAsync(ct));
        foreach (var s in forget) await repository.DeleteSnapshotAsync(s.Id, ct);
        return forget;
    }

    /// <summary>
    /// Deletes packs whose blobs no snapshot uses, and repacks mostly-unused ones. Needs the repository alone
    /// (exclusive lock). Crash-safe order: new packs → new index → delete old index files → delete old packs.
    /// A crash leaves only extra data behind, never missing data.
    /// </summary>
    public async Task<PruneResult> PruneAsync(CancellationToken ct = default)
    {
        await using var repoLock = await repository.LockAsync(exclusive: true, "prune", ct);
        await repository.LoadIndexAsync(ct);
        var used = await UsedBlobsAsync(await repository.ListSnapshotsAsync(ct), ct);

        var indexFiles = await repository.ReadIndexFilesAsync(ct);
        var packs = indexFiles.SelectMany(f => f.File.Packs).GroupBy(p => p.Id).Select(g => g.First()).ToList();

        var keepPacks = new List<PackEntry>();
        var deletePacks = new List<PackEntry>();
        int repacked = 0, blobsRemoved = 0;
        long freed = 0;
        foreach (var pack in packs)
        {
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
                // Copy the live blobs as they are (still encrypted) into new packs.
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

        if (deletePacks.Count == 0) return new PruneResult(0, 0, 0, 0);

        // New packs from repacking get their own index file on flush; the kept packs are rewritten into one.
        await repository.FlushAsync(ct);
        if (keepPacks.Count > 0) await repository.WriteIndexFileAsync(new IndexFile(keepPacks), ct);
        // Everything the old index files said is now in the new ones (kept packs) or gone (deleted packs).
        foreach (var (id, _) in indexFiles) await repository.Backend.DeleteAsync($"index/{id}", ct);
        foreach (var pack in deletePacks) await repository.Backend.DeleteAsync(PackWriter.PathOf(pack.Id), ct);

        await repository.LoadIndexAsync(ct);
        return new PruneResult(deletePacks.Count - repacked, repacked, blobsRemoved, freed);
    }

    /// <summary>
    /// Checks the repository's structure: every snapshot's trees load, every referenced blob is indexed, every
    /// indexed pack exists with its recorded size. With <paramref name="readDataFraction"/> &gt; 0, also downloads that
    /// share of packs (at least one) and verifies every blob in them decrypts and matches its id.
    /// </summary>
    public async Task<CheckResult> CheckAsync(double readDataFraction = 0, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        await repository.LoadIndexAsync(ct);
        var index = repository.IndexSnapshot();
        var snapshots = await repository.ListSnapshotsAsync(ct);

        HashSet<BlobId> used;
        try { used = await UsedBlobsAsync(snapshots, ct, errors); }
        catch (Exception e) when (e is not OperationCanceledException) { errors.Add($"Walking snapshots failed: {e.Message}"); used = []; }
        foreach (var id in used.Where(id => !index.ContainsKey(id)))
            errors.Add($"Blob {id.Short} is used by a snapshot but missing from the index.");

        var indexed = (await repository.ReadIndexFilesAsync(ct)).SelectMany(f => f.File.Packs).GroupBy(p => p.Id).Select(g => g.First()).ToList();
        var stored = new Dictionary<string, long>();
        await foreach (var entry in repository.Backend.ListAsync("data", recursive: true, ct))
            stored[entry.Name] = entry.Size;
        foreach (var pack in indexed)
        {
            if (!stored.TryGetValue(pack.Id, out var size)) errors.Add($"Pack {pack.Id[..8]} is indexed but missing from storage.");
            else if (size != pack.Size) errors.Add($"Pack {pack.Id[..8]} is {size} bytes, expected {pack.Size}.");
        }
        foreach (var orphan in stored.Keys.Except(indexed.Select(p => p.Id)))
            warnings.Add($"Pack {orphan[..Math.Min(8, orphan.Length)]} is not indexed (an interrupted backup, or rebuild the index).");

        var packsRead = 0;
        if (readDataFraction > 0 && indexed.Count > 0)
        {
            var count = Math.Max(1, (int)Math.Ceiling(indexed.Count * Math.Min(1, readDataFraction)));
            foreach (var pack in indexed.Where(p => stored.ContainsKey(p.Id)).OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).Take(count))
            {
                packsRead++;
                var content = await repository.Backend.ReadAllBytesAsync(PackWriter.PathOf(pack.Id), ct);
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
                    catch (Exception e) when (e is CryptographicException or InvalidDataException)
                    {
                        errors.Add($"Blob {blob.Id.Short} in pack {pack.Id[..8]} cannot be decrypted: {e.Message}");
                    }
                }
            }
        }
        return new CheckResult(errors, warnings, snapshots.Count, indexed.Count, packsRead);
    }

    /// <summary>Every blob (trees and data) reachable from the snapshots.</summary>
    private async Task<HashSet<BlobId>> UsedBlobsAsync(List<Snapshot> snapshots, CancellationToken ct, List<string>? errors = null)
    {
        var used = new HashSet<BlobId>();
        var stack = new Stack<BlobId>(snapshots.Select(s => s.Tree));
        while (stack.TryPop(out var treeId))
        {
            if (!used.Add(treeId)) continue; // a shared subtree is walked once
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
