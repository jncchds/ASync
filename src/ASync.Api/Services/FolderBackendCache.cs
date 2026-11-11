using System.Collections.Concurrent;
using ASync.Core.Models;
using ASync.Engine.Storage;

namespace ASync.Api.Services;

/// <summary>
/// Open back-end connections per folder, reused across gateway requests (an SFTP or SMB handshake per request would
/// be far too slow) and closed after a few idle minutes. A connection is replaced when the back-end's settings
/// change. Thread-safety of each connection is the back-end's own (SFTP/SMB serialise their operations).
/// </summary>
public sealed class FolderBackendCache(IServiceScopeFactory scopes, TimeProvider clock) : IAsyncDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);

    private sealed class Entry(IStorageBackend backend, DateTime version)
    {
        public IStorageBackend Backend { get; } = backend;
        public DateTime Version { get; } = version;
        public DateTime LastUsed { get; set; }
    }

    private readonly ConcurrentDictionary<int, Entry> _entries = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The folder's storage: its root on its back-end (inside the owner's area of a shared back-end).</summary>
    public async Task<IStorageBackend> GetAsync(Folder folder, CancellationToken ct = default)
    {
        var backend = folder.StorageBackend ?? throw new InvalidOperationException("Load the folder with its storage back-end.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (_entries.TryGetValue(folder.Id, out var entry) && entry.Version == backend.UpdatedAt)
        {
            entry.LastUsed = now;
            return entry.Backend;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_entries.TryGetValue(folder.Id, out entry) && entry.Version == backend.UpdatedAt)
            {
                entry.LastUsed = now;
                return entry.Backend;
            }
            if (entry is not null && _entries.TryRemove(folder.Id, out var stale)) await stale.Backend.DisposeAsync();

            await using var scope = scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<StorageBackendService>();
            var opened = new PrefixedBackend(service.OpenForUser(backend, folder.UserId), folder.RootPath);
            _entries[folder.Id] = new Entry(opened, backend.UpdatedAt) { LastUsed = now };
            return opened;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes connections idle for a while (called periodically).</summary>
    public async Task SweepAsync()
    {
        var cutoff = clock.GetUtcNow().UtcDateTime - IdleTimeout;
        foreach (var (id, entry) in _entries)
            if (entry.LastUsed < cutoff && _entries.TryRemove(new KeyValuePair<int, Entry>(id, entry)))
                await entry.Backend.DisposeAsync();
    }

    public async Task ForgetAsync(int folderId)
    {
        if (_entries.TryRemove(folderId, out var entry)) await entry.Backend.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _entries.Values) await entry.Backend.DisposeAsync();
        _entries.Clear();
        _gate.Dispose();
    }
}

/// <summary>Closes idle back-end connections every minute.</summary>
public class FolderBackendSweeper(FolderBackendCache cache) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await cache.SweepAsync();
    }
}
