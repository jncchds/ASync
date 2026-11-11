// End-to-end smoke test of storage back-end management against a running server.
//   dotnet run scripts/smoke-storage.cs -- [server-url] [admin-user] [admin-password] [s3-endpoint-as-seen-by-server]
// Defaults: http://localhost:5100 admin admin-password http://host.docker.internal:19000 (docker-compose.test.yml).
// The S3 checks are skipped when the S3 endpoint is "none".
#:property TargetFramework=net10.0
#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

var server = args.Length > 0 ? args[0].TrimEnd('/') : "http://localhost:5100";
var adminUser = args.Length > 1 ? args[1] : "admin";
var adminPassword = args.Length > 2 ? args[2] : "admin-password";
var s3Endpoint = args.Length > 3 ? args[3] : "http://host.docker.internal:19000";
var http = new HttpClient { BaseAddress = new Uri(server) };
var failures = 0;

void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

async Task<(HttpStatusCode Status, JsonNode? Body)> Send(HttpMethod method, string path, object? body = null, string? token = null)
{
    using var req = new HttpRequestMessage(method, path);
    if (body is not null) req.Content = JsonContent.Create(body);
    if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var res = await http.SendAsync(req);
    var text = await res.Content.ReadAsStringAsync();
    var isJson = res.Content.Headers.ContentType?.MediaType?.Contains("json") == true;
    return (res.StatusCode, isJson && !string.IsNullOrWhiteSpace(text) ? JsonNode.Parse(text) : null);
}

async Task<string?> SignIn(string user, string password)
{
    var setup = await Send(HttpMethod.Get, "/api/auth/setup");
    var path = setup.Body?["needsSetup"]?.GetValue<bool>() == true ? "/api/auth/register" : "/api/auth/login";
    return (await Send(HttpMethod.Post, path, new { username = user, password })).Body?["accessToken"]?.GetValue<string>();
}

var admin = await SignIn(adminUser, adminPassword);
Check(admin is not null, $"admin {adminUser} signs in");
if (admin is null) return 1;

// ── Local (server disk) ───────────────────────────────────────────────────────
var options = await Send(HttpMethod.Get, "/api/storage-backends/options", token: admin);
Check(options.Body?["canCreateLocal"]?.GetValue<bool>() == true, $"admin may create Local back-ends under {options.Body?["localRoots"]?.ToJsonString()}");

var localSettings = new { type = "Local", path = $"/data/smoke-{Guid.NewGuid():N}" };
var localTest = await Send(HttpMethod.Post, "/api/storage-backends/test", new { settings = localSettings }, admin);
Check(localTest.Body?["ok"]?.GetValue<bool>() == true, $"test of a Local back-end → {localTest.Body?["error"] ?? "ok"}");

var outside = await Send(HttpMethod.Post, "/api/storage-backends", new { name = "etc", settings = new { type = "Local", path = "/etc" } }, admin);
Check(outside.Status == HttpStatusCode.BadRequest, $"Local outside the allowed roots refused → {outside.Body?["message"]}");

var local = await Send(HttpMethod.Post, "/api/storage-backends", new { name = "Smoke disk", settings = localSettings }, admin);
Check(local.Status == HttpStatusCode.OK, $"create Local back-end → {local.Status}");
var localId = local.Body?["id"]?.GetValue<int>() ?? 0;
var savedTest = await Send(HttpMethod.Post, $"/api/storage-backends/{localId}/test", token: admin);
Check(savedTest.Body?["ok"]?.GetValue<bool>() == true, "saved back-end test passes");
var listed = await Send(HttpMethod.Get, "/api/storage-backends", token: admin);
Check(listed.Body?.AsArray().Any(b => b?["id"]?.GetValue<int>() == localId && b["lastCheckOk"]?.GetValue<bool>() == true) == true,
    "list shows the back-end as working");

