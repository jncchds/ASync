// End-to-end smoke test of folders and snapshot browsing against a running server: creates a Transport Backup
// folder, enrolls a device that backs up a small tree through the storage gateway, then browses and downloads the
// snapshot the way the web UI does. Also checks the refusals (end-to-end folder, two-way folder, other users).
//   dotnet run scripts/smoke-snapshots.cs -- [server-url] [admin-user] [admin-password]
// Defaults: http://localhost:5100 admin admin-password. Everything it creates is removed at the end.
#:property TargetFramework=net10.0
#:property PublishAot=false
#:project ../src/ASync.Client.Core/ASync.Client.Core.csproj

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ASync.Client.Core;
using ASync.Client.Core.Sync;
using ASync.Contracts;

var server = args.Length > 0 ? args[0].TrimEnd('/') : "http://localhost:5100";
var adminUser = args.Length > 1 ? args[1] : "admin";
var adminPassword = args.Length > 2 ? args[2] : "admin-password";
var http = new HttpClient { BaseAddress = new Uri(server) };
var failures = 0;
var run = Guid.NewGuid().ToString("N")[..8];

void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

async Task<(HttpStatusCode Status, JsonNode? Body, byte[] Raw, HttpContentHeaders? Headers)> Send(HttpMethod method, string path, object? body = null, string? token = null)
{
    using var req = new HttpRequestMessage(method, path);
    if (body is not null) req.Content = JsonContent.Create(body);
    if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var res = await http.SendAsync(req);
    var raw = await res.Content.ReadAsByteArrayAsync();
    var isJson = res.Content.Headers.ContentType?.MediaType?.Contains("json") == true;
    return (res.StatusCode, isJson && raw.Length > 0 ? JsonNode.Parse(raw) : null, raw, res.Content.Headers);
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

var work = Directory.CreateTempSubdirectory("async-smoke-snapshots-");
var source = Directory.CreateDirectory(Path.Combine(work.FullName, "source")).FullName;
var big = new byte[3 * 1024 * 1024];
new Random(42).NextBytes(big);
File.WriteAllText(Path.Combine(source, "readme.txt"), "Hello from the snapshot smoke test.");
Directory.CreateDirectory(Path.Combine(source, "docs/deep"));
File.WriteAllText(Path.Combine(source, "docs/notes – ünïcode.md"), string.Concat(Enumerable.Repeat("notes ", 1000)));
File.WriteAllBytes(Path.Combine(source, "docs/deep/big.bin"), big);
Directory.CreateDirectory(Path.Combine(source, "empty"));

var storage = await Send(HttpMethod.Post, "/api/storage-backends",
    new { name = $"Smoke snapshots {run}", settings = new { type = "Local", path = $"/data/smoke-snapshots-{run}" } }, admin);
Check(storage.Status == HttpStatusCode.OK, $"create Local storage → {storage.Status} {storage.Body?["message"]}");
var storageId = storage.Body?["id"]?.GetValue<int>() ?? 0;

async Task<int> CreateFolder(string name, string mode, string encryption)
{
    var r = await Send(HttpMethod.Post, "/api/folders", new
    {
        name, mode, encryption, kind = "Files", storageBackendId = storageId, rootPath = name.ToLowerInvariant(), intervalMinutes = 60,
    }, admin);
    Check(r.Status == HttpStatusCode.OK, $"create {mode}/{encryption} folder \"{name}\" → {r.Status} {r.Body?["message"]}");
    return r.Body?["id"]?.GetValue<int>() ?? 0;
}

var backupId = await CreateFolder("Backup", "Backup", "Transport");
var e2eId = await CreateFolder("Secret", "Backup", "EndToEnd");
var twoWayId = await CreateFolder("Shared", "TwoWay", "Transport");

// ── A device backs up through the gateway ─────────────────────────────────────
var enrollment = await Send(HttpMethod.Post, "/api/devices/enrollments", new { reusable = false }, admin);
using var api = new AsyncApiClient(server);
var identity = await api.EnrollAsync(enrollment.Body!["code"]!.GetValue<string>(), $"smoke-snapshots-{run}", DevicePlatform.Cli);
using var device = new AsyncApiClient(server, identity);
Check(identity.DeviceId != Guid.Empty, $"device enrolled as {identity.DeviceName}");

var attach = await Send(HttpMethod.Put, $"/api/folders/{backupId}/devices", new { deviceId = identity.DeviceId, localPath = source }, admin);
Check(attach.Status == HttpStatusCode.OK, $"attach the device to the Backup folder → {attach.Status} {attach.Body?["message"]}");

var deviceFolder = (await device.GetFoldersAsync()).Single(f => f.FolderId == backupId);
var store = new FileClientStore(Path.Combine(work.FullName, "client"));
var repo = await RepositoryAccess.OpenAsync(device, store, deviceFolder, new RemoteStorageBackend(device, backupId));
var (made, _) = await new BackupJob(repo, deviceFolder, identity.DeviceId).RunAsync();
await device.ReportAsync(backupId, new SyncReportRequest(true, null, Uploaded: made.Stats.FilesNew));
Check(made.Stats.FilesNew == 3, $"device backed up {made.Stats.FilesNew} files, snapshot {made.Id[..12]}");

// ── Browsing as the owner ─────────────────────────────────────────────────────
var list = await Send(HttpMethod.Get, $"/api/folders/{backupId}/snapshots", token: admin);
var snap = list.Body?.AsArray().FirstOrDefault();
Check(list.Status == HttpStatusCode.OK && list.Body!.AsArray().Count == 1, $"list snapshots → {list.Status}, {list.Body?.AsArray().Count} snapshot(s)");
Check(snap?["id"]?.GetValue<string>() == made.Id && snap["deviceName"]?.GetValue<string>() == identity.DeviceName,
    $"snapshot shows its device name \"{snap?["deviceName"]}\"");
var sid = made.Id;

var root = await Send(HttpMethod.Get, $"/api/folders/{backupId}/snapshots/{sid}/tree", token: admin);
var rootNames = root.Body?["nodes"]?.AsArray().Select(n => n!["name"]!.GetValue<string>()).ToList() ?? [];
Check(rootNames.SequenceEqual(["docs", "empty", "readme.txt"]), $"root lists directories first: {string.Join(", ", rootNames)}");

var docs = await Send(HttpMethod.Get, $"/api/folders/{backupId}/snapshots/{sid}/tree?path=docs", token: admin);
var docNames = docs.Body?["nodes"]?.AsArray().Select(n => n!["path"]!.GetValue<string>()).ToList() ?? [];
Check(docNames.SequenceEqual(["docs/deep", "docs/notes – ünïcode.md"]), $"docs/ lists {string.Join(", ", docNames)}");

var file = await Send(HttpMethod.Get, $"/api/folders/{backupId}/snapshots/{sid}/file?path={Uri.EscapeDataString("docs/deep/big.bin")}", token: admin);
Check(file.Status == HttpStatusCode.OK && file.Raw.AsSpan().SequenceEqual(big), $"download a 3 MB file → {file.Status}, {file.Raw.Length} bytes, identical");
Check(file.Headers?.ContentDisposition?.DispositionType == "attachment" && file.Headers.ContentDisposition.FileNameStar == "big.bin",
    $"file comes as an attachment: {file.Headers?.ContentDisposition}");

var unicode = await Send(HttpMethod.Get, $"/api/folders/{backupId}/snapshots/{sid}/file?path={Uri.EscapeDataString("docs/notes – ünïcode.md")}", token: admin);
Check(unicode.Headers?.ContentDisposition?.FileNameStar == "notes – ünïcode.md", $"non-ASCII file name kept: {unicode.Headers?.ContentDisposition?.FileNameStar}");

var zip = await Send(HttpMethod.Get, $"/api/folders/{backupId}/snapshots/{sid}/zip?path=docs", token: admin);
var entries = new List<string>();
byte[]? zippedBig = null;
if (zip.Status == HttpStatusCode.OK)
{
    using var archive = new ZipArchive(new MemoryStream(zip.Raw), ZipArchiveMode.Read);
    entries = [.. archive.Entries.Select(e => e.FullName).Order()];
    using var entry = new MemoryStream();
    await using (var s = archive.GetEntry("deep/big.bin")!.Open()) await s.CopyToAsync(entry);
    zippedBig = entry.ToArray();
}
Check(entries.SequenceEqual(["deep/big.bin", "notes – ünïcode.md"]) && zippedBig.AsSpan().SequenceEqual(big),
    $"download docs/ as ZIP → {zip.Status}: {string.Join(", ", entries)}");

var whole = await Send(HttpMethod.Get, $"/api/folders/{backupId}/snapshots/{sid}/zip", token: admin);
using (var archive = new ZipArchive(new MemoryStream(whole.Raw), ZipArchiveMode.Read))
    Check(archive.Entries.Any(e => e.FullName == "empty/"), $"whole snapshot as ZIP keeps the empty directory ({archive.Entries.Count} entries)");

var link = await Send(HttpMethod.Post, $"/api/folders/{backupId}/snapshots/{sid}/download-link", new { path = "readme.txt" }, admin);
var url = link.Body?["url"]?.GetValue<string>() ?? "";
var viaLink = await Send(HttpMethod.Get, url);
Check(viaLink.Status == HttpStatusCode.OK && Encoding.UTF8.GetString(viaLink.Raw) == "Hello from the snapshot smoke test.",
    $"download link works without a token → {viaLink.Status}");
var tampered = await Send(HttpMethod.Get, url[..^6] + "AAAAAA");
Check(tampered.Status == HttpStatusCode.Forbidden, $"tampered download link refused → {tampered.Status}");

// ── Refusals ──────────────────────────────────────────────────────────────────
var missing = await Send(HttpMethod.Get, $"/api/folders/{backupId}/snapshots/{sid}/tree?path=nope", token: admin);
Check(missing.Status == HttpStatusCode.NotFound, $"missing path → {missing.Status} {missing.Body?["message"]}");
var escape = await Send(HttpMethod.Get, $"/api/folders/{backupId}/snapshots/{sid}/tree?path=../config", token: admin);
Check(escape.Status == HttpStatusCode.BadRequest, $"'..' in a path → {escape.Status}");
var bogus = await Send(HttpMethod.Get, $"/api/folders/{backupId}/snapshots/{new string('0', 32)}/tree", token: admin);
Check(bogus.Status == HttpStatusCode.NotFound, $"unknown snapshot → {bogus.Status} {bogus.Body?["message"]}");
var fileAsDir = await Send(HttpMethod.Get, $"/api/folders/{backupId}/snapshots/{sid}/zip?path=readme.txt", token: admin);
Check(fileAsDir.Status == HttpStatusCode.BadRequest, $"ZIP of a file → {fileAsDir.Status}");
var e2e = await Send(HttpMethod.Get, $"/api/folders/{e2eId}/snapshots", token: admin);
Check(e2e.Status == HttpStatusCode.Conflict && e2e.Body?["message"]?.GetValue<string>().Contains("async-cli restore") == true,
    $"end-to-end folder → {e2e.Status}: {e2e.Body?["message"]}");
var twoWay = await Send(HttpMethod.Get, $"/api/folders/{twoWayId}/snapshots", token: admin);
Check(twoWay.Status == HttpStatusCode.BadRequest, $"two-way folder → {twoWay.Status}");

var bobName = $"smoke-bob-{run}";
await Send(HttpMethod.Post, "/api/users", new { username = bobName, password = "bob-password-123", isAdmin = false }, admin);
var bob = await SignIn(bobName, "bob-password-123");
var bobList = await Send(HttpMethod.Get, $"/api/folders/{backupId}/snapshots", token: bob);
Check(bobList.Status == HttpStatusCode.NotFound, $"another user cannot see the snapshots → {bobList.Status}");
var bobLink = await Send(HttpMethod.Post, $"/api/folders/{backupId}/snapshots/{sid}/download-link", new { path = "readme.txt" }, bob);
Check(bobLink.Status == HttpStatusCode.NotFound, $"…nor get a download link → {bobLink.Status}");
var deviceToken = await device.GetTokenAsync();
var asDevice = await Send(HttpMethod.Get, $"/api/folders/{backupId}/snapshots", token: deviceToken);
Check(asDevice.Status == HttpStatusCode.Forbidden, $"a device token cannot browse → {asDevice.Status}");

var folders = await Send(HttpMethod.Get, "/api/folders", token: admin);
var status = folders.Body?.AsArray().First(f => f!["id"]!.GetValue<int>() == backupId)!["devices"]![0];
Check(status?["lastSyncOk"]?.GetValue<bool>() == true, $"folder list shows the device's last sync: {status?["lastSyncMessage"]}");

// ── Clean up ──────────────────────────────────────────────────────────────────
foreach (var id in new[] { backupId, e2eId, twoWayId }) await Send(HttpMethod.Delete, $"/api/folders/{id}", token: admin);
await Send(HttpMethod.Delete, $"/api/devices/{identity.DeviceId}", token: admin);
var removed = await Send(HttpMethod.Delete, $"/api/storage-backends/{storageId}", token: admin);
Check(removed.Status is HttpStatusCode.NoContent or HttpStatusCode.OK, $"clean up → {removed.Status}");
work.Delete(recursive: true);

Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
