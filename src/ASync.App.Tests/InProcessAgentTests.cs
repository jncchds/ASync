using ASync.Agent;
using ASync.Agent.Control;
using ASync.App.Services;
using ASync.App.ViewModels;
using ASync.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASync.App.Tests;

/// <summary>
/// The in-process agent client (Android, iOS): the same <see cref="IAgentSession"/> behaviour as the control channel's,
/// against a fake handler and against a real <see cref="AgentRuntime"/> without a control channel; and the phone parts of
/// the shared pages (QR scan, opened pairing links, the back gesture, content-URI folders).
/// </summary>
public class InProcessAgentTests
{
    private sealed class FakeHandler : IControlHandler
    {
        public AgentStatusDto Status { get; set; } = Statuses.NotEnrolled();
        public CallLog<string> Calls { get; } = new();
        public Exception? Fail { get; set; }

        public HelloDto Hello() => new(ControlProtocol.Version, "9.9.9", 1, AgentMode.Foreground, "/cfg");

        private T Answer<T>(string call, T value)
        {
            Calls.Add(call);
            return Fail is { } e ? throw e : value;
        }

        public Task<AgentStatusDto> StatusAsync(CancellationToken ct) => Task.FromResult(Answer("status", Status));
        public Task<List<FolderRunDto>> SyncAsync(int? folderId, bool allowMassDelete, CancellationToken ct) =>
            Task.FromResult(Answer($"sync {folderId} {allowMassDelete}", new List<FolderRunDto> { new(1, "Docs", true, "1 up", false, DateTimeOffset.UtcNow) }));
        public Task<string> SetPausedAsync(int folderId, bool paused, CancellationToken ct) => Task.FromResult(Answer($"pause {folderId} {paused}", "paused"));
        public Task<string> SetPathAsync(int folderId, string path, CancellationToken ct) => Task.FromResult(Answer($"set-path {folderId} {path}", "moved"));
        public Task<string> UnlockAsync(int folderId, string secret, CancellationToken ct) => Task.FromResult(Answer($"unlock {folderId}", "unlocked"));
        public Task<AgentStatusDto> EnrollAsync(string server, string code, string name, DevicePlatform platform, CancellationToken ct) =>
            Task.FromResult(Answer($"enroll {server} {code} {name} {platform}", Status));
        public Task<PendingEnrollmentDto> StartDeviceCodeAsync(string server, string name, DevicePlatform platform, CancellationToken ct) =>
            Task.FromResult(Answer($"device-code {server}", new PendingEnrollmentDto(server, name, "ABCD-EFGH", server + "/devices/approve", DateTimeOffset.UtcNow.AddMinutes(15))));
        public Task<string> ForgetAsync(CancellationToken ct) => Task.FromResult(Answer("forget", "forgotten"));
        public Task<string> InitEncryptionAsync(int folderId, string passphrase, CancellationToken ct) =>
            Task.FromResult(Answer($"init-encryption {folderId}", "RECOVERY-KEY"));
        public Task<string> SetConditionsAsync(int folderId, RunConditionsDto conditions, CancellationToken ct) =>
            Task.FromResult(Answer($"set-conditions {folderId} {conditions.UnmeteredOnly}", "saved"));
    }

    [Fact]
    public async Task TheBackgroundOperations_SetUpEncryptionAndConditions_GoToTheHandler()
    {
        var handler = new FakeHandler();
        var session = (await new InProcessAgentConnector(handler, new ControlEventHub()).ConnectAsync(CancellationToken.None))!;
        Assert.Equal("RECOVERY-KEY", await session.InitEncryptionAsync(3, "correct horse battery", CancellationToken.None));
        Assert.Equal("saved", await session.SetConditionsAsync(3, new RunConditionsDto(UnmeteredOnly: true), CancellationToken.None));
        Assert.Equal("\"init-encryption\" needs a passphrase.", (await Assert.ThrowsAsync<ControlException>(() =>
            session.InitEncryptionAsync(3, "", CancellationToken.None))).Message);
        Assert.Equal(["init-encryption 3", "set-conditions 3 True"], handler.Calls);
    }

