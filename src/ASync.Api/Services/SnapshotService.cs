using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ASync.Contracts;
using ASync.Core.Models;
using ASync.Engine;
using ASync.Engine.Repository;
using ASync.Engine.Storage;
using Microsoft.AspNetCore.DataProtection;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Api.Services;

/// <summary>
/// Read-only views of one repository's snapshots: list them, walk a directory, stream a file, ZIP a directory.
/// Engine-level, so it is tested against local repositories. Paths are relative to the snapshot root ("" = root).
/// </summary>
public sealed class SnapshotBrowser(Repo repository)
{
    public Repo Repository { get; } = repository;

    /// <summary>Snapshot ids are Guid v7 in hex; anything else is never a path into the repository.</summary>
    public static bool IsSnapshotId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigitLower);

    public Task<Snapshot> LoadSnapshotAsync(string id, CancellationToken ct = default) =>
        IsSnapshotId(id) ? Repository.LoadSnapshotAsync(id, ct) : throw new FileNotFoundException("No such snapshot.");

    /// <summary>The node at <paramref name="path"/> (the root is a nameless directory). Throws <see cref="FileNotFoundException"/>.</summary>
    public async Task<TreeNode> FindAsync(Snapshot snapshot, string path, CancellationToken ct = default)
    {
        var node = new TreeNode { Name = "", Type = NodeType.Directory, Subtree = snapshot.Tree };
        foreach (var segment in StoragePath.Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.Subtree is not BlobId sub) throw new FileNotFoundException($"\"{path}\" is not in this snapshot.");
            node = (await Repository.LoadTreeAsync(sub, ct)).Nodes.FirstOrDefault(n => n.Name == segment)
                   ?? throw new FileNotFoundException($"\"{path}\" is not in this snapshot.");
        }
        return node;
    }

    public async Task<SnapshotTreeDto> ListDirectoryAsync(Snapshot snapshot, string path, CancellationToken ct = default)
    {
        var normalized = StoragePath.Normalize(path);
        var node = await FindAsync(snapshot, normalized, ct);
        if (node.Type != NodeType.Directory || node.Subtree is not BlobId subtree)
            throw new ArgumentException($"\"{normalized}\" is a file, not a directory.");
        var nodes = (await Repository.LoadTreeAsync(subtree, ct)).Nodes
            .OrderBy(n => n.Type == NodeType.Directory ? 0 : 1).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(n => new SnapshotNodeDto(n.Name, normalized.Length == 0 ? n.Name : $"{normalized}/{n.Name}", Map(n.Type), n.Size,
                DateTimeOffset.FromUnixTimeMilliseconds(n.Modified), n.LinkTarget))
            .ToArray();
        return new SnapshotTreeDto(snapshot.Id, normalized, nodes);
    }

    private static SnapshotNodeType Map(NodeType t) => t switch
    {
        NodeType.Directory => SnapshotNodeType.Directory,
        NodeType.Symlink => SnapshotNodeType.Symlink,
        _ => SnapshotNodeType.File,
    };

    /// <summary>Writes a file's content, chunk by chunk (each chunk is verified against its id as it is read).</summary>
    public async Task CopyFileAsync(TreeNode file, Stream output, CancellationToken ct = default)
    {
        if (file.Type != NodeType.File) throw new ArgumentException($"\"{file.Name}\" is not a file.");
        foreach (var id in file.Content ?? [])
            await output.WriteAsync(await Repository.ReadBlobAsync(id, ct), ct);
    }

    /// <summary>True when every chunk of the file is in the loaded index (so a download will not fail half-way for that reason).</summary>
    public bool CanRead(TreeNode file) => (file.Content ?? []).All(id => Repository.Locate(id) is not null);

    /// <summary>
    /// Writes a directory as a ZIP: its files, and its empty directories as entries. Symlinks are left out (a ZIP
    /// cannot hold them portably). <paramref name="onFile"/> runs after each file (to refresh a lock).
    /// </summary>
    public async Task WriteZipAsync(TreeNode directory, Stream output, Func<Task>? onFile = null, CancellationToken ct = default)
    {
        if (directory.Type != NodeType.Directory || directory.Subtree is not BlobId root)
            throw new ArgumentException($"\"{directory.Name}\" is not a directory.");
        // ZipArchive still writes synchronously when an entry or the archive is closed, which a response body refuses.
        var body = new SyncWriteBuffer(output);
        var zip = await ZipArchive.CreateAsync(body, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ct);

        async Task AddAsync(BlobId treeId, string prefix)
        {
            var nodes = (await Repository.LoadTreeAsync(treeId, ct)).Nodes;
            if (nodes.Count == 0 && prefix.Length > 0) zip.CreateEntry(prefix + "/");
            foreach (var node in nodes)
            {
                ct.ThrowIfCancellationRequested();
                if (node.Name is "." or ".." || node.Name.Contains('/') || node.Name.Contains('\\'))
                    throw new InvalidDataException($"Unsafe name in snapshot: '{node.Name}'.");
                var name = prefix.Length == 0 ? node.Name : $"{prefix}/{node.Name}";
                switch (node.Type)
                {
                    case NodeType.Directory when node.Subtree is BlobId sub:
                        await AddAsync(sub, name);
                        break;
                    case NodeType.File:
                        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                        entry.LastWriteTime = ZipStreaming.Clamp(DateTimeOffset.FromUnixTimeMilliseconds(node.Modified));
                        await using (var stream = await entry.OpenAsync(ct))
                            await CopyFileAsync(node, stream, ct);
                        await body.DrainAsync(ct);
                        if (onFile is not null) await onFile();
                        break;
                }
            }
        }

        await AddAsync(root, "");
        await zip.DisposeAsync(); // the central directory
        await body.DrainAsync(ct);
        await output.FlushAsync(ct);
    }
}

