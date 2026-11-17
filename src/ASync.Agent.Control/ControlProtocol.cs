using System.Text.Json;
using System.Text.Json.Serialization;
using ASync.Contracts;

namespace ASync.Agent.Control;

/// <summary>
/// The agent's control protocol: one JSON object per line (UTF-8, <c>\n</c>) in both directions over a Unix domain
/// socket or a named pipe (see <see cref="ControlEndpoint"/>). The client sends <see cref="ControlRequest"/>s and gets one
/// <see cref="ControlResponse"/> each, with the same <c>id</c>, in order. After <c>subscribe</c> the connection carries
/// only <see cref="ControlEvent"/>s until either side closes it.
/// <para><b>Versioning:</b> every message carries <c>v</c>, the protocol version. Within a version only optional fields
/// and new operations are added; clients ignore fields they do not know (System.Text.Json does by default) and check
/// <see cref="HelloDto.Protocol"/> before using an operation newer than 1. A request with a higher <c>v</c> than the
/// agent speaks is answered with an error naming the agent's version, so an old agent tells a new app to update it.</para>
/// </summary>
public static class ControlProtocol
{
    public const int Version = 1;

    /// <summary>Longest line either side accepts (a status of thousands of folders still fits).</summary>
    public const int MaxLineBytes = 1 << 20;

    public static class Ops
    {
        /// <summary>Protocol and agent version, process id, mode. Result: <see cref="ControlResponse.Hello"/>.</summary>
        public const string Hello = "hello";
        /// <summary>Device, server, connection and every folder's state. Result: <see cref="ControlResponse.Status"/>.</summary>
        public const string Status = "status";
        /// <summary>Run one folder (<c>folderId</c>) or all now and wait for the results: <see cref="ControlResponse.Results"/>.</summary>
        public const string Sync = "sync";
        public const string Pause = "pause";
        public const string Resume = "resume";
        /// <summary>Move a folder on this device (<c>folderId</c>, <c>path</c>).</summary>
        public const string SetPath = "set-path";
        /// <summary>Unlock an end-to-end folder with its passphrase or recovery key (<c>folderId</c>, <c>secret</c>).</summary>
        public const string Unlock = "unlock";
        /// <summary>Turn the connection into an event stream: a <c>status</c> event at once, then every change.</summary>
        public const string Subscribe = "subscribe";

        // Added for the desktop app (still protocol 1: an older agent answers "Unknown operation").

        /// <summary>
        /// Enroll this device with an add-device code (<c>server</c>, <c>code</c>, <c>name</c>, <c>platform</c>). The agent
        /// keeps the new identity and starts syncing. Result: <see cref="ControlResponse.Status"/>.
        /// </summary>
        public const string Enroll = "enroll";
        /// <summary>
        /// Start the device-code flow (<c>server</c>, <c>name</c>, <c>platform</c>): the result's
        /// <see cref="ControlResponse.Enrollment"/> has the code to show and the approval link; the agent polls the server
        /// and starts by itself once someone approves it (<see cref="AgentStatusDto.Enrollment"/> tracks the request).
        /// </summary>
        public const string DeviceCode = "device-code";
        /// <summary>Forget this device's identity (sign out): stops syncing, deletes the identity and cancels a pending enrollment.</summary>
        public const string Forget = "forget";

        // Added for background work (M4-2, still protocol 1).

        /// <summary>Set a folder's run conditions on this device (<c>folderId</c>, <c>conditions</c>); the server keeps them.</summary>
        public const string SetConditions = "set-conditions";
        /// <summary>Stop the running run of a folder (<c>folderId</c>) or of all; they continue when next due.</summary>
        public const string Cancel = "cancel";
        /// <summary>
        /// Set up a new end-to-end encrypted folder with a passphrase (<c>folderId</c>, <c>secret</c>); the answer's
        /// <see cref="ControlResponse.RecoveryKey"/> is its recovery key, shown to the person once.
        /// </summary>
        public const string InitEncryption = "init-encryption";

        // Added for browsing from the web UI (task 17, still protocol 1).

        /// <summary>
        /// Allow or forbid the web UI to browse this device's folders (<c>enabled</c>); the agent keeps it in its config
        /// directory and tells the server. <see cref="AgentStatusDto.Browsing"/> shows the setting.
        /// </summary>
        public const string SetBrowsing = "set-browsing";

