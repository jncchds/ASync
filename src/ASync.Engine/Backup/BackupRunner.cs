using ASync.Engine.Repository;

namespace ASync.Engine.Backup;

public sealed record BackupOptions
{
    /// <summary>Label of what is backed up; retention applies per source. Defaults to the directory name.</summary>
    public string? Source { get; init; }
    public string? DeviceId { get; init; }
    /// <summary>The name recorded in the snapshot. Default: the machine name (a random id inside a container).</summary>
    public string? Hostname { get; init; }
    public IReadOnlyList<string> Excludes { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>
    /// A previous snapshot of the same source. Files with the same size and modification time are taken from it
    /// without being read again, which makes repeated backups fast.
    /// </summary>
    public Snapshot? Parent { get; init; }
}

public sealed record BackupProgress(string CurrentPath, int Files, long BytesProcessed, long BytesAdded);

/// <summary>
/// Backs a directory up into a snapshot: walks it, chunks changed files, stores new chunks and directory trees,
/// and records a snapshot. Unreadable files are reported in <see cref="SnapshotStats.Errors"/> and skipped,
/// so one locked file does not fail a whole backup. Symlinks are stored as links, never followed.
/// </summary>
public sealed class BackupRunner(Repository.Repository repository)
{
    private const int MaxReportedErrors = 100;

    public async Task<Snapshot> RunAsync(string directory, BackupOptions? options = null, IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new BackupOptions();
        var root = new DirectoryInfo(Path.GetFullPath(directory));
        if (!root.Exists) throw new DirectoryNotFoundException($"'{directory}' does not exist.");

        await using var repoLock = await repository.LockAsync(exclusive: false, $"backup of {root.FullName}", ct);
        await repository.LoadIndexAsync(ct); // dedup against everything stored so far, by anyone
        var excludes = new Glob(options.Excludes);
        var stats = new SnapshotStats();
        var lastRefresh = DateTimeOffset.UtcNow;

        async Task RefreshLockIfDueAsync()
        {
            if (DateTimeOffset.UtcNow - lastRefresh < TimeSpan.FromMinutes(5)) return;
            lastRefresh = DateTimeOffset.UtcNow;
            await repoLock.RefreshAsync(ct);
        }

        void Error(string path, Exception e)
        {
            if (stats.Errors.Count < MaxReportedErrors) stats.Errors.Add($"{path}: {e.Message}");
        }

        async Task<BlobId> BackupDirectoryAsync(DirectoryInfo dir, string relative, BlobId? parentTreeId)
        {
            Dictionary<string, TreeNode> previous = [];
            if (parentTreeId is BlobId pid)
            {
                try { previous = (await repository.LoadTreeAsync(pid, ct)).Nodes.ToDictionary(n => n.Name, StringComparer.Ordinal); }
                catch (Exception e) when (e is not OperationCanceledException) { Error(relative, e); }
            }

            var nodes = new List<TreeNode>();
            IEnumerable<FileSystemInfo> children;
            try { children = dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error(relative, e); children = []; }

            foreach (var child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var childPath = relative.Length == 0 ? child.Name : $"{relative}/{child.Name}";
                if (child.Name.EndsWith(".async-tmp", StringComparison.Ordinal) || excludes.Matches(childPath)) continue;
                previous.TryGetValue(child.Name, out var old);
                try
                {
                    if (child.LinkTarget is { } target)
                    {
                        nodes.Add(new TreeNode { Name = child.Name, Type = NodeType.Symlink, LinkTarget = target, Modified = Ms(child.LastWriteTimeUtc) });
                    }
                    else if (child is DirectoryInfo sub)
                    {
                        var subtree = await BackupDirectoryAsync(sub, childPath, old?.Type == NodeType.Directory ? old.Subtree : null);
                        stats.Directories++;
                        nodes.Add(new TreeNode { Name = child.Name, Type = NodeType.Directory, Subtree = subtree, Modified = Ms(sub.LastWriteTimeUtc), Mode = ModeOf(sub) });
                    }
                    else if (child is FileInfo file)
                    {
                        nodes.Add(await BackupFileAsync(file, childPath, old));
                        progress?.Report(new BackupProgress(childPath, stats.FilesNew + stats.FilesChanged + stats.FilesUnmodified, stats.BytesProcessed, stats.BytesAdded));
                        await RefreshLockIfDueAsync();
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Error(childPath, e);
                    // Keep the previous version rather than dropping a file that is only temporarily unreadable.
                    if (old is not null) nodes.Add(old);
                }
            }
            return await repository.SaveTreeAsync(new Tree(nodes), ct);
        }

        async Task<TreeNode> BackupFileAsync(FileInfo file, string relative, TreeNode? old)
        {
            var modified = Ms(file.LastWriteTimeUtc);
            if (old is { Type: NodeType.File, Content: not null } && old.Size == file.Length && old.Modified == modified
                && old.Content.All(repository.HasBlob))
            {
                stats.FilesUnmodified++;
                return old with { Mode = ModeOf(file) };
            }

            var content = new List<BlobId>();
            long size = 0;
            await using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true))
            {
                await foreach (var chunk in repository.Chunker.SplitAsync(stream, ct))
                {
                    var (id, stored) = await repository.SaveBlobAsync(BlobType.Data, chunk, ct);
                    content.Add(id);
                    size += chunk.Length;
                    stats.BytesProcessed += chunk.Length;
                    if (stored > 0)
                    {
                        stats.BytesAdded += chunk.Length;
                        stats.BytesStored += stored;
                    }
                }
            }
            if (old is null) stats.FilesNew++; else stats.FilesChanged++;
            return new TreeNode { Name = file.Name, Type = NodeType.File, Size = size, Modified = modified, Mode = ModeOf(file), Content = content };
        }

        var treeId = await BackupDirectoryAsync(root, "", options.Parent?.Tree);
        await repository.FlushAsync(ct); // data before the snapshot that refers to it

        var snapshot = new Snapshot
        {
            Id = Repository.Repository.NewSnapshotId(),
            Time = DateTimeOffset.UtcNow,
            Tree = treeId,
            Source = options.Source ?? root.Name,
            DeviceId = options.DeviceId,
            Hostname = options.Hostname ?? Environment.MachineName,
            Parent = options.Parent?.Id,
            Tags = [.. options.Tags],
            Stats = stats,
        };
        await repository.SaveSnapshotAsync(snapshot, ct);
        return snapshot;
    }

    internal static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static int? ModeOf(FileSystemInfo info) => OperatingSystem.IsWindows() ? null : (int)info.UnixFileMode;
}
