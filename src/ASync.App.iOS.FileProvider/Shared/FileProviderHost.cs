using ASync.Client.Core;
using ASync.Client.Core.Documents;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Storage;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.App.iOS.FileProvider;

/// <summary>What the extension needs of the server, as the device itself (the gateway, the repository key, the folder list).</summary>
public interface IFileProviderServer
{
    Task<List<DeviceFolderDto>> FoldersAsync(CancellationToken ct);
    IStorageBackend Backend(DeviceFolderDto folder);
    Task<Repo> OpenRepositoryAsync(DeviceFolderDto folder, IStorageBackend backend, CancellationToken ct);
    Task NotifyChangedAsync(int folderId, CancellationToken ct);
}

/// <summary>
/// The server through the device's own client: the extension signs the device's challenges itself, with the key it shares
/// with the app through the Keychain group, and keeps the access token in the shared store's token.json (so the app and the
/// extension rarely sign in twice). No new endpoint or credential: every request is the device's own.
/// </summary>
public sealed class ApiFileProviderServer(AsyncApiClient api, IClientStore store) : IFileProviderServer
{
    public AsyncApiClient Api => api;
    public Task<List<DeviceFolderDto>> FoldersAsync(CancellationToken ct) => api.GetFoldersAsync(ct);
    public IStorageBackend Backend(DeviceFolderDto folder) => new RemoteStorageBackend(api, folder.FolderId);
    public Task<Repo> OpenRepositoryAsync(DeviceFolderDto folder, IStorageBackend backend, CancellationToken ct) => RepositoryAccess.OpenAsync(api, store, folder, backend, ct);
    public Task NotifyChangedAsync(int folderId, CancellationToken ct) => api.NotifyChangedAsync(folderId, ct);
}

/// <summary>
/// The catalog's view of the device inside the extension, which has no agent: the identity from the shared store, the
/// account and the folder list as the app last wrote them (<see cref="SharedAccount"/>: an answer at once, also offline),
/// refreshed from the server when older than <see cref="FoldersTtl"/>. "Removed from the account" comes from the app's file or
/// from the server refusing the device's signature here. A write asks the app to sync the folder's local copy
/// (<paramref name="written"/>: a note in the group and a Darwin notification), as Android's provider triggers the agent.
/// </summary>
public sealed class FileProviderHost(
    Func<DeviceIdentity?> identity,
    Func<SharedAccount?> shared,
    Func<IFileProviderServer?> server,
    Action<int> written,
    TimeProvider? clock = null) : IDocumentsHost
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private (DateTimeOffset At, List<DeviceFolderDto> Folders, Guid Device)? _fetched;
    private Task? _refreshing;
    private string? _revoked;

    /// <summary>How long a folder list is used before it is fetched again.</summary>
    public TimeSpan FoldersTtl { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The last refresh's problem (logged by the extension).</summary>
    public string? LastProblem { get; private set; }

    public DocumentsAccount Account
    {
        get
        {
            var id = identity();
            if (id is null) return new DocumentsAccount(false, "ASync");
            var title = SharedAccount.DomainTitle(id.ServerUrl);
            var account = shared();
            var revoked = _revoked ?? (account is { } a && a.DeviceId == id.DeviceId ? a.Revoked : null);
            return new DocumentsAccount(true, title, id.Username,
                revoked is null ? null : $"This iPhone was removed from {id.Username}'s account. Open ASync to enroll it again.");
        }
    }

    /// <summary>The server refused the device (its key no longer enrolled): every request answers "not authenticated".</summary>
    public void Saw(Exception e)
    {
        if (e is DeviceRevokedException) _revoked = e.Message;
    }

    public IReadOnlyList<DeviceFolderDto>? Folders
    {
        get
        {
            var id = identity();
            if (id is null) return null;
            var now = _clock.GetUtcNow();
            var account = shared();
            var fromApp = account is { } a && a.DeviceId == id.DeviceId ? a : null;
            (DateTimeOffset At, List<DeviceFolderDto> Folders, Guid Device)? fetched;
            lock (_gate) fetched = _fetched is { } f && f.Device == id.DeviceId ? f : null;
            var newest = fetched is { } mine && (fromApp is null || mine.At >= fromApp.At)
                ? (mine.At, (IReadOnlyList<DeviceFolderDto>?)mine.Folders)
                : fromApp is not null ? (fromApp.At, fromApp.Folders) : (DateTimeOffset.MinValue, null);
            if (now - newest.Item1 >= FoldersTtl) Refresh(id.DeviceId);
            return newest.Item2;
        }
    }

    /// <summary>Fetches the folder list in the background (one fetch at a time).</summary>
    private void Refresh(Guid device)
    {
        if (server() is not { } s) return;
        lock (_gate)
        {
            if (_refreshing is { IsCompleted: false }) return;
            _refreshing = Task.Run(async () =>
            {
                try
                {
                    var folders = await s.FoldersAsync(CancellationToken.None);
                    lock (_gate) _fetched = (_clock.GetUtcNow(), folders, device);
                    LastProblem = null;
                }
                catch (Exception e)
                {
                    Saw(e);
                    LastProblem = DocumentCatalog.Describe(e);
                }
            });
        }
    }

    /// <summary>Waits for a refresh in progress (tests, and the extension's first listing).</summary>
    public Task Refreshing { get { lock (_gate) return _refreshing ?? Task.CompletedTask; } }

    private IFileProviderServer Server => server() ?? throw new UnauthorizedAccessException("This iPhone is not enrolled: open ASync.");

    public IStorageBackend Backend(DeviceFolderDto folder) => Server.Backend(folder);

    public Task<Repo> OpenRepositoryAsync(DeviceFolderDto folder, IStorageBackend backend, CancellationToken ct) => Server.OpenRepositoryAsync(folder, backend, ct);

    public Task NotifyChangedAsync(int folderId, CancellationToken ct) => Server.NotifyChangedAsync(folderId, ct);

    /// <summary>Only folders with a local copy on this iPhone need the app's run.</summary>
    public void SyncLocalCopy(int folderId)
    {
        if (Folders?.FirstOrDefault(f => f.FolderId == folderId) is { LocalPath.Length: > 0 }) written(folderId);
    }
}