/// <summary>
/// A Contacts or Calendars snapshot's account or calendar directory as one file (task 20): its vCards one after the other (a .vcf may
/// hold many), or its events in one VCALENDAR (each zone's VTIMEZONE once, then every VEVENT, named after calendar.json). What an
/// address book or calendar app imports in one go. The files are written as they are stored; nothing is re-encoded.
/// </summary>
public static class CollectionMerge
{
    public static async Task WriteAsync(SnapshotBrowser browser, TreeNode directory, bool calendars, Stream output, CancellationToken ct)
    {
        if (directory.Type != NodeType.Directory || directory.Subtree is not BlobId sub) throw new ArgumentException($"\"{directory.Name}\" is not a directory.");
        var nodes = (await browser.Repository.LoadTreeAsync(sub, ct)).Nodes.Where(n => n.Type == NodeType.File).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        async Task<string> TextAsync(TreeNode node)
        {
            using var buffer = new MemoryStream();
            await browser.CopyFileAsync(node, buffer, ct);
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        async Task WriteText(string s) => await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes(s), ct);
        if (!calendars)
        {
            foreach (var node in nodes.Where(n => n.Name.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)))
            {
                var text = await TextAsync(node);
                await WriteText(text.EndsWith('\n') ? text : text + "\r\n");
            }
            return;
        }
        string? name = null;
        if (nodes.FirstOrDefault(n => n.Name == "calendar.json") is { } info)
        {
            try
            {
                using var doc = JsonDocument.Parse(await TextAsync(info));
                name = doc.RootElement.TryGetProperty("name", out var v) ? v.GetString() : null;
            }
            catch (JsonException) { }
        }
        var zones = new Dictionary<string, string>(StringComparer.Ordinal);
        var zoneOrder = new List<string>();
        var events = new System.Text.StringBuilder();
        foreach (var node in nodes.Where(n => n.Name.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)))
        {
            var lines = (await TextAsync(node)).Replace("\r\n", "\n").Split('\n');
            System.Text.StringBuilder? block = null;
            string? kind = null, tzid = null;
            foreach (var line in lines)
            {
                if (block is null)
                {
                    if (line is "BEGIN:VTIMEZONE" or "BEGIN:VEVENT" or "BEGIN:VTODO" or "BEGIN:VJOURNAL")
                    {
                        block = new System.Text.StringBuilder();
                        kind = line[6..];
                        tzid = null;
                        block.Append(line).Append("\r\n");
                    }
                    continue;
                }
                block.Append(line).Append("\r\n");
                if (kind == "VTIMEZONE" && tzid is null && line.StartsWith("TZID:", StringComparison.Ordinal)) tzid = line[5..];
                if (line == $"END:{kind}")
                {
                    if (kind == "VTIMEZONE")
                    {
                        if (tzid is not null && zones.TryAdd(tzid, block.ToString())) zoneOrder.Add(tzid);
                    }
                    else events.Append(block);
                    block = null;
                }
            }
        }
        var head = new System.Text.StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//ASync//Calendars 1//EN\r\nCALSCALE:GREGORIAN\r\n");
        if (name is { Length: > 0 }) head.Append("X-WR-CALNAME:").Append(name.Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;").Replace("\n", "\\n")).Append("\r\n");
        await WriteText(head.ToString());
        foreach (var z in zoneOrder) await WriteText(zones[z]);
        await WriteText(events.ToString());
        await WriteText("END:VCALENDAR\r\n");
    }
}

