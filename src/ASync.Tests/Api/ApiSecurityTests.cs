using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using ASync.Api.Auth;
using ASync.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ASync.Tests.Api;

/// <summary>
/// Role separation and ownership over HTTP, for every endpoint the server maps: the list comes from the routing table,
/// so a new endpoint is covered (and has to be classified) without anyone remembering to add a test.
/// <list type="bullet">
/// <item>Anonymous: 401 everywhere except the endpoints listed in <see cref="Anonymous"/>.</item>
/// <item>A device token: 403 on every people's endpoint. A person's token: 403 on every device endpoint.</item>
/// <item>A non-admin: 403 on admin endpoints.</item>
/// <item>One person (and their device) asking for another's folder, back-end, device, snapshot, file, upload, alert
///   channel, enrollment code or purge by id: 404, as if it did not exist.</item>
/// </list>
/// </summary>
[Trait("Category", "Api")]
public class ApiSecurityTests(ApiWorld world) : IClassFixture<ApiWorld>
{
    /// <summary>Endpoints anyone may call: signing in, device enrollment and sign-in, token-protected download links, health.</summary>
    private static readonly HashSet<string> Anonymous =
    [
        "GET api/auth/setup", "POST api/auth/register", "POST api/auth/login", "POST api/auth/refresh", "POST api/auth/logout",
        "GET api/auth/oidc/login", "GET api/auth/oidc/complete",
        "POST api/device-auth/enroll", "POST api/device-auth/device-code", "POST api/device-auth/device-code/poll",
        "POST api/device-auth/challenge", "POST api/device-auth/token",
        "GET api/file-downloads", "GET api/snapshot-downloads", "* health",
    ];

    /// <summary>Endpoints for any signed-in caller, person or device: live updates.</summary>
    private static readonly HashSet<string> AnySignedIn = ["* hubs/sync", "* hubs/sync/negotiate"];

    private enum Kind { Anonymous, AnySignedIn, People, Admin, Device }

    private sealed record Api(string Method, string Template, Kind Kind, ControllerActionDescriptor? Action)
    {
        public string Key => $"{Method} {Template}";
        public override string ToString() => Key;
    }

    private static readonly string[] Users = Roles.Users.Split(',');

    private List<Api> Endpoints()
    {
        var result = new List<Api>();
        foreach (var endpoint in world.Server.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var template = (endpoint.RoutePattern.RawText ?? "").TrimStart('/');
            if (template.StartsWith("{*", StringComparison.Ordinal)) continue; // the SPA fallback (index.html)
            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["*"];
            var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null || authorize.Count == 0;
            // Every [Authorize] on the way must be satisfied: the roles allowed are the intersection.
            HashSet<string>? roles = null;
            foreach (var a in authorize.Where(a => !string.IsNullOrEmpty(a.Roles)))
            {
                var these = a.Roles!.Split(',', StringSplitOptions.TrimEntries).ToHashSet();
                roles = roles is null ? these : [.. roles.Intersect(these)];
            }
            var kind = anonymous ? Kind.Anonymous
                : roles is null ? Kind.AnySignedIn
                : roles.SetEquals([Roles.Device]) ? Kind.Device
                : roles.SetEquals([Roles.Admin]) ? Kind.Admin
                : roles.SetEquals(Users) ? Kind.People
                : throw new Xunit.Sdk.XunitException($"{template} allows the roles {string.Join(",", roles)}: people and devices must not share an endpoint.");
            foreach (var method in methods)
                result.Add(new Api(method, template, kind, endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()));
        }
        return result;
    }

    // ── Filling in requests ───────────────────────────────────────────────────

