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
}

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

public sealed record HelloDto(int Protocol, string AgentVersion, int ProcessId, AgentMode Mode, string ConfigDirectory);

/// <summary>How the agent was started: the foreground <c>async-cli run</c>, or as a service (systemd, Windows, launchd).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentMode>))]
public enum AgentMode { Foreground, Service }

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
}

public sealed record DeviceInfoDto(Guid DeviceId, string DeviceName, string Username, string ServerUrl);

public sealed record FolderStatusDto
{
    public int FolderId { get; init; }
    public string Name { get; init; } = "";
    public SyncMode Mode { get; init; }
    public MirrorDirection? Direction { get; init; }
    public FolderEncryption Encryption { get; init; }
    public string LocalPath { get; init; } = "";
    public int IntervalMinutes { get; init; }
    public bool Paused { get; init; }
    /// <summary>End-to-end and not unlocked on this device (or not set up yet): it cannot run.</summary>
    public bool Locked { get; init; }
    public FolderActivity Activity { get; init; }
    /// <summary>Set while <see cref="Activity"/> is Syncing or BackingUp.</summary>
    public ProgressDto? Progress { get; init; }
    public FolderRunDto? LastResult { get; init; }
    public DateTimeOffset? NextRunAt { get; init; }
}

/// <summary>
/// A run's progress. Totals are known for Mirror/TwoWay after the plan is made (files to transfer or delete, bytes to
/// transfer); a backup does not count its folder first, so its totals stay empty.
/// </summary>
public sealed record ProgressDto(int Files, int? TotalFiles, long Bytes, long? TotalBytes, string? CurrentFile);

public sealed record FolderRunDto(int FolderId, string Name, bool Ok, string Message, bool Skipped, DateTimeOffset At);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
[JsonSerializable(typeof(ControlEventMessage))]
public sealed partial class ControlJsonContext : JsonSerializerContext;
