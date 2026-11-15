using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ASync.Agent;
using ASync.Agent.Control;
using ASync.Client.Core;
using ASync.Contracts;
using ASync.Tests.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASync.Tests.Agent;

/// <summary>
/// The control operations the desktop app enrolls and signs out with (<c>enroll</c>, <c>device-code</c>, <c>forget</c>):
/// a real <see cref="AgentRuntime"/> on its channel against a small fake server that only knows the enrollment endpoints
/// (sign-in fails there, so an enrolled agent stays Connecting/Offline, which is all these tests need).
/// </summary>
public class AgentEnrollmentTests : IAsyncLifetime
{
    private readonly TempDir _config = new();
    private readonly FakeEnrollmentServer _server = new();
    private readonly CancellationTokenSource _stop = new();
    private AgentRuntime _runtime = null!;
    private Task _running = Task.CompletedTask;
    private ControlClient _client = null!;

    public async Task InitializeAsync()
    {
        _runtime = new AgentRuntime(new AgentOptions
        {
            ConfigDirectory = _config.Path, Mode = AgentMode.Service, Version = "1.2.3", WaitForIdentity = true,
            IdentityPollInterval = TimeSpan.FromSeconds(30), // an enrollment must wake the agent without waiting for this
        }, NullLoggerFactory.Instance);
        _runtime.Start();
        _running = _runtime.RunAsync(_stop.Token);
        _client = await ControlClient.TryConnectAsync(_config.Path) ?? throw new InvalidOperationException("no agent");
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _stop.CancelAsync();
        await _running.WaitAsync(TimeSpan.FromSeconds(30));
        await _runtime.DisposeAsync();
        _server.Dispose();
        _config.Dispose();
    }

    private FileClientStore Store => new(_config.Path);

    private async Task<AgentStatusDto> WaitForAsync(Func<AgentStatusDto, bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var status = await _client.StatusAsync();
            if (condition(status)) return status;
            if (DateTime.UtcNow > until) throw new TimeoutException($"Status stayed {status.State}: {JsonSerializer.Serialize(status)}");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Enroll_WithACode_KeepsTheIdentity_AndTheAgentStartsAtOnce()
    {
        var status = await _client.EnrollAsync(_server.Url, "K7QD-M2XP-9RWA", "laptop", DevicePlatform.Linux);

        Assert.Equal(("laptop", "alice", _server.Url), (status.Device?.DeviceName, status.Device?.Username, status.Device?.ServerUrl));
        Assert.Equal(DevicePlatform.Linux, _server.EnrolledPlatform);
        var identity = Store.LoadIdentity();
        Assert.Equal(FakeEnrollmentServer.DeviceId, identity?.DeviceId);
        // Started without waiting for the 30-second identity poll.
        await WaitForAsync(s => s.State is not AgentState.NotEnrolled and not AgentState.Revoked);
    }

    [Fact]
    public async Task Enroll_ShowsTheServersMessage_ForABadCode_AndAnUnreachableServer()
    {
        var bad = await Assert.ThrowsAsync<ControlException>(() => _client.EnrollAsync(_server.Url, "WRONG", "laptop", DevicePlatform.Linux));
        Assert.Equal("The code is not valid or has expired.", bad.Message);

        var unreachable = await Assert.ThrowsAsync<ControlException>(() =>
            _client.EnrollAsync($"http://127.0.0.1:{FakeEnrollmentServer.FreePort()}", "K7QD-M2XP-9RWA", "laptop", DevicePlatform.Linux));
        Assert.StartsWith("Cannot reach the server", unreachable.Message);

        var notAnAddress = await Assert.ThrowsAsync<ControlException>(() => _client.EnrollAsync("ftp://x", "K7QD", "laptop", DevicePlatform.Linux));
        Assert.Contains("not a server address", notAnAddress.Message);
        Assert.Null(Store.LoadIdentity());
        Assert.Equal(AgentState.NotEnrolled, (await _client.StatusAsync()).State);
    }

    [Fact]
    public async Task Enroll_IsRefused_WhileEnrolled()
    {
        await _client.EnrollAsync(_server.Url, "K7QD-M2XP-9RWA", "laptop", DevicePlatform.Linux);
        var refused = await Assert.ThrowsAsync<ControlException>(() => _client.EnrollAsync(_server.Url, "K7QD-M2XP-9RWA", "other", DevicePlatform.Linux));
        Assert.Contains("Already enrolled as \"laptop\"", refused.Message);
        await Assert.ThrowsAsync<ControlException>(() => _client.StartDeviceCodeAsync(_server.Url, "other", DevicePlatform.Linux));
    }

    [Fact]
    public async Task DeviceCode_ShowsTheCode_ThenStartsOnceApproved()
    {
        var pending = await _client.StartDeviceCodeAsync(_server.Url, "laptop", DevicePlatform.MacOS);
        Assert.Equal("ABCD-EFGH", pending.UserCode);
        Assert.Equal($"{_server.Url}/devices/approve?code=ABCD-EFGH", pending.VerificationUri);
        var status = await _client.StatusAsync();
        Assert.Equal((AgentState.NotEnrolled, "ABCD-EFGH"), (status.State, status.Enrollment?.UserCode));

        _server.Approve();
        status = await WaitForAsync(s => s.Device is not null);
        Assert.Null(status.Enrollment);
        Assert.Equal("laptop", Store.LoadIdentity()?.DeviceName);
        Assert.Equal(DevicePlatform.MacOS, _server.EnrolledPlatform);
    }

    [Fact]
    public async Task DeviceCode_Denied_IsReportedInTheStatus()
    {
        await _client.StartDeviceCodeAsync(_server.Url, "laptop", DevicePlatform.Linux);
        _server.Deny();
        var status = await WaitForAsync(s => s.Enrollment?.Error is not null);
        Assert.Equal("The request was denied in the web UI.", status.Enrollment!.Error);
        Assert.Equal(AgentState.NotEnrolled, status.State);
    }

    [Fact]
    public async Task Forget_StopsTheDevice_DeletesItsIdentity_AndAllowsANewEnrollment()
    {
        Assert.Equal("This device was not enrolled.", await _client.ForgetAsync());
        await _client.EnrollAsync(_server.Url, "K7QD-M2XP-9RWA", "laptop", DevicePlatform.Linux);
        await WaitForAsync(s => s.State is not AgentState.NotEnrolled);

        var message = await _client.ForgetAsync();
        Assert.Contains("\"laptop\" is no longer enrolled", message);
        Assert.Null(Store.LoadIdentity());
        var status = await WaitForAsync(s => s.State == AgentState.NotEnrolled);
        Assert.Null(status.Device);

        await _client.EnrollAsync(_server.Url, "K7QD-M2XP-9RWA", "desk", DevicePlatform.Linux);
        await WaitForAsync(s => s.Device?.DeviceName == "desk");
    }

    [Fact]
    public async Task Forget_CancelsAPendingDeviceCode()
    {
        await _client.StartDeviceCodeAsync(_server.Url, "laptop", DevicePlatform.Linux);
        await _client.ForgetAsync();
        Assert.Null((await _client.StatusAsync()).Enrollment);
        _server.Approve();
        await Task.Delay(1500); // a poll that would have happened
        Assert.Null(Store.LoadIdentity());
    }
}

/// <summary>The enrollment endpoints of the server, on a local port; everything else answers 503.</summary>
internal sealed class FakeEnrollmentServer : IDisposable
{
    public static readonly Guid DeviceId = Guid.Parse("0193b2a4-0000-7000-8000-000000000001");
    private readonly HttpListener _listener = new();
    private volatile string _decision = "Pending";