        // Added for adding folders from the apps (task 18, still protocol 1).

        /// <summary>
        /// What this device may do about folders: <see cref="ControlResponse.FolderOptions"/> (the storage back-ends it may
        /// create folders on, the owner's folders it could join, or why it may not).
        /// </summary>
        public const string FolderOptions = "folder-options";
        /// <summary>
        /// Create a folder of the owner with this device attached (<c>newFolder</c>). The answer's <see cref="ControlResponse.FolderId"/>
        /// is the new folder, <see cref="ControlResponse.RecoveryKey"/> a Transport Backup folder's recovery key (shown once).
        /// </summary>
        public const string CreateFolder = "create-folder";
        /// <summary>Attach this device to one of the owner's folders (<c>folderId</c>, <c>path</c>, <c>direction</c> for Mirror).</summary>
        public const string JoinFolder = "join-folder";
        /// <summary>Detach this device from a folder (<c>folderId</c>); the folder and its data stay.</summary>
        public const string LeaveFolder = "leave-folder";

        // Added for photo backups (task 19, still protocol 1). A Photos folder's join-folder sends media instead of path.

        /// <summary>What this device may read of its photo library, and its albums: <see cref="ControlResponse.MediaLibrary"/>.</summary>
        public const string MediaLibrary = "media-library";
        /// <summary>Change which photos and videos a Photos folder backs up on this device (<c>folderId</c>, <c>media</c>).</summary>
        public const string SetMedia = "set-media";
        /// <summary>
        /// Start restoring a Photos folder's snapshot (<c>folderId</c>, <c>snapshot</c>: an id, or this device's newest when absent) into
        /// the photo library. It runs in the agent; <see cref="AgentStatusDto.MediaRestore"/> follows it.
        /// </summary>
        public const string RestoreMedia = "restore-media";
        /// <summary>A Photos folder's snapshots to restore from (<c>folderId</c>): <see cref="ControlResponse.MediaSnapshots"/>, newest first.</summary>
        public const string MediaSnapshots = "media-snapshots";

        // Added for contacts and calendars (task 20, still protocol 1). Their join-folder sends collections instead of path.

        /// <summary>The device's contact accounts or calendars (<c>kind</c>): <see cref="ControlResponse.Collections"/>.</summary>
        public const string CollectionList = "collection-list";
        /// <summary>Change which accounts or calendars a Contacts or Calendars folder backs up here (<c>folderId</c>, <c>collections</c>).</summary>
        public const string SetCollections = "set-collections";
        /// <summary>A Contacts or Calendars folder's snapshots to restore from (<c>folderId</c>): <see cref="ControlResponse.MediaSnapshots"/>.</summary>
        public const string CollectionSnapshots = "collection-snapshots";
        /// <summary>
        /// Start importing a Contacts or Calendars snapshot (<c>folderId</c>, <c>snapshot</c>, <c>target</c>: the account or calendar to
        /// import into, absent = each into its own). <see cref="AgentStatusDto.CollectionRestore"/> follows it.
        /// </summary>
        public const string RestoreCollections = "restore-collections";
    }
}

