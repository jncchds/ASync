using ASync.Agent.Control;
using ASync.Contracts;

namespace ASync.App.Services;

/// <summary>
/// Finds the sync agent this app shows and steers. On the desktop that is the background service, reached over the local
/// control channel (<see cref="ControlAgentConnector"/>); a mobile head can hand out an agent that runs in the app's own
/// process. The app itself never syncs.
/// </summary>
public interface IAgentConnector
{
    /// <summary>A connection to the running agent, or null when none answers.</summary>
    Task<IAgentSession?> ConnectAsync(CancellationToken ct);
}

/// <summary>One connection to an agent: requests, and the event stream on a connection of its own.</summary>
public interface IAgentSession : IAsyncDisposable
{
    HelloDto Hello { get; }

    Task<AgentStatusDto> StatusAsync(CancellationToken ct);

    /// <summary>A full status first, then every change, until the agent stops (the enumeration ends) or <paramref name="ct"/> is cancelled.</summary>
    IAsyncEnumerable<ControlEvent> SubscribeAsync(CancellationToken ct);

    Task<List<FolderRunDto>> SyncAsync(int? folderId, CancellationToken ct);
    Task<string> SetPausedAsync(int folderId, bool paused, CancellationToken ct);
    Task<string> SetPathAsync(int folderId, string path, CancellationToken ct);

    /// <summary>The passphrase or recovery key goes to the agent and nowhere else (not logged, not kept).</summary>
    Task<string> UnlockAsync(int folderId, string secret, CancellationToken ct);

    Task<AgentStatusDto> EnrollAsync(string server, string code, string name, DevicePlatform platform, CancellationToken ct);
    Task<PendingEnrollmentDto> StartDeviceCodeAsync(string server, string name, DevicePlatform platform, CancellationToken ct);
    Task<string> ForgetAsync(CancellationToken ct);

    /// <summary>A folder's run conditions on this device (only on Wi-Fi, while charging, not on a low battery).</summary>
    Task<string> SetConditionsAsync(int folderId, RunConditionsDto conditions, CancellationToken ct) =>
        throw new ControlException("This agent cannot change run conditions.");

    /// <summary>Sets up a new end-to-end folder with a passphrase; returns its recovery key (shown once).</summary>
    Task<string> InitEncryptionAsync(int folderId, string passphrase, CancellationToken ct) =>
        throw new ControlException("This agent cannot set up encryption.");

    /// <summary>Stops the running run of a folder, or of all (null).</summary>
    Task<string> CancelAsync(int? folderId, CancellationToken ct) => throw new ControlException("This agent cannot stop runs.");

    /// <summary>Allows or forbids browsing this device's folders from the web UI (the agent keeps it and tells the server).</summary>
    Task<string> SetBrowsingAsync(bool enabled, CancellationToken ct) => throw new ControlException("This agent cannot change browsing.");

    /// <summary>The storage back-ends this device may create folders on, the folders it could join, or why it may not (task 18).</summary>
    Task<DeviceFolderOptionsDto> FolderOptionsAsync(CancellationToken ct) => throw new ControlException(NoFolders);

    /// <summary>Creates a folder of the owner with this device attached.</summary>
    Task<FolderAddedDto> CreateFolderAsync(DeviceCreateFolderRequest request, CancellationToken ct) => throw new ControlException(NoFolders);

    /// <summary>Attaches this device to one of the owner's folders.</summary>
    Task<FolderAddedDto> JoinFolderAsync(int folderId, string path, MirrorDirection? direction, CancellationToken ct) =>
        throw new ControlException(NoFolders);

    Task<string> LeaveFolderAsync(int folderId, CancellationToken ct) => throw new ControlException(NoFolders);

    /// <summary>The device's photo library: what may be read, and its albums (task 19).</summary>
    Task<MediaLibraryDto> MediaLibraryAsync(CancellationToken ct) => throw new ControlException(NoPhotos);

    /// <summary>Which photos and videos a Photos folder backs up on this device.</summary>
    Task<string> SetMediaAsync(int folderId, MediaSelectionDto media, CancellationToken ct) => throw new ControlException(NoPhotos);

    /// <summary>Attaches this device to one of the owner's Photos folders.</summary>
    Task<FolderAddedDto> JoinPhotosAsync(int folderId, MediaSelectionDto media, CancellationToken ct) => throw new ControlException(NoPhotos);

    /// <summary>A Photos folder's snapshots, newest first (the restore's choice).</summary>
    Task<List<MediaSnapshotDto>> MediaSnapshotsAsync(int folderId, CancellationToken ct) => throw new ControlException(NoPhotos);

    /// <summary>Starts restoring a Photos snapshot (null: this device's newest) into the photo library.</summary>
    Task<string> RestoreMediaAsync(int folderId, string? snapshot, CancellationToken ct) => throw new ControlException(NoPhotos);

    const string NoFolders = "This agent cannot add folders: update it (async-cli or the agent service).";
    const string NoPhotos = "This agent cannot back up photos: update it.";
}