    [Fact]
    public async Task Requests_GoToTheHandler_WithTheControlServersChecks()
    {
        var handler = new FakeHandler();
        var session = (await new InProcessAgentConnector(handler, new ControlEventHub()).ConnectAsync(CancellationToken.None))!;
        Assert.Equal("9.9.9", session.Hello.AgentVersion);
        Assert.Single(await session.SyncAsync(1, CancellationToken.None));
        Assert.Equal("paused", await session.SetPausedAsync(1, true, CancellationToken.None));
        Assert.Equal("moved", await session.SetPathAsync(1, "content://x/tree/y", CancellationToken.None));
        Assert.Equal("unlocked", await session.UnlockAsync(1, "secret", CancellationToken.None));
        await session.EnrollAsync("  https://s ", " CODE ", " Pixel ", DevicePlatform.Android, CancellationToken.None);
        Assert.Equal(["sync 1 False", "pause 1 True", "set-path 1 content://x/tree/y", "unlock 1", "enroll https://s CODE Pixel Android"], handler.Calls);

        // What the control server refuses before the agent sees it.
        Assert.Equal("\"enroll\" needs a code.", (await Assert.ThrowsAsync<ControlException>(() =>
            session.EnrollAsync("https://s", " ", "Pixel", DevicePlatform.Android, CancellationToken.None))).Message);
        Assert.Equal("\"unlock\" needs a secret.", (await Assert.ThrowsAsync<ControlException>(() =>
            session.UnlockAsync(1, "", CancellationToken.None))).Message);
        Assert.Equal("\"set-path\" needs a path.", (await Assert.ThrowsAsync<ControlException>(() =>
            session.SetPathAsync(1, "", CancellationToken.None))).Message);
        Assert.Equal(5, handler.Calls.Count);
    }