/// <summary>Why a folder's snapshots cannot be browsed on the server, with the HTTP status to answer.</summary>
public sealed record BrowseRefusal(int Status, string Message);

/// <summary>What a download link grants: one file or directory of one snapshot, to the folder's owner.
/// <paramref name="Merge"/>: a Contacts or Calendars directory as one .vcf or .ics (<see cref="CollectionMerge"/>).</summary>
public sealed record SnapshotDownloadTicket(int UserId, int FolderId, string SnapshotId, string Path, bool Zip, bool Merge = false);

/// <summary>
/// Snapshot browsing for Transport-encrypted Backup folders, which the server can open with its own key
/// (<see cref="FolderService.ServerKey"/>). End-to-end folders are refused: only a device holds their keys.
/// <para>
/// Opened repositories are kept per folder for a few idle minutes, so walking directories does not reload the index
/// each time. The index is reloaded when a snapshot appears that was written after it was loaded (snapshots are
/// written after their data, so listing snapshot ids before loading the index tells which ones it covers), and
/// once more when a read fails because a prune moved data meanwhile.
/// </para>
/// </summary>
public sealed class SnapshotService(FolderBackendCache backends, IDataProtectionProvider dataProtection, TimeProvider clock)
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DownloadLinkLifetime = TimeSpan.FromMinutes(5);

    private sealed class Entry(IStorageBackend backend, SnapshotBrowser browser)
    {
        public IStorageBackend Backend { get; } = backend;
        public SnapshotBrowser Browser { get; } = browser;
        public HashSet<string> Covered { get; set; } = [];
        public SemaphoreSlim Reload { get; } = new(1, 1);
        public DateTime LastUsed { get; set; }
    }

    private readonly ConcurrentDictionary<int, Entry> _entries = new();
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public static BrowseRefusal? CannotBrowse(Folder folder) =>
        folder.Mode != SyncMode.Backup
            ? new BrowseRefusal(StatusCodes.Status400BadRequest, "Only Backup folders have snapshots. Mirror and two-way folders keep normal files on the storage.")
        : folder.Encryption == FolderEncryption.EndToEnd
            ? new BrowseRefusal(StatusCodes.Status409Conflict,
                $"\"{folder.Name}\" is end-to-end encrypted, so the server cannot read its backups. Browse and restore them on one of your devices: `async-cli snapshots --folder {folder.Id}` and `async-cli restore --folder {folder.Id} --target <dir>`.")
        : folder.ProtectedRepositoryKey is null
            ? new BrowseRefusal(StatusCodes.Status409Conflict, "This folder's backup repository is not set up yet.")
            : null;

    /// <summary>The folder's repository with an index that covers <paramref name="snapshotId"/> (null: just opened or current).</summary>
    public async Task<SnapshotBrowser> OpenAsync(Folder folder, Func<byte[]> serverKey, string? snapshotId = null, CancellationToken ct = default)
    {
        Sweep();
        var backend = await backends.GetAsync(folder, ct);
        if (!_entries.TryGetValue(folder.Id, out var entry) || !ReferenceEquals(entry.Backend, backend))
        {
            var repo = await Repo.OpenAsync(backend, serverKey(), ct);
            entry = new Entry(backend, new SnapshotBrowser(repo));
            await ReloadAsync(entry, ct);
            _entries[folder.Id] = entry;
        }
        else if (snapshotId is not null && !entry.Covered.Contains(snapshotId))
        {
            await ReloadAsync(entry, ct);
        }
        entry.LastUsed = Now;
        return entry.Browser;
    }

    /// <summary>Runs a read; if data went missing (a prune since the index was loaded), reloads the index and tries once more.</summary>
    public async Task<T> ReadAsync<T>(Folder folder, Func<byte[]> serverKey, string? snapshotId, Func<SnapshotBrowser, Task<T>> read, CancellationToken ct = default)
    {
        var browser = await OpenAsync(folder, serverKey, snapshotId, ct);
        try { return await read(browser); }
        catch (Exception e) when (e is BlobNotFoundException or StorageNotFoundException && _entries.TryGetValue(folder.Id, out var entry))
        {
            if (e is StorageNotFoundException && snapshotId is not null && !await browser.Repository.Backend.ExistsAsync($"snapshots/{snapshotId}", ct))
                throw new FileNotFoundException("No such snapshot.");
            await ReloadAsync(entry, ct);
            return await read(browser);
        }
    }

    private static async Task ReloadAsync(Entry entry, CancellationToken ct)
    {
        await entry.Reload.WaitAsync(ct);
        try
        {
            var covered = new HashSet<string>();
            await foreach (var e in entry.Backend.ListAsync("snapshots", ct: ct))
                if (!e.IsDirectory) covered.Add(e.Name);
            await entry.Browser.Repository.LoadIndexAsync(ct);
            entry.Covered = covered;
        }
        finally
        {
            entry.Reload.Release();
        }
    }

    /// <summary>
    /// Forgets the folder's cached repository, so the next request loads the index afresh. Called after the server
    /// pruned the folder. A prune by a device is not announced: <see cref="ReadAsync"/> notices it by a failed read.
    /// </summary>
    public void Invalidate(int folderId) => _entries.TryRemove(folderId, out _);

    /// <summary>Drops repositories nobody browsed for a while (their index can be large).</summary>
    private void Sweep()
    {
        var cutoff = Now - IdleTimeout;
        foreach (var (id, entry) in _entries)
            if (entry.LastUsed < cutoff && entry.LastUsed != default)
                _entries.TryRemove(new KeyValuePair<int, Entry>(id, entry));
    }

    // ── Download links ────────────────────────────────────────────────────────

    private ITimeLimitedDataProtector Tickets => dataProtection.CreateProtector("ASync.SnapshotDownload.v1").ToTimeLimitedDataProtector();

    public (string Ticket, DateTimeOffset ExpiresAt) IssueTicket(SnapshotDownloadTicket ticket)
    {
        var expires = clock.GetUtcNow() + DownloadLinkLifetime;
        return (Tickets.Protect(JsonSerializer.Serialize(ticket, JsonSerializerOptions.Web), expires), expires);
    }

    /// <summary>The ticket's grant, or null when it is forged or expired.</summary>
    public SnapshotDownloadTicket? ReadTicket(string ticket)
    {
        try
        {
            var json = Tickets.Unprotect(ticket, out var expires);
            return expires < clock.GetUtcNow() ? null : JsonSerializer.Deserialize<SnapshotDownloadTicket>(json, JsonSerializerOptions.Web);
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }
}
