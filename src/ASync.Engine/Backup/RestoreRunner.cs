using ASync.Engine.Repository;

namespace ASync.Engine.Backup;

public sealed record RestoreOptions
{
    /// <summary>Restore only this file or directory of the snapshot ("" = everything).</summary>
    public string SubPath { get; init; } = "";
    /// <summary>Skip files that already exist with the same size and modification time (resuming a restore).</summary>
    public bool SkipUnchanged { get; init; } = true;
}

public sealed record RestoreResult(int FilesRestored, int FilesSkipped, long BytesRestored, List<string> Errors);

/// <summary>
/// Writes (part of) a snapshot to a directory. Each file is written to a temporary name and renamed when complete,
/// with its modification time and permissions, so an interrupted restore never leaves half-written files behind.
/// </summary>
public sealed class RestoreRunner(Repository.Repository repository)
{
    public async Task<RestoreResult> RunAsync(Snapshot snapshot, string targetDirectory, RestoreOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new RestoreOptions();
        var target = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(target);
        int restored = 0, skipped = 0;
        long bytes = 0;
        var errors = new List<string>();
        await using var repoLock = await repository.LockAsync(exclusive: false, $"restore to {target}", ct);
        await repository.LoadIndexAsync(ct);

        // Find the requested node.
        var node = new TreeNode { Name = "", Type = NodeType.Directory, Subtree = snapshot.Tree };
        foreach (var segment in options.SubPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.Subtree is not BlobId sub) throw new FileNotFoundException($"'{options.SubPath}' is not in the snapshot.");
            node = (await repository.LoadTreeAsync(sub, ct)).Nodes.FirstOrDefault(n => n.Name == segment)
                   ?? throw new FileNotFoundException($"'{options.SubPath}' is not in the snapshot.");
        }

        async Task RestoreNodeAsync(TreeNode n, string path)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (n.Type)
                {
                    case NodeType.Directory:
                        Directory.CreateDirectory(path);
                        foreach (var child in (await repository.LoadTreeAsync(n.Subtree!.Value, ct)).Nodes)
                            await RestoreNodeAsync(child, Path.Combine(path, SafeName(child.Name)));
                        if (n.Name.Length > 0) Directory.SetLastWriteTimeUtc(path, FromMs(n.Modified));
                        break;
                    case NodeType.Symlink:
                        if (File.Exists(path) || Directory.Exists(path)) File.Delete(path);
                        File.CreateSymbolicLink(path, n.LinkTarget!);
                        restored++;
                        break;
                    case NodeType.File:
                        await RestoreFileAsync(n, path);
                        break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.Cryptography.CryptographicException)
            {
                errors.Add($"{path}: {e.Message}");
            }
        }

        async Task RestoreFileAsync(TreeNode n, string path)
        {
            var existing = new FileInfo(path);
            if (options.SkipUnchanged && existing.Exists && existing.Length == n.Size && BackupRunner.Ms(existing.LastWriteTimeUtc) == n.Modified)
            {
                skipped++;
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".async-tmp";
            try
            {
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    foreach (var id in n.Content ?? [])
                        await output.WriteAsync(await repository.ReadBlobAsync(id, ct), ct);
                }
                if (new FileInfo(temp).Length != n.Size) throw new InvalidDataException("Restored size does not match the snapshot.");
                File.SetLastWriteTimeUtc(temp, FromMs(n.Modified));
                if (n.Mode is int mode && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, (UnixFileMode)mode);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            restored++;
            bytes += n.Size;
        }

        var destination = node.Name.Length == 0 ? target : Path.Combine(target, SafeName(node.Name));
        await RestoreNodeAsync(node, destination);
        return new RestoreResult(restored, skipped, bytes, errors);
    }

    /// <summary>A snapshot made on another OS may hold names this one cannot use; never let a name escape the target.</summary>
    private static string SafeName(string name)
    {
        if (name is "." or ".." || name.Contains('/') || name.Contains('\\'))
            throw new InvalidDataException($"Unsafe name in snapshot: '{name}'.");
        return name;
    }

    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