    /// <summary>The id of alice's resource for each route parameter. New parameters must be added here, or the test fails.</summary>
    private string Value(string template, string name) => (name, template) switch
    {
        ("id", _) when template.StartsWith("api/sync/folders", StringComparison.Ordinal) => $"{world.AliceFolder}",
        ("id", _) when template.StartsWith("api/folders", StringComparison.Ordinal) => $"{world.AliceFolder}",
        ("folderId", _) => $"{world.AliceFolder}",
        ("id", _) when template.StartsWith("api/storage-backends", StringComparison.Ordinal) => $"{world.AliceBackend}",
        ("id", _) when template.StartsWith("api/alerts/channels", StringComparison.Ordinal) => $"{world.AliceChannel}",
        ("id", _) when template.StartsWith("api/devices/enrollments", StringComparison.Ordinal) => $"{world.AliceEnrollment}",
        ("id", _) when template.StartsWith("api/devices", StringComparison.Ordinal) => $"{world.AliceDevice}",
        ("id", _) when template.StartsWith("api/purges", StringComparison.Ordinal) => $"{world.AlicePurge}",
        ("id", _) when template.StartsWith("api/users", StringComparison.Ordinal) => $"{world.AliceId}",
        ("deviceId", _) => $"{world.AliceDevice}",
        ("snapshotId", _) => "0123456789abcdef",
        ("uploadId", _) => $"{Guid.NewGuid()}",
        ("number", _) => "0",
        ("leaseId", _) => "some-lease",
        ("userCode", _) => "ABCD-EFGH",
        _ => throw new Xunit.Sdk.XunitException(
            $"Route parameter {{{name}}} of {template} is not mapped to another user's resource: add it to ApiSecurityTests.Value."),
    };

    private static readonly Regex Parameter = new(@"\{\*?(?<name>\w+)(:[^}]*)?\}", RegexOptions.Compiled);

    private string Url(Api api)
    {
        var path = Parameter.Replace(api.Template, m => Uri.EscapeDataString(Value(api.Template, m.Groups["name"].Value)));
        // Query parameters get harmless values, so actions reach their ownership check rather than stopping at a null.
        var query = api.Action?.Parameters.Where(p => p.BindingInfo?.BindingSource == BindingSource.Query)
            .Select(p => $"{p.Name}={(p.ParameterType == typeof(bool) || p.ParameterType == typeof(bool?) ? "false" : p.ParameterType == typeof(string) ? "x" : "1")}")
            .ToList() ?? [];
        return path + (query.Count > 0 ? "?" + string.Join("&", query) : "");
    }

    private async Task<HttpStatusCode> CallAsync(Api api, string? token)
    {
        var method = api.Method == "*" ? HttpMethod.Get : new HttpMethod(api.Method);
        var body = method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch ? "{}" : null;
        using var response = await world.SendAsync(method, Url(api), token, body);
        return response.StatusCode;
    }

    private async Task ExpectAsync(IEnumerable<Api> apis, string? token, HttpStatusCode expected)
    {
        var wrong = new List<string>();
        foreach (var api in apis)
        {
            var status = await CallAsync(api, token);
            if (status != expected) wrong.Add($"{api.Key}: {(int)status}");
        }
        Assert.True(wrong.Count == 0, $"Expected {(int)expected} but got:\n{string.Join("\n", wrong)}");
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public void EveryEndpoint_IsForPeople_Devices_OrListedAsOpen()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES (docker-compose.test.yml + scripts/test-backends-env.sh).");
        var all = Endpoints();
        Assert.True(all.Count > 80, $"Only {all.Count} endpoints found: is the routing table read correctly?");
        Assert.Equal(Anonymous.Order(), all.Where(a => a.Kind == Kind.Anonymous).Select(a => a.Key).Order());
        Assert.Equal(AnySignedIn.Order(), all.Where(a => a.Kind == Kind.AnySignedIn).Select(a => a.Key).Order());
        Assert.Contains(all, a => a.Kind == Kind.Device);
        Assert.Contains(all, a => a.Kind == Kind.Admin);
    }

    [SkippableFact]
    public async Task Anonymous_Gets401_EverywhereButTheOpenEndpoints()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        await ExpectAsync(Endpoints().Where(a => a.Kind != Kind.Anonymous), null, HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task ADeviceToken_Gets403_OnEveryPeoplesEndpoint()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        await ExpectAsync(Endpoints().Where(a => a.Kind is Kind.People or Kind.Admin), world.AliceDeviceToken, HttpStatusCode.Forbidden);
    }

