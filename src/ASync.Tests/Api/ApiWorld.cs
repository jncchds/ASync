extern alias server;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASync.Contracts;
using ASync.Core.Models;
using ASync.Infrastructure.Data;
using ASync.Tests.Engine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ASync.Tests.Api;

/// <summary>
/// The real server (<c>Program</c>, every controller, the auth pipeline, EF Core) on a real PostgreSQL: a database of its
/// own on the server named by <c>ASYNC_TEST_POSTGRES</c> (the <c>postgres</c> service of docker-compose.test.yml; see
/// scripts/test-backends-env.sh), dropped afterwards. Without the variable the API tests are skipped.
/// <para>PostgreSQL rather than an in-memory provider because the queries, the upserts of usage accounting and the
/// constraints are the production ones; the container is already part of the test stack CI starts, so Testcontainers
/// would add a dependency without adding coverage.</para>
/// <para>Background jobs (alerts, maintenance, usage scans, health checks) are switched off; automatic 400 answers for
/// invalid bodies are too (<see cref="ApiBehaviorOptions.SuppressModelStateInvalidFilter"/>), so every action really runs
/// with whatever a request carries and its own ownership check is what answers.</para>
/// Seeds two people: alice (the first account, admin) with a back-end, a folder, a device attached to it, an enrollment
/// code, an alert channel, an incident and a purge record; and bob (not admin) with a device of his own.
/// </summary>
public sealed class ApiWorld : IAsyncLifetime
{
    public static string? Connection => Environment.GetEnvironmentVariable("ASYNC_TEST_POSTGRES") is { Length: > 0 } c ? c : null;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly string _database = $"async_api_{Guid.NewGuid():N}";
    private readonly TempDir _storage = new();

    /// <summary>Where Local back-ends may live (Storage:LocalRoots).</summary>
    public string StorageRoot => _storage.Path;
    public Factory Server { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;
    public string AliceToken { get; private set; } = "";
    public string BobToken { get; private set; } = "";
    public string AliceDeviceToken { get; private set; } = "";
    public string BobDeviceToken { get; private set; } = "";
    public int AliceId { get; private set; }
    public int BobId { get; private set; }
    public int AliceBackend { get; private set; }
    public int AliceFolder { get; private set; }
    public Guid AliceDevice { get; private set; }
    public Guid BobDevice { get; private set; }
    public int AliceChannel { get; private set; }
    public int AliceEnrollment { get; private set; }
    public Guid AlicePurge { get; private set; }

    public sealed class Factory(string connection, string storageRoot) : WebApplicationFactory<server::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection", connection);
            builder.UseSetting("Jwt:SigningKey", "api-tests-signing-key-0123456789-abcdefghijklmnop");
            builder.UseSetting("Storage:LocalRoots", storageRoot);
            foreach (var job in new[] { "Alerts", "Maintenance", "Usage", "Health" }) builder.UseSetting($"{job}:Enabled", "false");
            builder.ConfigureServices(s => s.Configure<ApiBehaviorOptions>(o => o.SuppressModelStateInvalidFilter = true));
        }
    }

    public async Task InitializeAsync()
    {
        if (Connection is not { } connection) return;
        Server = new Factory($"{connection};Database={_database}", _storage.Path);
        Client = Server.CreateClient();

        AliceToken = (await PostAsync<SessionDto>("api/auth/register", new LoginRequest("alice", "alice-password"), null)).AccessToken;
        BobId = (await PostAsync<AdminUserDto>("api/users", new CreateUserRequest("bob", "bob-password"), AliceToken)).Id;
        var bob = await PostAsync<SessionDto>("api/auth/login", new LoginRequest("bob", "bob-password"), null);
        BobToken = bob.AccessToken;

        var me = await SendAsync(HttpMethod.Get, "api/auth/me", AliceToken);
        AliceId = (await me.Content.ReadFromJsonAsync<UserDto>(Json))!.Id;
        Directory.CreateDirectory(Path.Combine(_storage.Path, "alice"));
        AliceBackend = (await PostAsync<StorageBackendDto>("api/storage-backends",
            new SaveStorageBackendRequest("Alice's disk", new LocalStorageSettings(Path.Combine(_storage.Path, "alice"))), AliceToken)).Id;
        AliceFolder = (await PostAsync<FolderDto>("api/folders",
            new SaveFolderRequest("Docs", SyncMode.TwoWay, FolderEncryption.Transport, FolderKind.Files, AliceBackend, "docs"), AliceToken)).Id;

        (AliceDevice, AliceDeviceToken) = await EnrollAsync(AliceToken, "alice-laptop");
        (BobDevice, BobDeviceToken) = await EnrollAsync(BobToken, "bob-laptop");
        AliceEnrollment = (await PostAsync<EnrollmentCreatedDto>("api/devices/enrollments", new CreateEnrollmentRequest(), AliceToken)).Id;
        using (var attach = await SendAsync(HttpMethod.Put, $"api/folders/{AliceFolder}/devices", AliceToken,
                   new AttachDeviceRequest(AliceDevice, "/home/alice/docs")))
            attach.EnsureSuccessStatusCode();
        AliceChannel = (await PostAsync<AlertChannelDto>("api/alerts/channels",
            new SaveAlertChannelRequest("hook", new WebhookChannelSettings("https://hooks.example.invalid/x")), AliceToken)).Id;

        // Rows no endpoint makes directly.
        await using var scope = Server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        AlicePurge = Guid.CreateVersion7();
        db.PurgeJobs.Add(new PurgeJob
        {
            Id = AlicePurge, RequestedByUserId = AliceId, OwnerUserId = AliceId, OwnerName = "alice", FolderId = 999, FolderName = "Old",
            StorageBackendId = AliceBackend, RootPath = "old", Status = PurgeStatus.Done, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.AlertIncidents.Add(new AlertIncident
        {
            UserId = AliceId, FolderId = AliceFolder, Kind = AlertKind.CheckFailed, FolderName = "Docs", Detail = "secret detail",
            OpenedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.FolderUsages.Add(new FolderUsage { FolderId = AliceFolder, Bytes = 12345, Files = 3, ScannedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    /// <summary>Enrolls a device for the person with an enrollment code and signs it in (challenge + signature).</summary>
    public async Task<(Guid Id, string Token)> EnrollAsync(string personToken, string name, bool reusable = false)
    {
        var code = await PostAsync<EnrollmentCreatedDto>("api/devices/enrollments", new CreateEnrollmentRequest(Reusable: reusable), personToken);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var enrolled = await PostAsync<EnrolledDto>("api/device-auth/enroll",
            new EnrollRequest(code.Code, name, DevicePlatform.Linux, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())), null);
        var challenge = await PostAsync<DeviceChallengeDto>("api/device-auth/challenge", new DeviceChallengeRequest(enrolled.DeviceId), null);
        var signature = key.SignData(DeviceAuthMessage.Build(enrolled.DeviceId, challenge.Nonce), HashAlgorithmName.SHA256);
        var token = await PostAsync<DeviceTokenDto>("api/device-auth/token",
            new DeviceTokenRequest(enrolled.DeviceId, challenge.Nonce, Convert.ToBase64String(signature)), null);
        return (enrolled.DeviceId, token.AccessToken);
    }

    public async Task<T> PostAsync<T>(string url, object body, string? token)
    {
        using var response = await SendAsync(HttpMethod.Post, url, token, body);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"POST {url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is string raw) request.Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json");
        else if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        return Client.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        if (Connection is not { } connection) return;
        Client.Dispose();
        await Server.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection($"{connection};Database=postgres");
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
        _storage.Dispose();
    }
}
