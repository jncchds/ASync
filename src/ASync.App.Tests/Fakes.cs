using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ASync.Agent.Control;
using ASync.App.Services;
using ASync.App.ViewModels;
using ASync.Contracts;

namespace ASync.App.Tests;

/// <summary>
/// An agent the test drives: it answers requests from fields, records them, and streams the events the test pushes.
/// <see cref="Available"/> false makes <see cref="ConnectAsync"/> find no agent; <see cref="Stop"/> ends the stream.
/// </summary>
internal sealed class FakeAgent : IAgentConnector, IAgentSession
{
    private Channel<ControlEvent> _events = Channel.CreateUnbounded<ControlEvent>();

    public bool Available { get; set; } = true;
    public int Connects { get; private set; }
    public List<string> Calls { get; } = [];
    public string? LastSecret { get; private set; }
    public Func<int?, List<FolderRunDto>> SyncResult { get; set; } = id => [new FolderRunDto(id ?? 1, "Docs", true, "2 up, 0 down, 0 deleted", false, DateTimeOffset.UtcNow)];
    public Exception? Fail { get; set; }
    public PendingEnrollmentDto Pending { get; set; } =
        new("https://sync.example.com", "laptop", "ABCD-EFGH", "https://sync.example.com/devices/approve?code=ABCD-EFGH", DateTimeOffset.UtcNow.AddMinutes(15));

    public AgentStatusDto Status { get; set; } = Statuses.Connected();

    public HelloDto Hello => new(ControlProtocol.Version, "9.9.9", 4242, AgentMode.Service, "/cfg");

    public Task<IAgentSession?> ConnectAsync(CancellationToken ct)
    {
        if (!Available) return Task.FromResult<IAgentSession?>(null);
        Connects++;
        _events = Channel.CreateUnbounded<ControlEvent>();
        _events.Writer.TryWrite(new ControlEvent { Kind = ControlEventKinds.Status, Status = Status });
        return Task.FromResult<IAgentSession?>(this);
    }

    public void Push(ControlEvent e) => _events.Writer.TryWrite(e);
    public void PushStatus(AgentStatusDto status) => Push(new ControlEvent { Kind = ControlEventKinds.Status, Status = Status = status });
    public void PushFolder(FolderStatusDto folder) => Push(new ControlEvent { Kind = ControlEventKinds.Folder, Folder = folder });
    public void PushRun(FolderStatusDto folder) => Push(new ControlEvent { Kind = ControlEventKinds.RunFinished, Folder = folder, Result = folder.LastResult });

    /// <summary>The agent stops: the event stream ends.</summary>
    public void Stop()
    {
        Available = false;
        _events.Writer.TryComplete();
    }