public sealed record ControlRequest
{
    public int V { get; init; } = ControlProtocol.Version;
    public int Id { get; init; }
    public string Op { get; init; } = "";
    public int? FolderId { get; init; }
    public string? Path { get; init; }
    /// <summary>The passphrase or recovery key of <c>unlock</c>. Never logged.</summary>
    public string? Secret { get; init; }
    /// <summary><c>sync</c>: let this run delete more than half of a folder's files.</summary>
    public bool AllowMassDelete { get; init; }
    /// <summary><c>enroll</c>, <c>device-code</c>: the server's address.</summary>
    public string? Server { get; init; }
    /// <summary><c>enroll</c>: the add-device code. A one-time secret: never logged.</summary>
    public string? Code { get; init; }
    /// <summary><c>enroll</c>, <c>device-code</c>: this device's name.</summary>
    public string? Name { get; init; }
    /// <summary><c>enroll</c>, <c>device-code</c>: what to register the device as (default Cli).</summary>
    public DevicePlatform? Platform { get; init; }
    /// <summary><c>set-conditions</c>: the folder's new run conditions.</summary>
    public RunConditionsDto? Conditions { get; init; }
    /// <summary><c>set-browsing</c>: allow browsing from the web UI.</summary>
    public bool? Enabled { get; init; }
    /// <summary><c>create-folder</c>: the folder and where it lives on this device.</summary>
    public DeviceCreateFolderRequest? NewFolder { get; init; }
    /// <summary><c>join-folder</c>: which way a Mirror folder copies on this device.</summary>
    public MirrorDirection? Direction { get; init; }
    /// <summary><c>set-media</c>, <c>join-folder</c> of a Photos folder: which photos and videos it backs up.</summary>
    public MediaSelectionDto? Media { get; init; }
    /// <summary><c>restore-media</c>, <c>restore-collections</c>: the snapshot (id, or a prefix or suffix of one).</summary>
    public string? Snapshot { get; init; }
    /// <summary><c>set-collections</c>, <c>join-folder</c> of a Contacts or Calendars folder: which accounts or calendars it backs up.</summary>
    public CollectionSelectionDto? Collections { get; init; }
    /// <summary><c>collection-list</c>: Contacts or Calendars.</summary>
    public FolderKind? Kind { get; init; }
    /// <summary><c>restore-collections</c>: the account or calendar to import into (null: each into its own).</summary>
    public string? Target { get; init; }
}

public sealed record ControlResponse
{
    public int V { get; init; } = ControlProtocol.Version;
    public int Id { get; init; }
    public bool Ok { get; init; }
    public string? Error { get; init; }
    /// <summary>What happened, for people (pause, resume, set-path, unlock).</summary>
    public string? Message { get; init; }
    public HelloDto? Hello { get; init; }
    public AgentStatusDto? Status { get; init; }
    public List<FolderRunDto>? Results { get; init; }
    /// <summary><c>device-code</c>: the code to show and the link to approve it.</summary>
    public PendingEnrollmentDto? Enrollment { get; init; }
    /// <summary><c>init-encryption</c>: the new folder's recovery key (shown once; never logged).</summary>
    public string? RecoveryKey { get; init; }
    /// <summary><c>folder-options</c>: what this device may do about folders.</summary>
    public DeviceFolderOptionsDto? FolderOptions { get; init; }
    /// <summary><c>create-folder</c>, <c>join-folder</c>: the folder.</summary>
    public int? FolderId { get; init; }
    /// <summary><c>media-library</c>: the photo library.</summary>
    public MediaLibraryDto? MediaLibrary { get; init; }
    /// <summary><c>media-snapshots</c>: the snapshots a restore can use.</summary>
    public List<MediaSnapshotDto>? MediaSnapshots { get; init; }
    /// <summary><c>collection-list</c>: the accounts or calendars.</summary>
    public CollectionListDto? Collections { get; init; }
}

/// <summary>
/// The device's contact accounts or calendars as the agent sees them (task 20). <paramref name="CanWrite"/>: a restore may add to them
/// now (WRITE_CONTACTS / WRITE_CALENDAR granted); <paramref name="Problem"/>: why they cannot be read, for people.
/// </summary>
public sealed record CollectionListDto(FolderKind Kind, MediaAccess Access, List<CollectionDto> Collections, int Items = 0, bool CanWrite = false,
    string? Problem = null);

/// <summary>A Photos folder's snapshot for the restore's choice: <paramref name="Mine"/> = made by this device; <paramref name="Files"/>: originals and sidecars.</summary>
public sealed record MediaSnapshotDto(string Id, string ShortId, DateTimeOffset Time, string? Device, bool Mine, int Files);

/// <summary>
/// The device's photo library as the agent sees it (task 19). <paramref name="Access"/>: what the permissions allow;
/// <paramref name="Problem"/>: why it cannot be read, for people.
/// </summary>
public sealed record MediaLibraryDto(MediaAccess Access, List<MediaAlbumDto> Albums, int Photos = 0, int Videos = 0, string? Problem = null);

