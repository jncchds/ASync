// End-to-end smoke test of sign-in and device provisioning against a running server.
//   dotnet run scripts/smoke-devices.cs -- [server-url] [admin-user] [admin-password]
// Defaults: http://localhost:5100 admin admin-password. On a fresh database it creates the admin account.
// It plays both sides: a person in the web UI (JWT) and a device holding an ECDSA P-256 key.
#:property TargetFramework=net10.0
#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var server = args.Length > 0 ? args[0].TrimEnd('/') : "http://localhost:5100";
var adminUser = args.Length > 1 ? args[1] : "admin";
var adminPassword = args.Length > 2 ? args[2] : "admin-password";
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

// ── Person signs in ───────────────────────────────────────────────────────────
var setup = await Send(HttpMethod.Get, "/api/auth/setup");
Check(setup.Status == HttpStatusCode.OK, $"GET /api/auth/setup → {setup.Status}");
var needsSetup = setup.Body?["needsSetup"]?.GetValue<bool>() == true;
var session = await Send(HttpMethod.Post, needsSetup ? "/api/auth/register" : "/api/auth/login",
    new { username = adminUser, password = adminPassword });
Check(session.Status == HttpStatusCode.OK, $"{(needsSetup ? "register" : "login")} as {adminUser} → {session.Status}");
var userToken = session.Body?["accessToken"]?.GetValue<string>();
if (userToken is null) { Console.WriteLine("Cannot continue without a session."); return 1; }

var badLogin = await Send(HttpMethod.Post, "/api/auth/login", new { username = adminUser, password = "wrong-password" });
Check(badLogin.Status == HttpStatusCode.Unauthorized, $"wrong password → {badLogin.Status}");

// ── Device helpers ────────────────────────────────────────────────────────────
static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

async Task<string?> DeviceSignIn(Guid deviceId, ECDsa key)
{
    var challenge = await Send(HttpMethod.Post, "/api/device-auth/challenge", new { deviceId });
    var nonce = challenge.Body?["nonce"]?.GetValue<string>();
    if (nonce is null) return null;
    var message = Encoding.UTF8.GetBytes($"async-device-auth:v1:{deviceId:D}:{nonce}");
    var signature = Convert.ToBase64String(key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    var token = await Send(HttpMethod.Post, "/api/device-auth/token", new { deviceId, nonce, signature, appVersion = "smoke" });
    return token.Body?["accessToken"]?.GetValue<string>();
}

// ── Flow 1: enrollment code (what the QR code carries) ────────────────────────
using var phoneKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var enrollment = await Send(HttpMethod.Post, "/api/devices/enrollments", new { reusable = false }, userToken);
Check(enrollment.Status == HttpStatusCode.OK, $"create enrollment code → {enrollment.Status}");
var code = enrollment.Body?["code"]?.GetValue<string>() ?? "";
Check(enrollment.Body?["pairUri"]?.GetValue<string>().StartsWith("async://pair?s=") == true, "pair URI has the async:// scheme");

var enrolled = await Send(HttpMethod.Post, "/api/device-auth/enroll",
    new { code = code.ToLowerInvariant(), name = "Smoke phone", platform = "IOS", publicKey = PublicKey(phoneKey) });
Check(enrolled.Status == HttpStatusCode.OK, $"enroll with code (typed lowercase) → {enrolled.Status}");
var phoneId = Guid.Parse(enrolled.Body?["deviceId"]?.GetValue<string>() ?? Guid.Empty.ToString());

using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var reused = await Send(HttpMethod.Post, "/api/device-auth/enroll",
    new { code, name = "Second phone", platform = "Android", publicKey = PublicKey(otherKey) });
Check(reused.Status == HttpStatusCode.BadRequest, $"one-time code used twice → {reused.Status}");

var phoneToken = await DeviceSignIn(phoneId, phoneKey);
Check(phoneToken is not null, "device signs in by signing a challenge");
var self = await Send(HttpMethod.Get, "/api/device-auth/self", token: phoneToken);
Check(self.Status == HttpStatusCode.OK && self.Body?["name"]?.GetValue<string>() == "Smoke phone", $"device reads /self → {self.Status}");

var deviceOnUserApi = await Send(HttpMethod.Get, "/api/devices", token: phoneToken);
Check(deviceOnUserApi.Status == HttpStatusCode.Forbidden, $"device token cannot manage devices → {deviceOnUserApi.Status}");

var impostor = await DeviceSignIn(phoneId, otherKey);
Check(impostor is null, "a different key cannot sign in as the device");

// ── Flow 2: device code (headless CLI) ────────────────────────────────────────
using var cliKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var started = await Send(HttpMethod.Post, "/api/device-auth/device-code", new { name = "Smoke NAS", platform = "Cli", publicKey = PublicKey(cliKey) });
Check(started.Status == HttpStatusCode.OK, $"start device-code flow → {started.Status}");
var deviceCode = started.Body?["deviceCode"]?.GetValue<string>() ?? "";
var userCode = started.Body?["userCode"]?.GetValue<string>() ?? "";
Console.WriteLine($"      user code {userCode}, verification link {started.Body?["verificationUri"]}");

var poll1 = await Send(HttpMethod.Post, "/api/device-auth/device-code/poll", new { deviceCode });
Check(poll1.Body?["status"]?.GetValue<string>() == "Pending", $"poll before approval → {poll1.Body?["status"]}");

var pending = await Send(HttpMethod.Get, $"/api/devices/approvals/{userCode}", token: userToken);
Check(pending.Status == HttpStatusCode.OK && pending.Body?["name"]?.GetValue<string>() == "Smoke NAS", $"approval page sees the request → {pending.Status}");

var approve = await Send(HttpMethod.Post, "/api/devices/approvals", new { userCode, approve = true }, userToken);
Check(approve.Status == HttpStatusCode.OK, $"user approves → {approve.Status}");

var poll2 = await Send(HttpMethod.Post, "/api/device-auth/device-code/poll", new { deviceCode });
Check(poll2.Body?["status"]?.GetValue<string>() == "Approved", $"poll after approval → {poll2.Body?["status"]}");
var cliId = Guid.Parse(poll2.Body?["device"]?["deviceId"]?.GetValue<string>() ?? Guid.Empty.ToString());
var cliToken = await DeviceSignIn(cliId, cliKey);
Check(cliToken is not null, "approved CLI device signs in");

// ── Revocation ────────────────────────────────────────────────────────────────
var revoke = await Send(HttpMethod.Delete, $"/api/devices/{phoneId}", token: userToken);
Check(revoke.Status == HttpStatusCode.NoContent, $"user revokes the phone → {revoke.Status}");
var afterRevoke = await Send(HttpMethod.Get, "/api/device-auth/self", token: phoneToken);
Check(afterRevoke.Status == HttpStatusCode.Unauthorized, $"revoked device's existing token stops working → {afterRevoke.Status}");
Check(await DeviceSignIn(phoneId, phoneKey) is null, "revoked device cannot sign in again");
var cliStill = await Send(HttpMethod.Get, "/api/device-auth/self", token: cliToken);
Check(cliStill.Status == HttpStatusCode.OK, $"other devices keep working → {cliStill.Status}");

// ── Misc ──────────────────────────────────────────────────────────────────────
var health = await Send(HttpMethod.Get, "/health");
Check(health.Status == HttpStatusCode.OK, $"GET /health → {health.Status}");
using var spa = await http.GetAsync("/devices/approve?code=X");
Check(spa.IsSuccessStatusCode && (await spa.Content.ReadAsStringAsync()).Contains("<div id=\"root\">"), "SPA served for client-side routes");

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