    public async IAsyncEnumerable<ControlEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in _events.Reader.ReadAllAsync(ct)) yield return e;
    }

    private Task<T> Answer<T>(string call, T value)
    {
        Calls.Add(call);
        return Fail is { } e ? Task.FromException<T>(e) : Task.FromResult(value);
    }

    public Task<AgentStatusDto> StatusAsync(CancellationToken ct) => Answer("status", Status);
    public Task<List<FolderRunDto>> SyncAsync(int? folderId, CancellationToken ct) => Answer($"sync {folderId?.ToString() ?? "all"}", SyncResult(folderId));
    public Task<string> SetPausedAsync(int folderId, bool paused, CancellationToken ct) =>
        Answer($"{(paused ? "pause" : "resume")} {folderId}", $"folder {folderId} {(paused ? "paused" : "resumed")}");
    public Task<string> SetPathAsync(int folderId, string path, CancellationToken ct) => Answer($"set-path {folderId} {path}", $"now at {path}");

    public Task<string> UnlockAsync(int folderId, string secret, CancellationToken ct)
    {
        LastSecret = secret;
        return Answer($"unlock {folderId}", "unlocked");
    }

    public Task<AgentStatusDto> EnrollAsync(string server, string code, string name, DevicePlatform platform, CancellationToken ct) =>
        Answer($"enroll {server} {code} {name} {platform}", Statuses.Connected());

    public Task<PendingEnrollmentDto> StartDeviceCodeAsync(string server, string name, DevicePlatform platform, CancellationToken ct) =>
        Answer($"device-code {server} {name} {platform}", Pending);

    public Task<string> ForgetAsync(CancellationToken ct) => Answer("forget", "Signed out.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeService : IAgentServiceManager
{
    public AgentServiceState State { get; set; } = AgentServiceState.NotInstalled;
    public List<string> Calls { get; } = [];
    public Action? OnInstall { get; set; }

    public Task<AgentServiceState> GetStateAsync(CancellationToken ct) => Task.FromResult(State);

    public Task<string> InstallAndStartAsync(CancellationToken ct)
    {
        Calls.Add("install");
        OnInstall?.Invoke();
        return Task.FromResult("installed");
    }

    public Task<string> StartAsync(CancellationToken ct)
    {
        Calls.Add("start");
        OnInstall?.Invoke();
        return Task.FromResult("started");
    }
}

internal sealed class FakePlatform : IPlatformServices
{
    public string MachineName => "test-host";
    public DevicePlatform Platform => DevicePlatform.Linux;
    public string AppVersion => "1.2.3";
    public List<string> Opened { get; } = [];
    public List<(string Title, string Message)> Notifications { get; } = [];
    public List<ThemeChoice> Themes { get; } = [];
    public string? NextPick { get; set; }
    public bool StartAtLogin { get; set; }

    public Task OpenUrlAsync(string url)
    {
        Opened.Add(url);
        return Task.CompletedTask;
    }

    public Task OpenFolderAsync(string path)
    {
        Opened.Add("folder " + path);
        return Task.CompletedTask;
    }

    public Task<string?> PickFolderAsync(string title, string? startAt) => Task.FromResult(NextPick);
    public void Notify(string title, string message) => Notifications.Add((title, message));
    public void ApplyTheme(ThemeChoice theme) => Themes.Add(theme);
    public bool SupportsStartAtLogin => true;
    public bool IsStartAtLoginEnabled() => StartAtLogin;
    public void SetStartAtLogin(bool enabled) => StartAtLogin = enabled;
}

internal sealed class MemorySettings : ISettingsStore
{
    public AppSettings Settings { get; set; } = new();
    public AppSettings Load() => Settings;
    public void Save(AppSettings settings) => Settings = settings;
}

internal static class Statuses
{
    public static FolderStatusDto Folder(int id = 1, string name = "Docs", string path = "/home/alice/Docs") => new()
    {
        FolderId = id, Name = name, Mode = SyncMode.TwoWay, Encryption = FolderEncryption.Transport, LocalPath = path, IntervalMinutes = 15,
        Activity = FolderActivity.Idle, NextRunAt = DateTimeOffset.UtcNow.AddMinutes(10),
    };

    public static AgentStatusDto Connected(params FolderStatusDto[] folders) => new()
    {
        State = AgentState.Connected, AgentVersion = "9.9.9", Mode = AgentMode.Service, ProcessId = 4242, StartedAt = DateTimeOffset.UtcNow,
        Device = new DeviceInfoDto(Guid.NewGuid(), "laptop", "alice", "https://sync.example.com"),
        Folders = [.. folders],
    };

    public static AgentStatusDto NotEnrolled() => new() { State = AgentState.NotEnrolled, AgentVersion = "9.9.9", Mode = AgentMode.Service };
}

/// <summary>A shell on fakes, started, plus a way to wait for what its loop does.</summary>
internal sealed class ShellFixture : IAsyncDisposable
{
    public FakeAgent Agent { get; } = new();
    public FakeService Service { get; } = new();
    public FakePlatform Platform { get; } = new();
    public MemorySettings Settings { get; } = new();
    public ShellViewModel Shell { get; private set; } = null!;

    public ShellFixture Start(AgentStatusDto? status = null)
    {
        if (status is not null) Agent.Status = status;
        Shell = new ShellViewModel(Agent, Service, Platform, Settings) { RetryInterval = TimeSpan.FromMilliseconds(50) };
        Shell.Start();
        return this;
    }

    public static async Task Until(Func<bool> condition, string what = "condition")
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }

    public ValueTask DisposeAsync() => Shell.DisposeAsync();
}