    public FakeEnrollmentServer()
    {
        Url = $"http://127.0.0.1:{FreePort()}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public string Url { get; }
    public DevicePlatform? EnrolledPlatform { get; private set; }

    public void Approve() => _decision = "Approved";
    public void Deny() => _decision = "Denied";

    public static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            try { await AnswerAsync(context); }
            catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { }
        }
    }

    private async Task AnswerAsync(HttpListenerContext c)
    {
        using var reader = new StreamReader(c.Request.InputStream);
        var body = JsonDocument.Parse(await reader.ReadToEndAsync() is { Length: > 0 } text ? text : "{}").RootElement;
        DevicePlatform Platform() => Enum.Parse<DevicePlatform>(body.GetProperty("platform").GetString()!, true);
        var (status, answer) = c.Request.Url!.AbsolutePath switch
        {
            "/api/device-auth/enroll" when body.GetProperty("code").GetString() == "K7QD-M2XP-9RWA" =>
                (200, Remember(Platform(), new { deviceId = DeviceId, userId = 1, username = "alice" })),
            "/api/device-auth/enroll" => (400, (object)new { message = "The code is not valid or has expired." }),
            "/api/device-auth/device-code" => (200, Remember(Platform(), new
            {
                deviceCode = "secret-device-code", userCode = "ABCD-EFGH", verificationUri = $"{Url}/devices/approve?code=ABCD-EFGH",
                intervalSeconds = 1, expiresAt = DateTime.UtcNow.AddMinutes(15),
            })),
            "/api/device-auth/device-code/poll" => (200, _decision == "Approved"
                ? new { status = "Approved", device = new { deviceId = DeviceId, userId = 1, username = "alice" } }
                : (object)new { status = _decision }),
            _ => (503, new { message = "not in this fake" }),
        };
        c.Response.StatusCode = status;
        c.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(answer));
        await c.Response.OutputStream.WriteAsync(bytes);
        c.Response.Close();
    }

    private object Remember(DevicePlatform platform, object answer)
    {
        EnrolledPlatform = platform;
        return answer;
    }

    public void Dispose() => _listener.Close();
}