    [SkippableFact]
    public async Task APersonsToken_Gets403_OnEveryDeviceEndpoint()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        await ExpectAsync(Endpoints().Where(a => a.Kind == Kind.Device), world.AliceToken, HttpStatusCode.Forbidden); // an admin, even
    }

    [SkippableFact]
    public async Task ANonAdmin_Gets403_OnAdminEndpoints()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        await ExpectAsync(Endpoints().Where(a => a.Kind == Kind.Admin), world.BobToken, HttpStatusCode.Forbidden);
    }

    [SkippableFact]
    public async Task APerson_Gets404_ForEverythingOfAnotherPersonById()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        await ExpectAsync(Endpoints().Where(a => a.Kind == Kind.People && Parameter.IsMatch(a.Template)), world.BobToken, HttpStatusCode.NotFound);
        // Nothing was changed for alice on the way.
        using var folder = await world.SendAsync(HttpMethod.Get, $"api/folders/{world.AliceFolder}", world.AliceToken);
        Assert.Equal(HttpStatusCode.OK, folder.StatusCode);
        Assert.Single((await folder.Content.ReadFromJsonAsync<FolderDto>(ApiWorld.Json))!.Devices);
    }

    [SkippableFact]
    public async Task ADevice_Gets404_ForAnotherPersonsFolders()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        await ExpectAsync(Endpoints().Where(a => a.Kind == Kind.Device && Parameter.IsMatch(a.Template)), world.BobDeviceToken, HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task Lists_ShowOnlyTheCallersOwnThings()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        async Task<string> Get(string url, string token)
        {
            using var response = await world.SendAsync(HttpMethod.Get, url, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        Assert.Equal("[]", await Get("api/folders", world.BobToken));
        Assert.Equal("[]", await Get("api/storage-backends", world.BobToken));
        Assert.Equal("[]", await Get("api/alerts/channels", world.BobToken));
        Assert.Equal("[]", await Get("api/alerts/incidents?days=30", world.BobToken));
        Assert.Equal("[]", await Get("api/purges", world.BobToken));
        Assert.Equal("[]", await Get("api/sync/folders", world.BobDeviceToken));
        var devices = await Get("api/devices", world.BobToken);
        Assert.Contains("bob-laptop", devices);
        Assert.DoesNotContain("alice-laptop", devices);
        var usage = (await world.SendAsync(HttpMethod.Get, "api/usage", world.BobToken)).Content.ReadFromJsonAsync<UsageDto>(ApiWorld.Json);
        Assert.Equal(0, (await usage)!.Total.UsedBytes);
        Assert.Empty((await usage)!.Folders);
        // And alice sees hers (so the empty lists above are not just broken endpoints).
        Assert.Contains("Docs", await Get("api/folders", world.AliceToken));
        Assert.Contains("secret detail", await Get("api/alerts/incidents?days=30", world.AliceToken));
        Assert.Contains("Docs", await Get("api/sync/folders", world.AliceDeviceToken));
        Assert.Equal(12345, (await (await world.SendAsync(HttpMethod.Get, "api/usage", world.AliceToken)).Content.ReadFromJsonAsync<UsageDto>(ApiWorld.Json))!.Total.UsedBytes);
    }

    [SkippableFact]
    public async Task Ids_InRequestBodies_AreCheckedToo()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        // A folder on alice's personal back-end.
        using (var folder = await world.SendAsync(HttpMethod.Post, "api/folders", world.BobToken,
                   new SaveFolderRequest("Mine", SyncMode.TwoWay, FolderEncryption.Transport, FolderKind.Files, world.AliceBackend, "bob")))
            Assert.True(folder.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"{folder.StatusCode}");
        // Testing with alice's stored secrets.
        using (var test = await world.SendAsync(HttpMethod.Post, "api/storage-backends/test", world.BobToken,
                   new StorageTestRequest(new LocalStorageSettings("/tmp"), world.AliceBackend)))
            Assert.Equal(HttpStatusCode.NotFound, test.StatusCode);
        using (var test = await world.SendAsync(HttpMethod.Post, "api/alerts/channels/test", world.BobToken,
                   new AlertTestRequest(new WebhookChannelSettings("https://hooks.example.invalid/y"), world.AliceChannel)))
            Assert.Equal(HttpStatusCode.NotFound, test.StatusCode);
        // Attaching alice's device to a folder: bob has none, and alice's folder is not his.
        using (var attach = await world.SendAsync(HttpMethod.Put, $"api/folders/{world.AliceFolder}/devices", world.BobToken,
                   new AttachDeviceRequest(world.BobDevice, "/x")))
            Assert.Equal(HttpStatusCode.NotFound, attach.StatusCode);
        Assert.Equal("[]", await (await world.SendAsync(HttpMethod.Get, "api/folders", world.BobToken)).Content.ReadAsStringAsync());
    }
}