/// <summary>The agent of a config directory, over its Unix socket or named pipe.</summary>
public sealed class ControlAgentConnector(string configDirectory) : IAgentConnector
{
    public async Task<IAgentSession?> ConnectAsync(CancellationToken ct) =>
        await ControlClient.TryConnectAsync(configDirectory, ct) is { } client ? new ControlAgentSession(client) : null;

    private sealed class ControlAgentSession(ControlClient client) : IAgentSession
    {
        public HelloDto Hello => client.Hello;
        public Task<AgentStatusDto> StatusAsync(CancellationToken ct) => client.StatusAsync(ct);
        public IAsyncEnumerable<ControlEvent> SubscribeAsync(CancellationToken ct) => client.SubscribeAsync(ct);
        public Task<List<FolderRunDto>> SyncAsync(int? folderId, CancellationToken ct) => client.SyncAsync(folderId, ct: ct);
        public Task<string> SetPausedAsync(int folderId, bool paused, CancellationToken ct) => client.SetPausedAsync(folderId, paused, ct);
        public Task<string> SetPathAsync(int folderId, string path, CancellationToken ct) => client.SetPathAsync(folderId, path, ct);
        public Task<string> UnlockAsync(int folderId, string secret, CancellationToken ct) => client.UnlockAsync(folderId, secret, ct);
        public Task<AgentStatusDto> EnrollAsync(string server, string code, string name, DevicePlatform platform, CancellationToken ct) =>
            client.EnrollAsync(server, code, name, platform, ct);
        public Task<PendingEnrollmentDto> StartDeviceCodeAsync(string server, string name, DevicePlatform platform, CancellationToken ct) =>
            client.StartDeviceCodeAsync(server, name, platform, ct);
        public Task<string> ForgetAsync(CancellationToken ct) => client.ForgetAsync(ct);
        public Task<string> SetConditionsAsync(int folderId, RunConditionsDto conditions, CancellationToken ct) => client.SetConditionsAsync(folderId, conditions, ct);
        public Task<string> CancelAsync(int? folderId, CancellationToken ct) => client.CancelAsync(folderId, ct);
        public Task<string> SetBrowsingAsync(bool enabled, CancellationToken ct) => client.SetBrowsingAsync(enabled, ct);
        public Task<string> InitEncryptionAsync(int folderId, string passphrase, CancellationToken ct) => client.InitEncryptionAsync(folderId, passphrase, ct);
        public Task<DeviceFolderOptionsDto> FolderOptionsAsync(CancellationToken ct) => client.FolderOptionsAsync(ct);
        public Task<FolderAddedDto> CreateFolderAsync(DeviceCreateFolderRequest request, CancellationToken ct) => client.CreateFolderAsync(request, ct);
        public Task<FolderAddedDto> JoinFolderAsync(int folderId, string path, MirrorDirection? direction, CancellationToken ct) =>
            client.JoinFolderAsync(folderId, path, direction, ct);
        public Task<string> LeaveFolderAsync(int folderId, CancellationToken ct) => client.LeaveFolderAsync(folderId, ct);
        public Task<MediaLibraryDto> MediaLibraryAsync(CancellationToken ct) => client.MediaLibraryAsync(ct);
        public Task<string> SetMediaAsync(int folderId, MediaSelectionDto media, CancellationToken ct) => client.SetMediaAsync(folderId, media, ct);
        public Task<FolderAddedDto> JoinPhotosAsync(int folderId, MediaSelectionDto media, CancellationToken ct) => client.JoinPhotosAsync(folderId, media, ct);
        public Task<string> RestoreMediaAsync(int folderId, string? snapshot, CancellationToken ct) => client.RestoreMediaAsync(folderId, snapshot, ct);
        public Task<List<MediaSnapshotDto>> MediaSnapshotsAsync(int folderId, CancellationToken ct) => client.MediaSnapshotsAsync(folderId, ct);
        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}

/// <summary>Whether the agent's background service exists here and runs.</summary>
public enum AgentServiceState
{
    /// <summary>This platform has no separate agent service (or the head cannot manage it).</summary>
    Unsupported,
    NotInstalled,
    /// <summary>Installed, not running.</summary>
    Stopped,
    /// <summary>The service manager says it runs, but its control channel does not answer (yet).</summary>
    Running,
}

/// <summary>Installs and starts the agent's service for the current user (the same code as <c>async-cli service install</c>).</summary>
public interface IAgentServiceManager
{
    Task<AgentServiceState> GetStateAsync(CancellationToken ct);
    /// <summary>Installs the per-user service and starts it; returns what happened, for people.</summary>
    Task<string> InstallAndStartAsync(CancellationToken ct);
    Task<string> StartAsync(CancellationToken ct);
    /// <summary>Stops the service (before an update replaces its program); it stays installed.</summary>
    Task<string> StopAsync(CancellationToken ct);
}
