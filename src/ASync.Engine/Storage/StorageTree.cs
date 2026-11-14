namespace ASync.Engine.Storage;

/// <summary>Deleting a whole directory tree on a back-end (purging a deleted folder's data).</summary>
public static class StorageTree
{
    /// <param name="Files">Files deleted.</param>
    /// <param name="Bytes">Their total size.</param>
    /// <param name="Left">Files still there after the last pass (written meanwhile, or not deletable).</param>
    public sealed record DeleteResult(long Files, long Bytes, long Left);

    /// <summary>
    /// Deletes every file under <paramref name="root"/> and then the directories that held them. Never touches anything
    /// outside the root: the root must be a real sub-directory (not the back-end's top), and each listed path is checked
    /// to lie inside it (<c>root/…</c>, so a sibling such as <c>root-old</c> is not "inside") before it is deleted,
    /// whatever the back-end's listing returns. Runs up to <paramref name="passes"/> passes, so files written while it
    /// worked are caught too. Idempotent: running it again after a crash just carries on.
    /// </summary>
    public static async Task<DeleteResult> DeleteAsync(IStorageBackend backend, string root, IProgress<(long Files, long Bytes)>? progress = null,
        int passes = 2, CancellationToken ct = default)
    {
        var prefix = StoragePath.Normalize(root);
        if (prefix.Length == 0) throw new ArgumentException("Refusing to delete the whole storage: a folder root is required.", nameof(root));
        long files = 0, bytes = 0, left = 0;
        var directories = new HashSet<string> { prefix };

        for (var pass = 0; pass < passes; pass++)
        {
            var batch = new List<StorageEntry>();
            await foreach (var entry in backend.ListAsync(prefix, recursive: true, ct))
                if (!entry.IsDirectory && IsInside(entry.Path, prefix)) batch.Add(entry);
            left = batch.Count;
            if (batch.Count == 0) break;

            foreach (var entry in batch)
            {
                ct.ThrowIfCancellationRequested();
                await backend.DeleteAsync(entry.Path, ct);
                files++;
                bytes += entry.Size;
                left--;
                for (var dir = StoragePath.Parent(StoragePath.Normalize(entry.Path)); dir.Length > prefix.Length; dir = StoragePath.Parent(dir))
                    directories.Add(dir);
                if (files % 200 == 0) progress?.Report((files, bytes));
            }
        }
        progress?.Report((files, bytes));

        // Directories that held no listed file (a released lock's locks/, an emptied .async/incoming…) are found by
        // walking what is left; recursive listings return files only.
        var pending = new Stack<string>([prefix]);
        var walked = new HashSet<string> { prefix };
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            await foreach (var entry in backend.ListAsync(dir, recursive: false, ct))
            {
                if (!entry.IsDirectory || !IsInside(entry.Path, prefix)) continue;
                var sub = StoragePath.Normalize(entry.Path);
                directories.Add(sub);
                if (walked.Add(sub)) pending.Push(sub);
            }
        }

        // Deepest first, so each parent is empty by the time it is reached; the root last.
        foreach (var dir in directories.OrderByDescending(d => d.Count(c => c == '/')).ThenByDescending(d => d.Length))
            await backend.DeleteDirectoryIfEmptyAsync(dir, ct);
        return new DeleteResult(files, bytes, left);
    }

    /// <summary>Whether <paramref name="path"/> lies strictly inside the directory <paramref name="root"/>.</summary>
    public static bool IsInside(string path, string root)
    {
        string normalized;
        try { normalized = StoragePath.Normalize(path); }
        catch (ArgumentException) { return false; }
        var r = StoragePath.Normalize(root);
        return r.Length > 0 && normalized.StartsWith(r + "/", StringComparison.Ordinal) && normalized.Length > r.Length + 1;
    }
}