/// <summary>A restore of a Photos snapshot into the library: running (<paramref name="Done"/> of <paramref name="Total"/>) or finished (<paramref name="Message"/>).</summary>
public sealed record MediaRestoreDto(int FolderId, string Snapshot, bool Running, int Done, int Total, int Restored, int Skipped, string? Current,
    string? Message = null, bool Ok = true);

/// <summary>A line of the event stream.</summary>
public sealed record ControlEventMessage
{
    public int V { get; init; } = ControlProtocol.Version;
    public ControlEvent Event { get; init; } = new();
}

public static class ControlEventKinds
{
    /// <summary>Everything changed or the subscription just started: <see cref="ControlEvent.Status"/> is set.</summary>
    public const string Status = "status";
    /// <summary>One folder's state, progress or last result changed: <see cref="ControlEvent.Folder"/> is set.</summary>
    public const string Folder = "folder";
    /// <summary>A run finished: <see cref="ControlEvent.Folder"/> and <see cref="ControlEvent.Result"/> are set.</summary>
    public const string RunFinished = "run-finished";
}

public sealed record ControlEvent
{
    public string Kind { get; init; } = ControlEventKinds.Status;
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public AgentStatusDto? Status { get; init; }
    public FolderStatusDto? Folder { get; init; }
    public FolderRunDto? Result { get; init; }
}

/// <summary>A folder this device created or joined: its id, what happened (for people) and a new Transport Backup folder's recovery key.</summary>
public sealed record FolderAddedDto(int FolderId, string Message, string? RecoveryKey = null);

public sealed record HelloDto(int Protocol, string AgentVersion, int ProcessId, AgentMode Mode, string ConfigDirectory);

/// <summary>
/// How the agent was started: the foreground <c>async-cli run</c>, as a service (systemd, Windows, launchd), or inside a
/// mobile app's process (<c>InApp</c>: reached in-process only, never over the channel, so older clients never see it).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentMode>))]
public enum AgentMode { Foreground, Service, InApp }

[JsonConverter(typeof(JsonStringEnumConverter<AgentState>))]
public enum AgentState
{
    /// <summary>No identity in the config directory yet: enroll (CLI or app); the agent starts by itself afterwards.</summary>
    NotEnrolled,
    /// <summary>Signing in and connecting live updates.</summary>
    Connecting,
    /// <summary>Signed in, live updates (SignalR) connected.</summary>
    Connected,
    /// <summary>Live updates are down and being retried; folders still run on their schedule.</summary>
    Reconnecting,
    /// <summary>The server is unreachable; folders keep trying on their schedule.</summary>
    Offline,
    /// <summary>The server removed this device. Enroll again (after <c>async-cli forget</c>).</summary>
    Revoked,
    Stopping,
}

[JsonConverter(typeof(JsonStringEnumConverter<FolderActivity>))]
public enum FolderActivity { Idle, Syncing, BackingUp, Maintenance, Paused }

public sealed record AgentStatusDto
{
    public AgentState State { get; init; }
    public string AgentVersion { get; init; } = "";
    public AgentMode Mode { get; init; }
    public int ProcessId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DeviceInfoDto? Device { get; init; }
    /// <summary>The last connection problem (or why the device was revoked).</summary>
    public string? Problem { get; init; }
    public List<FolderStatusDto> Folders { get; init; } = [];
    /// <summary>A device-code enrollment waiting for approval (or the reason it ended without one).</summary>
    public PendingEnrollmentDto? Enrollment { get; init; }
    /// <summary>Browsing this device's folders from the web UI (null: an older agent without it).</summary>
    public BrowsingStatusDto? Browsing { get; init; }
    /// <summary>What the agent may read of the device's photo library (null: an agent without photo backups, or none here).</summary>
    public MediaAccess? Media { get; init; }
    /// <summary>The last (or running) restore into the photo library.</summary>
    public MediaRestoreDto? MediaRestore { get; init; }
    /// <summary>What the agent may read of the device's contacts and calendars (null: none here, or an older agent).</summary>
    public MediaAccess? Contacts { get; init; }
    public MediaAccess? Calendars { get; init; }
    /// <summary>The last (or running) import of a Contacts or Calendars snapshot into the phone.</summary>
    public MediaRestoreDto? CollectionRestore { get; init; }
}