    [Fact]
    public async Task Errors_ArriveAsControlExceptions_WithTheAgentsMessage_AndCancellationStaysCancellation()
    {
        var handler = new FakeHandler { Fail = new InvalidOperationException("The agent is still starting; try again in a moment.") };
        var session = (await new InProcessAgentConnector(handler, new ControlEventHub()).ConnectAsync(CancellationToken.None))!;
        var e = await Assert.ThrowsAsync<ControlException>(() => session.StatusAsync(CancellationToken.None));
        Assert.Equal("The agent is still starting; try again in a moment.", e.Message);

        handler.Fail = new ControlException("Folder 9 is not assigned to this device.");
        Assert.Equal("Folder 9 is not assigned to this device.",
            (await Assert.ThrowsAsync<ControlException>(() => session.SyncAsync(9, CancellationToken.None))).Message);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.StatusAsync(cancelled.Token));
    }

    [Fact]
    public async Task Subscribe_StartsWithTheStatus_ThenStreamsEvents_AndEndsWhenTheAgentGoes()
    {
        var handler = new FakeHandler { Status = Statuses.Connected(Statuses.Folder()) };
        var hub = new ControlEventHub();
        var session = (await new InProcessAgentConnector(handler, hub).ConnectAsync(CancellationToken.None))!;
        var received = new CallLog<ControlEvent>(); // filled by the reading task, read here
        var reading = Task.Run(async () =>
        {
            await foreach (var e in session.SubscribeAsync(CancellationToken.None)) received.Add(e);
        }, TestContext.Current.CancellationToken);
        await ShellFixture.Until(() => hub.SubscriberCount == 1 && received.Count == 1, "the first status");
        Assert.Equal(ControlEventKinds.Status, received[0].Kind);
        Assert.Equal(AgentState.Connected, received[0].Status!.State);

        hub.Publish(new ControlEvent { Kind = ControlEventKinds.Folder, Folder = Statuses.Folder() with { Activity = FolderActivity.Syncing } });
        await ShellFixture.Until(() => received.Count == 2, "the folder event");
        hub.Complete(); // the agent stopped
        await reading.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, hub.SubscriberCount);
    }

    [Fact]
    public async Task Subscribe_EndsWhenCancelled_AndASlowSubscriberLosesTheOldestEvents()
    {
        var hub = new ControlEventHub();
        var session = (await new InProcessAgentConnector(new FakeHandler(), hub).ConnectAsync(CancellationToken.None))!;
        using var stop = new CancellationTokenSource();
        await using var e = session.SubscribeAsync(stop.Token).GetAsyncEnumerator(stop.Token);
        Assert.True(await e.MoveNextAsync()); // the status
        for (var i = 0; i < ControlEventHub.Capacity + 50; i++)
            hub.Publish(new ControlEvent { Kind = ControlEventKinds.Folder, Folder = Statuses.Folder(id: i) });
        Assert.True(await e.MoveNextAsync());
        Assert.Equal(50, e.Current.Folder!.FolderId); // the first 50 were dropped, nobody blocked
        await stop.CancelAsync();
        Assert.False(await e.MoveNextAsync());
    }

    [Fact]
    public async Task Connect_FindsNoAgent_WhileItIsNotRunning()
    {
        var running = false;
        var connector = new InProcessAgentConnector(new FakeHandler(), new ControlEventHub(), () => running);
        Assert.Null(await connector.ConnectAsync(CancellationToken.None));
        running = true;
        Assert.NotNull(await connector.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheShell_RunsOnARealInProcessAgent_WithoutAControlChannel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "async-inproc-" + Guid.NewGuid().ToString("N")[..10]);
        await using var runtime = new AgentRuntime(new AgentOptions
        {
            ConfigDirectory = dir, WaitForIdentity = true, ControlChannel = false, Version = "1.0.0",
            IdentityPollInterval = TimeSpan.FromMilliseconds(100),
        }, NullLoggerFactory.Instance);
        try
        {
            runtime.Start();
            Assert.Null(runtime.Endpoint); // no socket or pipe
            Assert.False(File.Exists(Path.Combine(dir, ControlEndpoint.EndpointFileName)));
            using var stop = new CancellationTokenSource();
            var running = runtime.RunAsync(stop.Token);

            var settings = new MemorySettings();
            var platform = new FakePlatform { Phone = true };
            await using var shell = new ShellViewModel(new InProcessAgentConnector(runtime, runtime.Events, () => runtime.IsStarted),
                new FakeService(), platform, settings) { RetryInterval = TimeSpan.FromMilliseconds(50) };
            shell.Start();
            await ShellFixture.Until(() => shell.Page is EnrollViewModel, "the enroll page");
            Assert.Equal(AgentState.NotEnrolled, shell.Status!.State);

            // A server that is not there: the agent's error shows next to the form, as over the channel.
            shell.Enroll.Server = "http://127.0.0.1:9";
            shell.Enroll.Code = "K7QD-M2XP-9RWA";
            await shell.Enroll.EnrollCommand.ExecuteAsync(null);
            Assert.StartsWith("Cannot reach the server", shell.Enroll.Error);

            await stop.CancelAsync();
            await running;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ── The phone parts of the shared pages ───────────────────────────────────

    [Fact]
    public async Task Scan_FillsServerAndCode_AndEnrollsAtOnce()
    {
        await using var f = new ShellFixture();
        f.Platform.Phone = true;
        f.Platform.NextScan = "async://pair?s=https%3A%2F%2Fsync.example.com&c=K7QD-M2XP-9RWA";
        f.Start(Statuses.NotEnrolled());
        await ShellFixture.Until(() => f.Shell.Status is not null);
        Assert.True(f.Shell.Enroll.CanScan);
        Assert.Contains("this phone", f.Shell.Enroll.Intro);
        await f.Shell.Enroll.ScanCommand.ExecuteAsync(null);
        Assert.Equal(["enroll https://sync.example.com K7QD-M2XP-9RWA test-host Linux"], f.Agent.Calls);
    }

    [Fact]
    public async Task Scan_OfSomethingElse_SaysSo_AndACancelledScanDoesNothing()
    {
        await using var f = new ShellFixture();
        f.Platform.Phone = true;
        f.Start(Statuses.NotEnrolled());
        await ShellFixture.Until(() => f.Shell.Status is not null);
        await f.Shell.Enroll.ScanCommand.ExecuteAsync(null); // cancelled: null
        Assert.Null(f.Shell.Enroll.Error);
        f.Platform.NextScan = "https://example.com/menu";
        await f.Shell.Enroll.ScanCommand.ExecuteAsync(null);
        Assert.Contains("not an ASync pairing code", f.Shell.Enroll.Error);
        Assert.Empty(f.Agent.Calls);
    }

    [Fact]
    public async Task AnOpenedPairingLink_FillsTheForm_InCodeMode()
    {
        await using var f = await new ShellFixture().StartAndWait(Statuses.NotEnrolled());
        f.Shell.Enroll.UseDeviceCode = true;
        Assert.True(f.Shell.Enroll.ApplyLink("async://pair?s=http%3A%2F%2F10.0.2.2%3A5100&c=ABCD-EFGH-JKMN"));
        Assert.True(f.Shell.Enroll.UseCode);
        Assert.Equal(("http://10.0.2.2:5100", "ABCD-EFGH-JKMN"), (f.Shell.Enroll.Server, f.Shell.Enroll.Code));
        Assert.False(f.Shell.Enroll.ApplyLink("https://not-a-pairing-link"));
        Assert.False(f.Shell.Enroll.CanScan); // a desktop has no scanner
    }

    [Fact]
    public async Task Back_ClosesTheDialog_ThenSettings_ThenTheMessage_ThenLetsTheSystemGoBack()
    {
        var folder = Statuses.Folder() with { Encryption = FolderEncryption.EndToEnd, Mode = SyncMode.Backup, Locked = true };
        await using var f = await new ShellFixture().StartAndWait(Statuses.Connected(folder));
        await ShellFixture.Until(() => f.Shell.Dashboard.Folders.Count == 1);
        f.Shell.Dashboard.Folders[0].UnlockCommand.Execute(null);
        f.Shell.Dialog!.Secret = "half-typed";
        f.Shell.OpenSettingsCommand.Execute(null);
        f.Shell.ShowMessage("Synced.");

        var dialog = f.Shell.Dialog;
        Assert.True(f.Shell.GoBack());
        Assert.Null(f.Shell.Dialog);
        Assert.Equal("", dialog.Secret); // cleared, as Cancel does
        Assert.True(f.Shell.GoBack());
        Assert.Null(f.Shell.SettingsPage);
        Assert.True(f.Shell.GoBack());
        Assert.Null(f.Shell.Message);
        Assert.False(f.Shell.GoBack());
    }

    [Fact]
    public async Task AContentUriFolder_ShowsByName_AndThePickerAsksForThePhone()
    {
        var folder = Statuses.Folder(path: "content://com.android.externalstorage.documents/tree/primary%3ASync");
        await using var f = new ShellFixture();
        f.Platform.Phone = true;
        f.Platform.NextPick = "content://com.android.externalstorage.documents/tree/primary%3AOther";
        f.Start(Statuses.Connected(folder, Statuses.Folder(id: 2, name: "New", path: "")));
        await ShellFixture.Until(() => f.Shell.Dashboard.Folders.Count == 2);
        var docs = f.Shell.Dashboard.Folders.Single(x => x.Name == "Docs");
        Assert.Equal("Internal storage › Sync", docs.LocalPath);
        Assert.Equal("No folder on this phone yet", f.Shell.Dashboard.Folders.Single(x => x.Name == "New").LocalPath);
        await docs.ChooseFolderCommand.ExecuteAsync(null);
        Assert.Contains($"set-path 1 {f.Platform.NextPick}", f.Agent.Calls);
    }
}