// ── S3 (secrets are write-only) ───────────────────────────────────────────────
int s3Id = 0;
if (s3Endpoint != "none")
{
    var s3Settings = new
    {
        type = "S3", endpoint = s3Endpoint, region = "us-east-1", bucket = "async-smoke", prefix = "smoke",
        accessKeyId = "asynctest", secretAccessKey = "asynctest-secret", forcePathStyle = true,
    };
    var s3 = await Send(HttpMethod.Post, "/api/storage-backends", new { name = "Smoke S3", settings = s3Settings }, admin);
    Check(s3.Status == HttpStatusCode.OK, $"create S3 back-end → {s3.Status} {s3.Body?["message"]}");
    s3Id = s3.Body?["id"]?.GetValue<int>() ?? 0;
    Check(s3.Body?["settings"]?["secretAccessKey"] is null, "secret access key is not returned");
    Check(s3.Body?["secretsSet"]?.AsArray().Any(x => x?.GetValue<string>() == "SecretAccessKey") == true, "secretsSet reports the stored key");

    var missing = await Send(HttpMethod.Post, $"/api/storage-backends/{s3Id}/test", token: admin);
    Check(missing.Body?["error"]?.GetValue<string>().Contains("does not exist") == true, $"missing bucket explained → {missing.Body?["error"]}");

    // ASync never creates buckets. The test server (SeaweedFS, no auth) accepts an anonymous create from this machine.
    using (var create = await new HttpClient().PutAsync(s3Endpoint.Replace("host.docker.internal", "localhost") + "/async-smoke", null))
        Check(create.IsSuccessStatusCode || create.StatusCode == HttpStatusCode.Conflict, $"test bucket created → {create.StatusCode}");
    var s3Test = await Send(HttpMethod.Post, $"/api/storage-backends/{s3Id}/test", token: admin);
    Check(s3Test.Body?["ok"]?.GetValue<bool>() == true, $"S3 test → {s3Test.Body?["error"] ?? "ok"}");

    var renamed = await Send(HttpMethod.Put, $"/api/storage-backends/{s3Id}",
        new { name = "Smoke S3 renamed", settings = s3Settings with { secretAccessKey = (string?)null } }, admin);
    Check(renamed.Status == HttpStatusCode.OK, $"update without re-typing the secret → {renamed.Status}");
    var s3Retest = await Send(HttpMethod.Post, $"/api/storage-backends/{s3Id}/test", token: admin);
    Check(s3Retest.Body?["ok"]?.GetValue<bool>() == true, "stored secret was kept: test still passes");
}

// ── Another user ──────────────────────────────────────────────────────────────
var bobName = $"bob-{Guid.NewGuid():N}"[..12];
await Send(HttpMethod.Post, "/api/users", new { username = bobName, password = "bob-password", isAdmin = false }, admin);
var bob = await SignIn(bobName, "bob-password");
Check(bob is not null, "second user signs in");
var bobLocal = await Send(HttpMethod.Post, "/api/storage-backends", new { name = "x", settings = localSettings }, bob);
Check(bobLocal.Status == HttpStatusCode.BadRequest, $"user cannot create a Local back-end → {bobLocal.Body?["message"]}");
var bobList = await Send(HttpMethod.Get, "/api/storage-backends", token: bob);
Check(bobList.Body?.AsArray().Count == 0, "user does not see the admin's personal back-ends");
var bobSteal = await Send(HttpMethod.Post, "/api/storage-backends/test",
    new { settings = new { type = "WebDav", url = "http://attacker.example/" }, backendId = localId }, bob);
Check(bobSteal.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, $"user cannot test with someone else's stored secrets → {bobSteal.Status}");
var bobDelete = await Send(HttpMethod.Delete, $"/api/storage-backends/{localId}", token: bob);
Check(bobDelete.Status == HttpStatusCode.NotFound, $"user cannot delete it → {bobDelete.Status}");

// ── Clean up ──────────────────────────────────────────────────────────────────
Check((await Send(HttpMethod.Delete, $"/api/storage-backends/{localId}", token: admin)).Status == HttpStatusCode.NoContent, "delete Local back-end");
if (s3Id > 0) Check((await Send(HttpMethod.Delete, $"/api/storage-backends/{s3Id}", token: admin)).Status == HttpStatusCode.NoContent, "delete S3 back-end");

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