/// <summary>
/// Whether the web UI may browse this device's folders. <paramref name="Allowed"/>: the setting in effect;
/// <paramref name="Locked"/>: forced off where the agent was started (<c>--no-browse</c>, <c>ASYNC_ALLOW_BROWSE=0</c>), so
/// the app cannot turn it on; <paramref name="Unavailable"/>: why it cannot work now anyway (a phone without All files access).
/// </summary>
public sealed record BrowsingStatusDto(bool Allowed, bool Locked = false, string? Unavailable = null);

/// <summary>
/// A device-code request the agent is polling: show <see cref="UserCode"/> and open <see cref="VerificationUri"/>.
/// <see cref="Error"/> is set when it was denied, expired or failed; the next enrollment clears it.
/// </summary>
public sealed record PendingEnrollmentDto(string Server, string Name, string UserCode, string VerificationUri, DateTimeOffset ExpiresAt, string? Error = null);

/// <summary>An open problem the server sees with a folder on this device (see <c>GET /api/sync/folders/alerts</c>).</summary>
public sealed record FolderAlertDto(AlertKind Kind, string Detail, DateTimeOffset OpenedAt);

public sealed record DeviceInfoDto(Guid DeviceId, string DeviceName, string Username, string ServerUrl);

public sealed record FolderStatusDto
{
    public int FolderId { get; init; }
    public string Name { get; init; } = "";
    public SyncMode Mode { get; init; }
    public MirrorDirection? Direction { get; init; }
    public FolderEncryption Encryption { get; init; }
    public string LocalPath { get; init; } = "";
    /// <summary>Files, or Photos (task 19: <see cref="Media"/> instead of a local path).</summary>
    public FolderKind Kind { get; init; }
    /// <summary>A Photos folder's selection on this device.</summary>
    public MediaSelectionDto? Media { get; init; }
    /// <summary>A Photos folder's last backup: what it holds.</summary>
    public MediaCountsDto? MediaCounts { get; init; }
    /// <summary>A Contacts or Calendars folder's selection on this device (task 20), and what its last backup holds.</summary>
    public CollectionSelectionDto? Collections { get; init; }
    public CollectionCountsDto? CollectionCounts { get; init; }
    public int IntervalMinutes { get; init; }
    public bool Paused { get; init; }
    /// <summary>End-to-end and not unlocked on this device (or not set up yet): it cannot run.</summary>
    public bool Locked { get; init; }
    /// <summary>End-to-end and not set up yet: a device must create its repository with a passphrase (<c>init-encryption</c>).</summary>
    public bool NeedsSetup { get; init; }
    public FolderActivity Activity { get; init; }
    /// <summary>Set while <see cref="Activity"/> is Syncing or BackingUp.</summary>
    public ProgressDto? Progress { get; init; }
    public FolderRunDto? LastResult { get; init; }
    public DateTimeOffset? NextRunAt { get; init; }
    /// <summary>What the server's overdue detection reports for this folder and device (null: nothing, or not known yet).</summary>
    public List<FolderAlertDto>? Alerts { get; init; }
    /// <summary>This device's run conditions for the folder (null: an older agent, or none).</summary>
    public RunConditionsDto? Conditions { get; init; }
    /// <summary>Why automatic runs wait now ("Waiting for Wi-Fi…"), or null.</summary>
    public string? Waiting { get; init; }
}

/// <summary>
/// A run's progress. Totals are known for Mirror/TwoWay after the plan is made (files to transfer or delete, bytes to
/// transfer); a backup does not count its folder first, so its totals stay empty.
/// </summary>
public sealed record ProgressDto(int Files, int? TotalFiles, long Bytes, long? TotalBytes, string? CurrentFile);

/// <summary>A run's result. <paramref name="Conflicts"/>: conflict copies the run made (TwoWay).</summary>
/// <summary>A run's outcome. <paramref name="Deferred"/>: it did not run (conditions, or stopped) and runs again when next due.</summary>
public sealed record FolderRunDto(int FolderId, string Name, bool Ok, string Message, bool Skipped, DateTimeOffset At, int Conflicts = 0, bool Deferred = false);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
[JsonSerializable(typeof(ControlEventMessage))]
public sealed partial class ControlJsonContext : JsonSerializerContext;
