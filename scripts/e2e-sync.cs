// End-to-end sync test: a running server plus real `async-cli` agents with separate config directories.
//   dotnet run scripts/e2e-sync.cs -- --cli <async-cli.dll> [--server URL] [--admin USER] [--password PW]
//                                     [--work DIR] [--server-container NAME] [--docker-image IMG --docker-network NET]
//                                     [--webhook-host HOST]
// Usually started by scripts/e2e-sync.sh, which builds the CLI, starts a throw-away server stack and cleans up.
// The person's side (admin account, a Local back-end under /data, folders, codes, attaching devices, revoking) goes
// through the REST API; the devices' side is the CLI. With --server-container it also looks at the storage on the
// server's disk; with --docker-image a third agent runs from the async-cli image. Alerts go to a small webhook listener
// on this machine, which the server reaches as --webhook-host (default host.docker.internal); the server must run with
// a short overdue grace (e2e-sync.sh sets Alerts__MinimumGraceMinutes and Alerts__GraceFactor=0).
#:property TargetFramework=net10.0
#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

var opts = new Dictionary<string, string>();
for (var i = 0; i + 1 < args.Length; i += 2) opts[args[i].TrimStart('-')] = args[i + 1];
string Opt(string name, string fallback) => opts.TryGetValue(name, out var v) ? v : fallback;

var server = Opt("server", "http://localhost:5190").TrimEnd('/');
var adminUser = Opt("admin", "admin");
var adminPassword = Opt("password", "admin-password");
var cli = Path.GetFullPath(opts.TryGetValue("cli", out var c) ? c : throw new ArgumentException("Pass --cli <path to async-cli.dll>."));
var work = Path.GetFullPath(Opt("work", Path.Combine(Path.GetTempPath(), $"async-e2e-{Guid.NewGuid():N}")));
var serverContainer = opts.GetValueOrDefault("server-container");
var dockerImage = opts.GetValueOrDefault("docker-image");
var dockerNetwork = opts.GetValueOrDefault("docker-network");
var webhookHost = Opt("webhook-host", "host.docker.internal");
var runId = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
var storageRoot = $"/data/e2e-{runId}";

Directory.CreateDirectory(work);
var http = new HttpClient { BaseAddress = new Uri(server) };
var failures = new List<string>();
var background = new List<Process>();

void Check(bool ok, string what, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok)
    {
        failures.Add(what);
        if (detail is not null) Console.WriteLine("      " + detail.Trim().Replace("\n", "\n      "));
    }
}

void Section(string title) => Console.WriteLine($"\n── {title} ──");

try
{
    return await RunAsync();
}
finally
{
    foreach (var p in background)
        if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
}

async Task<int> RunAsync()
{
    // ── Person: account, storage, codes ───────────────────────────────────────
    Section("setup through the API");
    var setup = await Send(HttpMethod.Get, "/api/auth/setup");
    var needsSetup = setup.Body?["needsSetup"]?.GetValue<bool>() == true;
    var session = await Send(HttpMethod.Post, needsSetup ? "/api/auth/register" : "/api/auth/login", new { username = adminUser, password = adminPassword });
    var token = session.Body?["accessToken"]?.GetValue<string>();
    Check(token is not null, $"{(needsSetup ? "register" : "sign in")} as {adminUser}", session.Raw);
    if (token is null) return 1;

    var backend = await Send(HttpMethod.Post, "/api/storage-backends",
        new { name = $"e2e {runId}", settings = new { type = "Local", path = storageRoot } }, token);
    Check(backend.Status == HttpStatusCode.OK, $"create Local back-end {storageRoot} → {backend.Status}", backend.Raw);
    var backendId = backend.Body?["id"]?.GetValue<int>() ?? 0;
    if (backendId == 0) return 1;

    // ── Devices enroll with the CLI ───────────────────────────────────────────
    var a = new Agent("e2e-A", Path.Combine(work, "A"));
    var b = new Agent("e2e-B", Path.Combine(work, "B"));
    foreach (var agent in new[] { a, b })
    {
        var code = (await Send(HttpMethod.Post, "/api/devices/enrollments", new { reusable = false, label = agent.Name }, token)).Body?["code"]?.GetValue<string>();
        var enrolled = await Cli(agent, ["enroll", "--server", server, "--code", code ?? "", "--name", agent.Name]);
        Check(enrolled.Exit == 0, $"{agent.Name}: enroll with a one-time code", enrolled.Output);
        var again = await Cli(agent, ["enroll", "--server", server, "--code", code ?? "", "--name", agent.Name]);
        Check(again.Exit == 1 && again.Output.Contains("Already enrolled"), $"{agent.Name}: enrolling twice is refused", again.Output);
    }
    var devices = (await Send(HttpMethod.Get, "/api/devices", token: token)).Body?.AsArray() ?? [];
    foreach (var agent in new[] { a, b })
        agent.Id = Guid.Parse(JsonNode.Parse(File.ReadAllText(Path.Combine(agent.Config, "identity.json")))!["deviceId"]!.GetValue<string>());
    Check(new[] { a, b }.All(x => devices.Any(d => d?["id"]?.GetValue<string>() == x.Id.ToString() && d?["name"]?.GetValue<string>() == x.Name)),
        "both devices are listed for the account under their names");
    var status = await Cli(a, ["status"]);
    Check(status.Exit == 0 && status.Output.Contains("No folders yet"), "A: status reaches the server, no folders yet", status.Output);
    Check(File.Exists(Path.Combine(a.Config, "token.json")), "A keeps its access token between commands (token.json)");
    var noFolders = await Cli(a, ["sync"]);
    Check(noFolders.Exit == 1 && noFolders.Output.Contains("No folders yet"), "A: `sync` without folders says so", noFolders.Output);

    // Creates a folder and attaches devices: (agent, local directory name, Mirror direction).
    async Task<int> Folder(string name, string mode, string encryption, params (Agent Agent, string Dir, string? Direction)[] members)
    {
        var folder = await Send(HttpMethod.Post, "/api/folders", new
        {
            name, mode, encryption, kind = "Files", storageBackendId = backendId, rootPath = name, intervalMinutes = 60,
            retention = new { last = 10 },
        }, token);
        Check(folder.Status == HttpStatusCode.OK, $"create {mode} {encryption} folder \"{name}\" → {folder.Status}", folder.Raw);
        var id = folder.Body?["id"]?.GetValue<int>() ?? 0;
        foreach (var (agent, dir, direction) in members)
        {
            var path = agent.Path(dir);
            Directory.CreateDirectory(path);
            var attached = await Send(HttpMethod.Put, $"/api/folders/{id}/devices", new { deviceId = agent.Id, localPath = path, direction }, token);
            Check(attached.Status == HttpStatusCode.OK, $"attach {agent.Name} to \"{name}\"{(direction is null ? "" : $" ({direction})")} → {attached.Status}", attached.Raw);
        }
        return id;
    }

    // ── TwoWay ────────────────────────────────────────────────────────────────
    Section("TwoWay");
    var two = await Folder("two", "TwoWay", "Transport", (a, "two", null), (b, "two", null));
    var folders = await Cli(b, ["folders"]);
    Check(folders.Exit == 0 && folders.Output.Contains("two") && folders.Output.Contains("TwoWay"), "B: `folders` lists the TwoWay folder", folders.Output);
    var unknown = await Cli(b, ["sync", "--folder", "999999"]);
    Check(unknown.Exit == 1 && unknown.Output.Contains("not assigned"), "B: `sync --folder` with someone else's folder id is refused", unknown.Output);

    var big = RandomBytes(3 * 1024 * 1024 + 123);
    Write(a.Path("two", "hello.txt"), "hello from A");
    Write(a.Path("two", "docs/report.bin"), big);
    Write(a.Path("two", "docs/deeper/note.md"), "# note");
    await Sync(a, two, "A uploads three files");
    await Sync(b, two, "B downloads them");
    Check(Same(a.Path("two"), b.Path("two"), "hello.txt", "docs/report.bin", "docs/deeper/note.md"), "A → B: files are byte-identical on B");

    Write(b.Path("two", "from-b.txt"), "written on B");
    Write(b.Path("two", "hello.txt"), "hello, edited on B");
    await Sync(b, two, "B uploads a new file and an edit");
    await Sync(a, two, "A downloads them");
    Check(Same(a.Path("two"), b.Path("two"), "from-b.txt", "hello.txt"), "B → A: the new file and the edit reach A");

    Section("TwoWay conflict");
    Write(a.Path("two", "clash.txt"), "base");
    await Sync(a, two, "A uploads clash.txt");
    await Sync(b, two, "B downloads clash.txt");
    Write(a.Path("two", "clash.txt"), "A's version of the file");
    Write(b.Path("two", "clash.txt"), "B's own longer version of the file");
    await Sync(a, two, "A uploads its edit");
    var conflict = await Sync(b, two, "B syncs its concurrent edit");
    Check(conflict.Output.Contains("1 conflicts"), "B reports one conflict", conflict.Output);
    var copies = Directory.GetFiles(b.Path("two"), "clash (conflict e2e-B *).txt");
    Check(copies.Length == 1 && File.ReadAllText(copies[0]) == "B's own longer version of the file", "B keeps its version as a conflict copy");
    Check(File.ReadAllText(b.Path("two", "clash.txt")) == "A's version of the file", "the original name holds the version synced first (A's)");
    await Sync(a, two, "A syncs");
    Check(copies.Length == 1 && File.Exists(a.Path("two", Path.GetFileName(copies[0]))), "the conflict copy reaches A");

    Section("TwoWay deletion");
    File.Delete(a.Path("two", "from-b.txt"));
    await Sync(a, two, "A uploads a deletion");
    await Sync(b, two, "B applies it");
    Check(!File.Exists(b.Path("two", "from-b.txt")), "the file is gone on B");
    var trashed = Directory.Exists(b.Path("two", ".async/trash"))
        ? Directory.GetFiles(b.Path("two", ".async/trash"), "from-b.txt", SearchOption.AllDirectories) : [];
    Check(trashed.Length == 1 && File.ReadAllText(trashed[0]) == "written on B", "B moved it to .async/trash (content intact)");
    if (serverContainer is not null)
    {
        var remoteTrash = await Docker(["exec", serverContainer, "find", $"{storageRoot}/two/.async/trash", "-name", "from-b.txt"]);
        Check(remoteTrash.Output.Contains("from-b.txt"), "the storage keeps it in .async/trash", remoteTrash.Output);
        var remoteVersions = await Docker(["exec", serverContainer, "find", $"{storageRoot}/two/.async/versions", "-name", "hello.txt~*"]);
        Check(remoteVersions.Output.Contains("hello.txt~"), "the storage keeps the overwritten hello.txt in .async/versions", remoteVersions.Output);
    }
    var nothing = await Sync(a, two, "A syncs again");
    Check(nothing.Output.Contains("0 up, 0 down, 0 deleted"), "a sync without changes transfers nothing", nothing.Output);

    // ── A large file whose upload and download are interrupted (the agent is killed) and continued ──
    Section("TwoWay large file: interrupted upload and download continue");
    var largeMb = int.TryParse(Environment.GetEnvironmentVariable("ASYNC_E2E_LARGE_MB"), out var mb) && mb > 0 ? mb : 200;
    var largeSize = (long)largeMb * 1024 * 1024 + 4321;
    var large = await Folder("large", "TwoWay", "Transport", (a, "large", null), (b, "large", null));
    var largeHash = WriteRandomFile(a.Path("large", "big.bin"), largeSize);

    // A's upload goes in parts (16 MB); the agent is killed once the server has stored two of them.
    var upload = Start(a, ["sync", "--folder", $"{large}", "--progress"]);
    var stored = 0L;
    var killedUpload = await WaitForAsync(() => Task.FromResult(UploadCut()), 120, 0.05);
    bool UploadCut()
    {
        var last = upload.Log.ToString().Split('\n').LastOrDefault(l => l.Contains("upload: "));
        if (last is null) return upload.Process.HasExited;
        stored = long.Parse(last.Split("upload: ")[1].Split(' ')[0].Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
        return stored >= 2 * 16 * 1024 * 1024 || upload.Process.HasExited;
    }
    var cutShort = !upload.Process.HasExited;
    if (cutShort) { upload.Process.Kill(entireProcessTree: true); await upload.Process.WaitForExitAsync(); }
    Check(killedUpload && cutShort && stored < largeSize, $"A's agent is killed during the upload of {largeMb} MB, with {stored:N0} bytes stored",
        upload.Log.ToString());
    if (serverContainer is not null)
    {
        var placed = await Docker(["exec", serverContainer, "sh", "-c", $"test -e '{storageRoot}/large/big.bin' && echo yes || echo no"]);
        Check(placed.Output.Trim() == "no", "the file is not in place on the storage yet", placed.Output);
    }

    var resumedUpload = await Sync(a, large, "A syncs again");
    var resumedLine = resumedUpload.Output.Split("continued 1 upload(s): ").ElementAtOrDefault(1) ?? "";
    var already = long.TryParse(resumedLine.Split(' ')[0], out var x1) ? x1 : -1;
    var sent = long.TryParse(resumedLine.Split(", ").ElementAtOrDefault(1)?.Split(' ')[0], out var x2) ? x2 : -1;
    Check(already >= stored && already < largeSize, $"the upload continues: {already:N0} bytes were already on the server (≥ {stored:N0} acknowledged)", resumedUpload.Output);
    Check(sent == largeSize - already, $"only the rest is sent: {sent:N0} bytes, none of the acknowledged parts again", resumedUpload.Output);
    if (serverContainer is not null)
    {
        var left = await Docker(["exec", serverContainer, "sh", "-c", $"find '{storageRoot}/large' -name '*.async-tmp' | wc -l; stat -c %s '{storageRoot}/large/big.bin'"]);
        Check(left.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries) is [var tmp, var len] && tmp.Trim() == "0" && long.Parse(len.Trim()) == largeSize,
            "on the storage: the whole file, and no staging files left", left.Output);
    }

    // B's download is killed part-way and continues from the partial file in .async/tmp.
    var download = Start(b, ["sync", "--folder", $"{large}"]);
    var partialDir = b.Path("large", ".async", "tmp");
    var have = 0L;
    var killedDownload = await WaitForAsync(() => Task.FromResult(DownloadCut()), 120, 0.05);
    bool DownloadCut()
    {
        if (download.Process.HasExited) return true;
        var part = Directory.Exists(partialDir) ? Directory.GetFiles(partialDir, "*.part").FirstOrDefault() : null;
        have = part is null ? 0 : new FileInfo(part).Length;
        return have >= largeSize / 4;
    }
    var downloadCut = !download.Process.HasExited;
    if (downloadCut) { download.Process.Kill(entireProcessTree: true); await download.Process.WaitForExitAsync(); }
    Check(killedDownload && downloadCut && !File.Exists(b.Path("large", "big.bin")), $"B's agent is killed during the download, with {have:N0} bytes here",
        download.Log.ToString());
    var downloaded = await Sync(b, large, "B syncs again");
    var hereLine = downloaded.Output.Split("continued download(s): ").ElementAtOrDefault(1) ?? "";
    var here = long.TryParse(hereLine.Split(' ')[0], out var x3) ? x3 : -1;
    Check(here >= have && here < largeSize, $"the download continues: {here:N0} bytes were already on B", downloaded.Output);
    Check(File.Exists(b.Path("large", "big.bin")) && HashFile(b.Path("large", "big.bin")) == largeHash, "B's copy is byte-identical to A's");
    Check(!Directory.Exists(partialDir) || Directory.GetFiles(partialDir).Length == 0, "no partial download is left on B");

    Section("pause, set-path, mass-deletion guard");
    var pause = await Cli(b, ["pause", "--folder", $"{two}"]);
    var paused = await Cli(b, ["sync", "--folder", $"{two}"]);
    Check(pause.Exit == 0 && paused.Output.Contains("Paused"), "B: a paused folder is skipped", pause.Output + paused.Output);
    var resume = await Cli(b, ["resume", "--folder", $"{two}"]);
    Check(resume.Exit == 0 && !(await Cli(b, ["folders"])).Output.Contains("paused"), "B: resume", resume.Output);

    var bulk = await Folder("bulk", "TwoWay", "Transport", (a, "bulk", null), (b, "bulk-old", null));
    for (var i = 0; i < 12; i++) Write(a.Path("bulk", $"f{i}.txt"), $"file {i}");
    await Sync(a, bulk, "A uploads 12 files");
    var moved = await Cli(b, ["set-path", "--folder", $"{bulk}", "--path", b.Path("bulk")]);
    Directory.CreateDirectory(b.Path("bulk"));
    Check(moved.Exit == 0 && (await Cli(b, ["folders"])).Output.Contains(b.Path("bulk")), "B: set-path moves the folder", moved.Output);
    await Sync(b, bulk, "B downloads them into the new path");
    Check(Directory.GetFiles(b.Path("bulk")).Length == 12, "B has all 12 files");
    foreach (var f in Directory.GetFiles(a.Path("bulk"))) File.Delete(f);
    var refused = await Cli(a, ["sync", "--folder", $"{bulk}"]);
    Check(refused.Exit == 1 && refused.Output.Contains("--allow-mass-delete"), "deleting all 12 files is refused without --allow-mass-delete", refused.Output);
    var allowed = await Cli(a, ["sync", "--folder", $"{bulk}", "--allow-mass-delete"]);
    Check(allowed.Exit == 0 && allowed.Output.Contains("12 deleted"), "A: --allow-mass-delete applies it", allowed.Output);
    var bRefused = await Cli(b, ["sync", "--folder", $"{bulk}"]);
    Check(bRefused.Exit == 1 && Directory.GetFiles(b.Path("bulk")).Length == 12, "B refuses to delete them all on its side too, and keeps them", bRefused.Output);
    var missing = await Cli(b, ["set-path", "--folder", $"{bulk}", "--path", b.Path("not-there")]);
    var unmounted = await Cli(b, ["sync", "--folder", $"{bulk}"]);
    Check(missing.Exit == 0 && unmounted.Exit == 1 && unmounted.Output.Contains("does not exist"), "a missing local folder (unmounted disk) is not synced", unmounted.Output);

    // ── Mirror ────────────────────────────────────────────────────────────────
    Section("Mirror Upload → Download");
    var mirror = await Folder("mirror", "Mirror", "Transport", (a, "mirror", "Upload"), (b, "mirror", "Download"));
    Write(a.Path("mirror", "one.txt"), "one");
    Write(a.Path("mirror", "sub/two.bin"), RandomBytes(70_000));
    await Sync(a, mirror, "A (Upload) uploads");
    Write(b.Path("mirror", "local-only.txt"), "only on B");
    await Sync(b, mirror, "B (Download) downloads");
    Check(Same(a.Path("mirror"), b.Path("mirror"), "one.txt", "sub/two.bin"), "B is a copy of A");
    Check(!File.Exists(b.Path("mirror", "local-only.txt")), "B's extra file is removed (Download makes it equal)");

    Write(b.Path("mirror", "one.txt"), "changed on B, must not survive");
    await Sync(b, mirror, "B syncs its local edit");
    Check(File.ReadAllText(b.Path("mirror", "one.txt")) == "one", "B's local edit is replaced by the storage's version");
    await Sync(a, mirror, "A syncs");
    Check(File.ReadAllText(a.Path("mirror", "one.txt")) == "one", "A (Upload) is not changed by B");

    File.Delete(a.Path("mirror", "one.txt"));
    Write(a.Path("mirror", "sub/two.bin"), RandomBytes(80_000));
    await Sync(a, mirror, "A uploads a deletion and an edit");
    await Sync(b, mirror, "B follows");
    Check(!File.Exists(b.Path("mirror", "one.txt")) && Same(a.Path("mirror"), b.Path("mirror"), "sub/two.bin"), "the deletion and the edit reach B");

    // ── Files, versions and trash of a plain folder in the web UI ─────────────
    Section("web UI: files, versions and trash (TwoWay)");
    var web = await Folder("webfiles", "TwoWay", "Transport", (a, "webfiles", null), (b, "webfiles", null));
    Write(a.Path("webfiles", "notes/plan.txt"), "v1");
    await Sync(a, web, "A uploads notes/plan.txt (v1)");
    Write(a.Path("webfiles", "notes/plan.txt"), "v2, longer");
    await Sync(a, web, "A uploads an edit (v2)");
    Write(a.Path("webfiles", "notes/plan.txt"), "v3, the longest one");
    Write(a.Path("webfiles", "gone.txt"), "deleted by A");
    Write(a.Path("webfiles", "scrap.txt"), "for the emptied trash");
    await Sync(a, web, "A uploads a second edit (v3) and two more files");
    await Sync(b, web, "B downloads them");
    File.Delete(a.Path("webfiles", "gone.txt"));
    File.Delete(a.Path("webfiles", "scrap.txt"));
    await Sync(a, web, "A deletes gone.txt and scrap.txt");
    await Sync(b, web, "B applies the deletions");
    Check(File.ReadAllText(b.Path("webfiles", "notes/plan.txt")) == "v3, the longest one" && !File.Exists(b.Path("webfiles", "gone.txt")),
        "B has v3 and no gone.txt");

    async Task<(HttpStatusCode Status, byte[] Bytes)> Fetch(string url)
    {
        using var res = await http.GetAsync(url);
        return (res.StatusCode, await res.Content.ReadAsByteArrayAsync());
    }
    async Task<(HttpStatusCode Status, byte[] Bytes)> DownloadPlain(int folderId, string source, string path, string? stamp = null, bool zip = false)
    {
        var link = await Send(HttpMethod.Post, $"/api/folders/{folderId}/files/download-link", new { source, path, stamp, zip }, token);
        var url = link.Body?["url"]?.GetValue<string>();
        return url is null ? (link.Status, []) : await Fetch(url);
    }

    var root = await Send(HttpMethod.Get, $"/api/folders/{web}/files", token: token);
    var rootNames = root.Body?["entries"]?.AsArray().Select(e => e?["name"]?.GetValue<string>()).ToList() ?? [];
    Check(root.Status == HttpStatusCode.OK && rootNames.SequenceEqual(["notes"]), "the root lists only notes/ (no .async, no deleted files)", root.Raw);
    var notes = await Send(HttpMethod.Get, $"/api/folders/{web}/files?path=notes", token: token);
    var plan = notes.Body?["entries"]?.AsArray().SingleOrDefault(e => e?["name"]?.GetValue<string>() == "plan.txt");
    Check(plan?["versions"]?.GetValue<int>() == 2 && plan?["size"]?.GetValue<long>() == "v3, the longest one".Length,
        "notes/plan.txt is listed with its size and 2 versions", notes.Raw);
    var wHistory = await Send(HttpMethod.Get, $"/api/folders/{web}/files/history?path=notes/plan.txt", token: token);
    var versions = wHistory.Body?["versions"]?.AsArray() ?? [];
    Check(versions.Count == 2 && versions[1]?["size"]?.GetValue<long>() == 2, "the history lists v2 and v1, newest first", wHistory.Raw);
    var v1Stamp = versions.Count == 2 ? versions[1]!["stamp"]!.GetValue<string>() : "";
    var v1 = await DownloadPlain(web, "Version", "notes/plan.txt", v1Stamp);
    Check(v1.Status == HttpStatusCode.OK && Encoding.UTF8.GetString(v1.Bytes) == "v1", "v1 downloads through a download link", $"{v1.Status}");
    var current = await DownloadPlain(web, "Current", "notes/plan.txt");
    Check(Encoding.UTF8.GetString(current.Bytes) == "v3, the longest one", "the current file downloads");
    var zipped = await DownloadPlain(web, "Current", "", zip: true);
    var zipNames = new List<string>();
    if (zipped.Status == HttpStatusCode.OK)
        using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zipped.Bytes))) zipNames = [.. archive.Entries.Select(e => e.FullName)];
    Check(zipNames.SequenceEqual(["notes/plan.txt"]), "the folder downloads as a ZIP without .async", string.Join(", ", zipNames));
    var allVersions = await Send(HttpMethod.Get, $"/api/folders/{web}/files/versions", token: token);
    Check(allVersions.Body?.AsArray().Any(f => f?["path"]?.GetValue<string>() == "notes/plan.txt" && f?["versions"]?.GetValue<int>() == 2) == true,
        "the files-with-versions view lists notes/plan.txt", allVersions.Raw);
    var trash = await Send(HttpMethod.Get, $"/api/folders/{web}/files/trash", token: token);
    var trashItems = trash.Body?["groups"]?.AsArray().SelectMany(g => g?["items"]?.AsArray() ?? []).ToList() ?? [];
    var goneStamp = trashItems.FirstOrDefault(i => i?["path"]?.GetValue<string>() == "gone.txt")?["stamp"]?.GetValue<string>();
    Check(trash.Status == HttpStatusCode.OK && goneStamp is not null && trashItems.Count == 2, "the trash lists gone.txt and scrap.txt", trash.Raw);
    var wTrashed = await DownloadPlain(web, "Trash", "gone.txt", goneStamp);
    Check(Encoding.UTF8.GetString(wTrashed.Bytes) == "deleted by A", "a trashed file downloads");

    var escape = await Send(HttpMethod.Get, $"/api/folders/{web}/files?path=../two", token: token);
    var meta = await Send(HttpMethod.Get, $"/api/folders/{web}/files?path=.async/trash", token: token);
    var badStamp = await Send(HttpMethod.Post, $"/api/folders/{web}/files/download-link", new { source = "Version", path = "notes/plan.txt", stamp = "../../x" }, token);
    Check(escape.Status == HttpStatusCode.BadRequest && meta.Status == HttpStatusCode.BadRequest && badStamp.Status == HttpStatusCode.BadRequest,
        "`..`, .async/ and a forged stamp → 400", $"{escape.Status} {meta.Status} {badStamp.Status}");
    var expired = await Fetch("/api/file-downloads?t=forged");
    Check(expired.Status == HttpStatusCode.Forbidden, "a forged download ticket → 403", $"{expired.Status}");
    var snapshotsOfPlain = await Send(HttpMethod.Get, $"/api/folders/{web}/snapshots", token: token);
    Check(snapshotsOfPlain.Status == HttpStatusCode.BadRequest, "the snapshot endpoints still refuse a TwoWay folder (400)", snapshotsOfPlain.Raw);

    // A device holds the folder's lease (as while it syncs): restores are refused, and nothing changes.
    var webDeviceToken = JsonNode.Parse(File.ReadAllText(Path.Combine(a.Config, "token.json")))?["accessToken"]?.GetValue<string>();
    var deviceFiles = await Send(HttpMethod.Get, $"/api/folders/{web}/files", token: webDeviceToken);
    Check(deviceFiles.Status == HttpStatusCode.Forbidden, "a device token gets 403 on the files endpoints", deviceFiles.Raw);
    var restoreBody = new
    {
        items = new object[]
        {
            new { source = "Version", path = "notes/plan.txt", stamp = v1Stamp },
            new { source = "Trash", path = "gone.txt", stamp = goneStamp },
        },
    };
    var held = await Send(HttpMethod.Post, $"/api/sync/folders/{web}/lease", token: webDeviceToken);
    var leaseId = held.Body?["leaseId"]?.GetValue<string>();
    Check(leaseId is not null, "A takes the folder's lease", held.Raw);
    var busy = await Send(HttpMethod.Post, $"/api/folders/{web}/files/restore", restoreBody, token);
    Check(busy.Status == HttpStatusCode.Conflict && busy.Raw.Contains("e2e-A is syncing this folder"), "a restore while A holds the lease → 409 naming A", busy.Raw);
    var stillTrashed = await Send(HttpMethod.Get, $"/api/folders/{web}/files/trash", token: token);
    Check(stillTrashed.Raw.Contains("gone.txt"), "the refused restore changed nothing");
    var released = await Send(HttpMethod.Delete, $"/api/sync/folders/{web}/lease/{leaseId}", token: webDeviceToken);
    Check(released.Status == HttpStatusCode.NoContent, "A releases the lease", released.Raw);

    var wRestored = await Send(HttpMethod.Post, $"/api/folders/{web}/files/restore", restoreBody, token);
    var restoredTo = wRestored.Body?["restoredTo"]?.AsArray().Select(p => p?.GetValue<string>()).ToList() ?? [];
    Check(wRestored.Status == HttpStatusCode.OK && restoredTo.SequenceEqual(["notes/plan.txt", "gone.txt"]) && wRestored.Body?["warning"] is null,
        "restore v1 of notes/plan.txt and the trashed gone.txt (no warning: no Mirror Upload device)", wRestored.Raw);
    var wAfter = await Send(HttpMethod.Get, $"/api/folders/{web}/files/history?path=notes/plan.txt", token: token);
    Check(wAfter.Body?["versions"]?.AsArray().Any(v => v?["size"]?.GetValue<long>() == "v3, the longest one".Length) == true,
        "v3 became a version (nothing is lost)", wAfter.Raw);
    var wAgain = await Send(HttpMethod.Post, $"/api/folders/{web}/files/restore",
        new { items = new[] { new { source = "Trash", path = "gone.txt", stamp = goneStamp } } }, token);
    Check(wAgain.Status == HttpStatusCode.NotFound, "restoring the same trashed file twice → 404", wAgain.Raw);

    await Sync(b, web, "B pulls the restored files");
    Check(File.ReadAllText(b.Path("webfiles", "notes/plan.txt")) == "v1" && File.ReadAllText(b.Path("webfiles", "gone.txt")) == "deleted by A",
        "B has v1 of notes/plan.txt and gone.txt back");
    await Sync(a, web, "A pulls the restored files");
    Check(File.ReadAllText(a.Path("webfiles", "notes/plan.txt")) == "v1" && File.ReadAllText(a.Path("webfiles", "gone.txt")) == "deleted by A",
        "A (which made the edits and the deletion) has both back too");

    // A conflict: gone.txt exists again, so restoring an older deletion of it is refused, then renamed.
    File.Delete(a.Path("webfiles", "gone.txt"));
    await Sync(a, web, "A deletes gone.txt again");
    Write(a.Path("webfiles", "gone.txt"), "a new gone.txt");
    await Sync(a, web, "A creates a new gone.txt");
    var trash2 = await Send(HttpMethod.Get, $"/api/folders/{web}/files/trash", token: token);
    var gone2 = trash2.Body?["groups"]?.AsArray().SelectMany(g => g?["items"]?.AsArray() ?? [])
        .FirstOrDefault(i => i?["path"]?.GetValue<string>() == "gone.txt")?["stamp"]?.GetValue<string>();
    var clash = await Send(HttpMethod.Post, $"/api/folders/{web}/files/restore",
        new { items = new[] { new { source = "Trash", path = "gone.txt", stamp = gone2 } } }, token);
    Check(clash.Status == HttpStatusCode.Conflict && clash.Body?["conflicts"]?.AsArray().Count == 1, "restoring onto an existing file → 409 with the conflict", clash.Raw);
    var renamed = await Send(HttpMethod.Post, $"/api/folders/{web}/files/restore",
        new { items = new[] { new { source = "Trash", path = "gone.txt", stamp = gone2 } }, onConflict = "Rename" }, token);
    var renamedTo = renamed.Body?["restoredTo"]?[0]?.GetValue<string>() ?? "";
    Check(renamed.Status == HttpStatusCode.OK && renamedTo.StartsWith("gone (restored "), "…and with Rename it is restored next to it", renamed.Raw);
    await Sync(b, web, "B pulls the renamed file");
    Check(File.Exists(b.Path("webfiles", renamedTo)) && File.ReadAllText(b.Path("webfiles", renamedTo)) == "deleted by A"
          && File.ReadAllText(b.Path("webfiles", "gone.txt")) == "a new gone.txt", "B has both gone.txt files");

    var notConfirmed = await Send(HttpMethod.Delete, $"/api/folders/{web}/files/trash", token: token);
    var emptied = await Send(HttpMethod.Delete, $"/api/folders/{web}/files/trash?confirm=true", token: token);
    var trash3 = await Send(HttpMethod.Get, $"/api/folders/{web}/files/trash", token: token);
    Check(notConfirmed.Status == HttpStatusCode.BadRequest && emptied.Status == HttpStatusCode.OK && emptied.Body?["deleted"]?.GetValue<int>() >= 1
          && trash3.Body?["files"]?.GetValue<int>() == 0, "emptying the trash needs confirm=true, then the trash is empty", emptied.Raw + trash3.Raw);

    // Mirror: A uploads (and deleted one.txt above), so a restore is undone by A's next run: the answer warns.
    var mirrorTrash = await Send(HttpMethod.Get, $"/api/folders/{mirror}/files/trash", token: token);
    var oneStamp = mirrorTrash.Body?["groups"]?.AsArray().SelectMany(g => g?["items"]?.AsArray() ?? [])
        .FirstOrDefault(i => i?["path"]?.GetValue<string>() == "one.txt")?["stamp"]?.GetValue<string>();
    var mirrorRestore = await Send(HttpMethod.Post, $"/api/folders/{mirror}/files/restore",
        new { items = new[] { new { source = "Trash", path = "one.txt", stamp = oneStamp } } }, token);
    Check(mirrorRestore.Status == HttpStatusCode.OK && mirrorRestore.Body?["warning"]?.GetValue<string>()?.Contains("e2e-A") == true,
        "restoring in a Mirror folder warns about the Upload device (A)", mirrorRestore.Raw);
    await Sync(b, mirror, "B (Download) pulls the restored one.txt");
    Check(File.ReadAllText(b.Path("mirror", "one.txt")) == "one", "B has one.txt back");

    // ── Backup (Transport) ────────────────────────────────────────────────────
    Section("Backup (Transport)");
    var backup = await Folder("backup", "Backup", "Transport", (a, "backup", null), (b, "backup-b", null));
    // The server maintains Transport folders itself; a new folder is due at once (the stack polls every few seconds).
    var serverRan = await WaitForAsync(async () =>
    {
        var f = (await Send(HttpMethod.Get, $"/api/folders/{backup}", token: token)).Body;
        return f?["lastPrune"]?["by"]?.GetValue<string>() == "server" && f?["lastCheck"]?["ok"]?.GetValue<bool>() == true;
    }, 60, 1);
    Check(serverRan, "the server prunes and checks the new Transport folder on its own schedule",
        (await Send(HttpMethod.Get, $"/api/folders/{backup}", token: token)).Raw);
    Write(a.Path("backup", "photo.raw"), RandomBytes(5 * 1024 * 1024));
    Write(a.Path("backup", "notes/today.txt"), "backed up");
    Write(a.Path("backup", "empty.txt"), "");
    await Sync(a, backup, "A backs up");
    Write(a.Path("backup", "notes/today.txt"), "backed up, second version");
    var second = await Sync(a, backup, "A backs up again");
    Check(second.Output.Contains("1 changed") && second.Output.Contains("2 unchanged"), "the second snapshot reuses unchanged files", second.Output);
    var snaps = await Cli(b, ["snapshots", "--folder", $"{backup}"]);
    Check(snaps.Exit == 0 && snaps.Output.Split('\n').Count(l => l.Contains("e2e-A")) == 2, "B lists A's two snapshots, under A's device name", snaps.Output);
    var restored = Path.Combine(work, "restore-transport");
    var restore = await Cli(b, ["restore", "--folder", $"{backup}", "--target", restored]);
    Check(restore.Exit == 0, "B restores the newest snapshot", restore.Output);
    Check(Same(a.Path("backup"), restored, "photo.raw", "notes/today.txt", "empty.txt"), "the restore is byte-identical to A's folder");
    var part = Path.Combine(work, "restore-part");
    var partial = await Cli(b, ["restore", "--folder", $"{backup}", "--target", part, "--path", "notes"]);
    Check(partial.Exit == 0 && File.Exists(Path.Combine(part, "notes", "today.txt")) && !File.Exists(Path.Combine(part, "photo.raw")),
        "a partial restore (--path notes) restores only that directory", partial.Output);

    // ── Recovery key of a Transport folder: restore without the server, take the repository over ──
    Section("Transport recovery key");
    var recoverNew = await Send(HttpMethod.Post, "/api/folders", new
    {
        name = "recover", mode = "Backup", encryption = "Transport", kind = "Files", storageBackendId = backendId, rootPath = "recover",
        intervalMinutes = 60, retention = new { last = 10 },
    }, token);
    var recoverFolder = recoverNew.Body?["id"]?.GetValue<int>() ?? 0;
    var firstKey = recoverNew.Body?["recoveryKey"]?.GetValue<string>() ?? "";
    Check(recoverNew.Status == HttpStatusCode.OK && firstKey.Length == 64, "creating a Transport Backup folder shows its recovery key", recoverNew.Raw);
    var recoverInfo = (await Send(HttpMethod.Get, $"/api/folders/{recoverFolder}", token: token)).Body;
    Check(recoverInfo?["recoveryKey"] is null && recoverInfo?["recoveryKeyCreatedAt"] is not null,
        "the folder afterwards says when its key was made, never the key", recoverInfo?.ToJsonString());
    Directory.CreateDirectory(a.Path("recover"));
    await Send(HttpMethod.Put, $"/api/folders/{recoverFolder}/devices", new { deviceId = a.Id, localPath = a.Path("recover") }, token);
    Write(a.Path("recover", "album/one.jpg"), RandomBytes(2 * 1024 * 1024));
    Write(a.Path("recover", "letter.txt"), "first draft");
    await Sync(a, recoverFolder, "A backs up the folder");
    Write(a.Path("recover", "letter.txt"), "second draft");
    await Sync(a, recoverFolder, "A backs it up again");

    var deviceTokenJson = JsonNode.Parse(File.ReadAllText(Path.Combine(a.Config, "token.json")));
    var deviceToken = (deviceTokenJson?["accessToken"] ?? deviceTokenJson?["AccessToken"])?.GetValue<string>();
    var byDevice = await Send(HttpMethod.Post, $"/api/folders/{recoverFolder}/recovery-key", new { password = adminPassword }, deviceToken);
    Check(byDevice.Status == HttpStatusCode.Forbidden, $"a device token cannot get a recovery key → {byDevice.Status}", byDevice.Raw);
    var noPassword = await Send(HttpMethod.Post, $"/api/folders/{recoverFolder}/recovery-key", new { confirm = true }, token);
    var badPassword = await Send(HttpMethod.Post, $"/api/folders/{recoverFolder}/recovery-key", new { password = "not the password" }, token);
    Check(noPassword.Status == HttpStatusCode.BadRequest && badPassword.Status == HttpStatusCode.BadRequest,
        "a new recovery key needs the owner's password", noPassword.Raw + badPassword.Raw);
    var reissued = await Send(HttpMethod.Post, $"/api/folders/{recoverFolder}/recovery-key", new { password = adminPassword }, token);
    var recoverKey = reissued.Body?["recoveryKey"]?.GetValue<string>() ?? "";
    Check(reissued.Status == HttpStatusCode.OK && recoverKey.Length == 64 && recoverKey != firstKey
          && reissued.Body?["storagePath"]?.GetValue<string>() == "recover", "the owner gets a new recovery key with the password", reissued.Raw);

    if (serverContainer is not null)
    {
        // Only the storage's contents and the key: a copy of the directory, and an agent that was never enrolled.
        var copy = Path.Combine(work, "recover-storage");
        var copied = await Docker(["cp", $"{serverContainer}:{storageRoot}/recover", copy]);
        Check(copied.Exit == 0 && File.Exists(Path.Combine(copy, "config")), "copy the folder's directory off the storage", copied.Output);
        var offline = new Agent("offline", Path.Combine(work, "offline"));
        var offlineKey = Path.Combine(work, "recover-key.txt");
        File.WriteAllText(offlineKey, recoverKey + "\n");
        var offlineSnaps = await Cli(offline, ["snapshots", "--repo", copy, "--key-file", offlineKey]);
        Check(offlineSnaps.Exit == 0 && offlineSnaps.Output.Split('\n').Count(l => l.Contains("e2e-A")) == 2,
            "offline: `snapshots --repo` lists both snapshots with the recovery key", offlineSnaps.Output);
        var offlineTarget = Path.Combine(work, "restore-offline");
        var offlineRestore = await Cli(offline, ["restore", "--repo", copy, "--target", offlineTarget], new() { ["ASYNC_RECOVERY_KEY"] = recoverKey });
        Check(offlineRestore.Exit == 0 && Same(a.Path("recover"), offlineTarget, "album/one.jpg", "letter.txt"),
            "offline: `restore --repo` is byte-identical, with no server and no enrolled device", offlineRestore.Output);
        Check(!File.Exists(Path.Combine(offline.Config, "identity.json")), "the offline agent has no identity (it never talked to a server)");
        var oldKey = await Cli(offline, ["snapshots", "--repo", copy], new() { ["ASYNC_RECOVERY_KEY"] = firstKey });
        Check(oldKey.Exit == 1 && oldKey.Output.Contains("does not open"), "the replaced recovery key no longer opens it", oldKey.Output);
        var both = await Cli(offline, ["snapshots", "--repo", copy, "--folder", $"{recoverFolder}"]);
        Check(both.Exit == 1 && both.Output.Contains("either --folder"), "`--repo` and `--folder` together are refused", both.Output);
    }

    // A new server (or the same one after losing the folder's row): the folder is created again on the repository.
    var gone = await Send(HttpMethod.Delete, $"/api/folders/{recoverFolder}", token: token);
    Check(gone.Status == HttpStatusCode.NoContent, "delete the folder (its row and server key; the storage stays)", gone.Raw);
    object Recreate(string? key) => new
    {
        name = "recovered", mode = "Backup", encryption = "Transport", kind = "Files", storageBackendId = backendId, rootPath = "recover",
        intervalMinutes = 60, retention = new { last = 10 }, recoveryKey = key,
    };
    var occupied = await Send(HttpMethod.Post, "/api/folders", Recreate(null), token);
    Check(occupied.Status == HttpStatusCode.BadRequest && occupied.Raw.Contains("already a backup repository"),
        "a new folder on the existing repository without its key is refused", occupied.Raw);
    var wrongKey = await Send(HttpMethod.Post, "/api/folders", Recreate(firstKey), token);
    Check(wrongKey.Status == HttpStatusCode.BadRequest && wrongKey.Raw.Contains("does not open"), "…and with a wrong key", wrongKey.Raw);
    var adopted = await Send(HttpMethod.Post, "/api/folders", Recreate(recoverKey), token);
    var recovered = adopted.Body?["id"]?.GetValue<int>() ?? 0;
    Check(adopted.Status == HttpStatusCode.OK && adopted.Body?["recoveryKey"] is null, "with its recovery key, the new folder takes the repository over", adopted.Raw);
    var adoptedSnaps = await Send(HttpMethod.Get, $"/api/folders/{recovered}/snapshots", token: token);
    Check(adoptedSnaps.Body?.AsArray().Count == 2, "the web UI lists the old snapshots of the taken-over folder", adoptedSnaps.Raw);
    await Send(HttpMethod.Put, $"/api/folders/{recovered}/devices", new { deviceId = a.Id, localPath = a.Path("recover") }, token);
    Write(a.Path("recover", "letter.txt"), "third draft");
    var continued = await Sync(a, recovered, "A backs up into the taken-over folder");
    Check(continued.Output.Contains("1 changed") && continued.Output.Contains("1 unchanged"),
        "the backup continues from the old snapshots (unchanged files reused)", continued.Output);
    var history = await Cli(a, ["snapshots", "--folder", $"{recovered}"]);
    Check(history.Exit == 0 && history.Output.Split('\n').Count(l => l.Contains("e2e-A")) == 3, "the history is intact: 3 snapshots", history.Output);
    if (serverContainer is not null)
    {
        var keyFiles = await Docker(["exec", serverContainer, "ls", $"{storageRoot}/recover/keys"]);
        Check(keyFiles.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2,
            "the repository has two key files: the new server key and the recovery key (the old server key is removed)", keyFiles.Output);

        // The admin's disaster recovery: the server's own keys, exported.
        var export = await Docker(["exec", serverContainer, "dotnet", "ASync.Api.dll", "secrets", "export-folder-keys"]);
        var exported = export.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith($"folder-{recovered}="))?.Split('=')[1] ?? "";
        Check(export.Exit == 0 && exported.Length == 64, "`secrets export-folder-keys` prints the folder's server key", export.Output);
        await Docker(["cp", $"{serverContainer}:{storageRoot}/recover", Path.Combine(work, "recover-storage-2")]);
        var withExport = await Cli(new Agent("offline", Path.Combine(work, "offline")),
            ["snapshots", "--repo", Path.Combine(work, "recover-storage-2")], new() { ["ASYNC_RECOVERY_KEY"] = exported });
        Check(withExport.Exit == 0 && withExport.Output.Split('\n').Count(l => l.Contains("e2e-A")) == 3,
            "offline: the exported server key opens the repository too", withExport.Output);

        var serverLog = await Docker(["logs", serverContainer]);
        Check(!serverLog.Output.Contains(recoverKey) && !serverLog.Output.Contains(firstKey) && !serverLog.Output.Contains(recoverKey.Replace("-", "")),
            "the server's log holds no recovery key");
    }

    // ── prune and check ───────────────────────────────────────────────────────
    Section("prune");
    async Task<long> StoredBytes(string folderName)
    {
        if (serverContainer is null) return -1;
        var du = await Docker(["exec", serverContainer, "du", "-sb", $"{storageRoot}/{folderName}/data"]);
        return long.TryParse(du.Output.Split('\t', ' ')[0], out var n) ? n : -1;
    }
    var wrongMode = await Cli(a, ["prune", "--folder", $"{two}"]);
    Check(wrongMode.Exit == 1 && wrongMode.Output.Contains("Only Backup folders"), "`prune` on a TwoWay folder is refused", wrongMode.Output);
    var sizeBefore = await StoredBytes("backup");
    var early = await Cli(a, ["prune", "--folder", $"{backup}"]);
    Check(early.Exit == 0 && early.Output.Contains("Nothing to remove"), "A: `prune` with every snapshot still kept removes nothing", early.Output);
    // Keep only the newest snapshot from now on, and drop the 5 MB file: the next backup forgets the two older snapshots.
    var keepOne = await Send(HttpMethod.Put, $"/api/folders/{backup}", new
    {
        name = "backup", mode = "Backup", encryption = "Transport", kind = "Files", storageBackendId = backendId, rootPath = "backup",
        intervalMinutes = 60, retention = new { last = 1, daily = 0, weekly = 0, monthly = 0, withinHours = (int?)null },
        pruneIntervalDays = 7, checkIntervalDays = 30, checkReadDataPercent = 50,
    }, token);
    Check(keepOne.Status == HttpStatusCode.OK && keepOne.Body?["checkReadDataPercent"]?.GetValue<int>() == 50,
        "the folder's retention and maintenance schedule can be edited", keepOne.Raw);
    File.Delete(a.Path("backup", "photo.raw"));
    var third = await Sync(a, backup, "A backs up without the big file");
    Check(third.Output.Contains("2 old snapshots forgotten"), "the backup forgets the two older snapshots", third.Output);
    if (serverContainer is not null)
        Check(await StoredBytes("backup") >= sizeBefore, "forgetting snapshots alone frees no space on the storage");
    var pruned = await Cli(a, ["prune", "--folder", $"{backup}"]);
    Check(pruned.Exit == 0 && pruned.Output.Contains("Freed") && !pruned.Output.Contains("Nothing to remove"), "A: `prune` reports what it removed", pruned.Output);
    if (serverContainer is not null)
    {
        var sizeAfter = await StoredBytes("backup");
        Check(sizeBefore > 5_000_000 && sizeAfter >= 0 && sizeAfter < sizeBefore - 5_000_000,
            $"the space is actually reclaimed on the storage ({sizeBefore:N0} → {sizeAfter:N0} bytes)", pruned.Output);
    }
    var afterPrune = Path.Combine(work, "restore-after-prune");
    var restoreAfterPrune = await Cli(b, ["restore", "--folder", $"{backup}", "--target", afterPrune, "--snapshot", ShortId(third.Output)]);
    Check(restoreAfterPrune.Exit == 0 && Same(a.Path("backup"), afterPrune, "notes/today.txt", "empty.txt")
          && Directory.GetFiles(afterPrune, "*", SearchOption.AllDirectories).Length == 2,
        "a restore after the prune is still byte-identical", restoreAfterPrune.Output);
    var prunedInfo = (await Send(HttpMethod.Get, $"/api/folders/{backup}", token: token)).Body;
    Check(prunedInfo?["lastPrune"]?["by"]?.GetValue<string>() == "e2e-A" && prunedInfo?["lastPrune"]?["ok"]?.GetValue<bool>() == true
          && prunedInfo?["lastPrune"]?["message"]?.GetValue<string>().Contains("freed") == true,
        "the server shows the device's prune (who, ok, bytes freed)", prunedInfo?["lastPrune"]?.ToJsonString());

    Section("check");
    var good = await Cli(b, ["check", "--folder", $"{backup}", "--read-data", "100"]);
    Check(good.Exit == 0 && good.Output.Contains("OK: no problems found") && good.Output.Contains("read back and verified"),
        "B: `check --read-data 100` passes on the good repository", good.Output);
    var allGood = await Cli(a, ["check"]);
    Check(allGood.Exit == 0 && allGood.Output.Contains("backup…"), "A: `check` without --folder checks every Backup folder of the device", allGood.Output);
    if (serverContainer is not null)
    {
        // Overwrite 16 bytes in the middle of one pack file, as a failing disk or a bad copy would.
        var damage = await Docker(["exec", serverContainer, "sh", "-c",
            $"f=$(find {storageRoot}/backup/data -type f | head -n 1) && printf 'ASYNC-E2E-DAMAGE' | dd of=\"$f\" bs=1 seek=40 conv=notrunc 2>/dev/null && echo altered $f"]);
        Check(damage.Output.Contains("altered"), "alter a pack file on the storage", damage.Output);
        var bad = await Cli(b, ["check", "--folder", $"{backup}", "--read-data", "100"]);
        Check(bad.Exit == 1 && bad.Output.Contains("FAILED") && bad.Output.Contains("damaged"),
            "B: `check` reports the altered pack and exits with 1", bad.Output);
        var badInfo = (await Send(HttpMethod.Get, $"/api/folders/{backup}", token: token)).Body;
        Check(badInfo?["lastCheck"]?["ok"]?.GetValue<bool>() == false && badInfo?["lastCheck"]?["by"]?.GetValue<string>() == "e2e-B"
              && badInfo?["lastCheck"]?["message"]?.GetValue<string>().Contains("problem") == true,
            "the server shows the failed check for the folder", badInfo?["lastCheck"]?.ToJsonString());
    }

    // ── Backup (end-to-end) ───────────────────────────────────────────────────
    Section("Backup (end-to-end)");
    var e2e = await Folder("e2e", "Backup", "EndToEnd", (a, "e2e", null), (b, "e2e-b", null));
    Write(a.Path("e2e", "secret.txt"), "for my eyes only");
    Write(a.Path("e2e", "blob.bin"), RandomBytes(1_500_000));
    var locked = await Cli(a, ["sync", "--folder", $"{e2e}"]);
    Check(locked.Exit == 1 && locked.Output.Contains("init-encryption"), "a backup before init-encryption is refused with a hint", locked.Output);
    var env = new Dictionary<string, string> { ["ASYNC_PASSPHRASE"] = "correct horse battery staple" };
    var init = await Cli(a, ["init-encryption", "--folder", $"{e2e}"], env);
    var recovery = init.Output.Split('\n').Select(l => l.Trim()).SkipWhile(l => !l.Contains("shown only now")).Skip(1)
        .FirstOrDefault(l => l.Length > 0) ?? "";
    Check(init.Exit == 0 && recovery.Length > 20, "A: init-encryption prints a recovery key", init.Output);
    var twice = await Cli(a, ["init-encryption", "--folder", $"{e2e}"], env);
    Check(twice.Exit == 1, "init-encryption a second time is refused", twice.Output);
    await Sync(a, e2e, "A backs up end-to-end encrypted");
    if (serverContainer is not null)
    {
        var plain = await Docker(["exec", serverContainer, "grep", "-rl", "for my eyes only", $"{storageRoot}/e2e"]);
        Check(plain.Output.Trim().Length == 0, "the storage holds no plaintext", plain.Output);
    }
    var bLocked = await Cli(b, ["restore", "--folder", $"{e2e}", "--target", Path.Combine(work, "nope")]);
    Check(bLocked.Exit == 1 && bLocked.Output.Contains("unlock"), "B cannot restore before unlocking", bLocked.Output);
    var wrong = await Cli(b, ["unlock", "--folder", $"{e2e}"], new() { ["ASYNC_PASSPHRASE"] = "wrong passphrase" });
    Check(wrong.Exit == 1, "B: unlock with a wrong passphrase fails", wrong.Output);
    var keyFile = Path.Combine(work, "recovery.txt");
    File.WriteAllText(keyFile, recovery + "\n");
    var unlock = await Cli(b, ["unlock", "--folder", $"{e2e}", "--passphrase-file", keyFile]);
    Check(unlock.Exit == 0, "B: unlock with the recovery key", unlock.Output);
    var e2eTarget = Path.Combine(work, "restore-e2e");
    var e2eRestore = await Cli(b, ["restore", "--folder", $"{e2e}", "--target", e2eTarget]);
    Check(e2eRestore.Exit == 0 && Same(a.Path("e2e"), e2eTarget, "secret.txt", "blob.bin"), "B restores A's end-to-end snapshot byte-identically", e2eRestore.Output);
    Write(b.Path("e2e-b", "b.txt"), "B's own backup");
    await Sync(b, e2e, "B backs up into the same end-to-end repository");
    var e2eSnaps = await Cli(a, ["snapshots", "--folder", $"{e2e}"]);
    Check(e2eSnaps.Output.Contains("e2e-B"), "A sees B's snapshot", e2eSnaps.Output);

    // ── run mode: live updates ────────────────────────────────────────────────
    Section("run mode (SignalR)");
    var runB = Start(b, ["run", "--verbose"]);
    Check(await WaitFor(() => runB.Log.Contains("Syncing \"two\""), 30), "B: `run` starts a worker per folder", runB.Log.ToString());
    await Task.Delay(TimeSpan.FromSeconds(3)); // let the initial runs finish
    Write(a.Path("two", "live.txt"), "pushed while B runs");
    await Sync(a, two, "A uploads live.txt (and tells the server)");
    Check(await WaitFor(() => File.Exists(b.Path("two", "live.txt")), 30) && File.ReadAllText(b.Path("two", "live.txt")) == "pushed while B runs",
        "B's agent downloads it after folder.changed, without polling", runB.Log.ToString());
    Write(b.Path("two", "watched.txt"), "seen by the file watcher");
    var watched = await WaitForAsync(async () =>
    {
        await Cli(a, ["sync", "--folder", $"{two}"]);
        return File.Exists(a.Path("two", "watched.txt"));
    }, 45, pollSeconds: 3);
    Check(watched, "B's agent uploads a local change it noticed by itself", runB.Log.ToString());

    // End-to-end folders are pruned and checked by a device's agent: the server has no key for them.
    var agentRan = await WaitForAsync(async () =>
    {
        var f = (await Send(HttpMethod.Get, $"/api/folders/{e2e}", token: token)).Body;
        return f?["lastPrune"]?["by"]?.GetValue<string>() == "e2e-B" && f?["lastPrune"]?["ok"]?.GetValue<bool>() == true
               && f?["lastCheck"]?["by"]?.GetValue<string>() == "e2e-B" && f?["lastCheck"]?["ok"]?.GetValue<bool>() == true;
    }, 90, 1);
    Check(agentRan && runB.Log.Contains("\"e2e\": prune:") && runB.Log.Contains("\"e2e\": check:"),
        "B's agent prunes and checks the end-to-end folder on schedule and reports both", runB.Log.ToString());
    var e2eCheck = await Cli(a, ["check", "--folder", $"{e2e}", "--read-data", "100"]);
    Check(e2eCheck.Exit == 0 && e2eCheck.Output.Contains("OK: no problems found"), "A: `check` works on the end-to-end folder too", e2eCheck.Output);
    var e2ePrune = await Cli(a, ["prune", "--folder", $"{e2e}"]);
    Check(e2ePrune.Exit == 0 && e2ePrune.Output.Contains("Nothing to remove"), "A: `prune` works on the end-to-end folder too", e2ePrune.Output);

    // A folder attached while the agent runs (config.changed).
    var late = await Folder("late", "TwoWay", "Transport", (a, "late", null));
    Write(a.Path("late", "x.txt"), "late folder");
    await Sync(a, late, "A fills a new folder");
    var attachB = await Send(HttpMethod.Put, $"/api/folders/{late}/devices", new { deviceId = b.Id, localPath = b.Path("late") }, token);
    Directory.CreateDirectory(b.Path("late"));
    Check(attachB.Status == HttpStatusCode.OK, "attach B to a new folder while it runs");
    Check(await WaitFor(() => File.Exists(b.Path("late", "x.txt")), 30), "B's agent picks up the new folder (config.changed) and syncs it", runB.Log.ToString());

    // ── Browsing a running agent's folders from the web UI ────────────────────
    Section("browsing B's folders through the API");
    string? BrowseState(JsonNode? list, Agent agent) =>
        list?.AsArray().FirstOrDefault(d => d?["id"]?.GetValue<string>() == agent.Id.ToString())?["browse"]?.GetValue<string>();
    var deviceList = (await Send(HttpMethod.Get, "/api/devices", token: token)).Body;
    Check(BrowseState(deviceList, b) == "Available" && BrowseState(deviceList, a) == "Offline",
        $"the device list says B can be browsed, A (not running) is offline → {BrowseState(deviceList, b)}, {BrowseState(deviceList, a)}", deviceList?.ToJsonString());
    Directory.CreateDirectory(b.Path("pick", "inner"));
    Write(b.Path("pick", "readme.txt"), "12345");
    var pickDir = Path.GetFullPath(b.Path("pick"));
    var browsed = await Send(HttpMethod.Get, $"/api/devices/{b.Id}/browse?path={Uri.EscapeDataString(pickDir)}", token: token);
    var entries = browsed.Body?["entries"]?.AsArray() ?? [];
    Check(browsed.Status == HttpStatusCode.OK && browsed.Body?["path"]?.GetValue<string>() == pickDir
          && entries.Select(e => e?["name"]?.GetValue<string>()).SequenceEqual(["inner", "readme.txt"])
          && entries[1]?["kind"]?.GetValue<string>() == "File" && entries[1]?["size"]?.GetValue<long>() == 5,
        "B's agent lists a directory for the web UI: directories first, files with their size", browsed.Raw);
    var homeListing = await Send(HttpMethod.Get, $"/api/devices/{b.Id}/browse", token: token);
    Check(homeListing.Status == HttpStatusCode.OK && homeListing.Body?["places"]?.AsArray().Any(p => p?["kind"]?.GetValue<string>() == "Home") == true,
        "without a path it starts at B's home, with its starting points", homeListing.Raw);
    var made = await Send(HttpMethod.Post, $"/api/devices/{b.Id}/browse/mkdir", new { path = pickDir, name = "target" }, token);
    var picked = made.Body?["path"]?.GetValue<string>();
    Check(made.Status == HttpStatusCode.OK && picked == Path.Combine(pickDir, "target") && Directory.Exists(picked),
        "a new folder is created on B from the picker", made.Raw);
    var offlineBrowse = await Send(HttpMethod.Get, $"/api/devices/{a.Id}/browse", token: token);
    Check(offlineBrowse.Status == HttpStatusCode.Conflict && offlineBrowse.Raw.Contains("offline"),
        $"browsing A, which is not running, is a 409 that says it is offline → {offlineBrowse.Status}", offlineBrowse.Raw);
    var bDeviceToken = JsonNode.Parse(File.ReadAllText(Path.Combine(b.Config, "token.json")))?["accessToken"]?.GetValue<string>();
    var browseByDevice = await Send(HttpMethod.Get, $"/api/devices/{b.Id}/browse", token: bDeviceToken);
    Check(browseByDevice.Status == HttpStatusCode.Forbidden, $"a device token cannot browse → {browseByDevice.Status}", browseByDevice.Raw);

    // The picked path becomes the folder's local path, and the folder syncs there.
    var pickedFolder = await Folder("picked", "TwoWay", "Transport", (a, "picked", null));
    var attachPicked = await Send(HttpMethod.Put, $"/api/folders/{pickedFolder}/devices", new { deviceId = b.Id, localPath = picked }, token);
    Check(attachPicked.Status == HttpStatusCode.OK, "attach B to a folder at the picked path", attachPicked.Raw);
    Write(a.Path("picked", "chosen.txt"), "synced to the picked folder");
    await Sync(a, pickedFolder, "A uploads into the folder");
    Check(await WaitFor(() => File.Exists(Path.Combine(picked ?? "", "chosen.txt")), 30), "B's agent syncs it into the picked folder", runB.Log.ToString());

    // Turned off on B (routed to its running agent): the server knows, and refuses with a clear message.
    var off = await Cli(b, ["browsing", "--off"]);
    Check(off.Exit == 0 && off.Output.Contains("no longer"), "B: `browsing --off` through the running agent", off.Output);
    var turnedOff = await WaitForAsync(async () => BrowseState((await Send(HttpMethod.Get, "/api/devices", token: token)).Body, b) == "TurnedOff", 15, 0.5);
    var offBrowse = await Send(HttpMethod.Get, $"/api/devices/{b.Id}/browse", token: token);
    Check(turnedOff && offBrowse.Status == HttpStatusCode.Conflict && offBrowse.Raw.Contains("turned off"),
        $"the device list says turned off, and browsing B is a 409 that says so → {offBrowse.Status}", offBrowse.Raw);
    var on = await Cli(b, ["browsing", "--on"]);
    var shown = await Cli(b, ["browsing"]);
    Check(on.Exit == 0 && shown.Output.Contains("allowed"), "B: `browsing --on` turns it back on", shown.Output);

    // ── The Docker image ──────────────────────────────────────────────────────
    if (dockerImage is not null && dockerNetwork is not null)
    {
        Section($"agent in Docker ({dockerImage})");
        var reusable = (await Send(HttpMethod.Post, "/api/devices/enrollments", new { reusable = true, validMinutes = 30, label = "e2e docker" }, token)).Body?["code"]?.GetValue<string>();
        var dockerData = Path.Combine(work, "docker-data");
        Directory.CreateDirectory(Path.Combine(dockerData, "two"));
        Directory.CreateDirectory(Path.Combine(work, "docker-config")); // else Docker creates it, owned by root
        var name = $"async-e2e-cli-{runId}";
        var uid = (await Exec("id", ["-u"])).Output.Trim();
        var gid = (await Exec("id", ["-g"])).Output.Trim();
        var started = await Docker(["run", "-d", "--name", name, "--network", dockerNetwork, "--user", $"{uid}:{gid}",
            "-e", "ASYNC_SERVER=http://async-server:8080", "-e", $"ASYNC_ENROLL_CODE={reusable}", "-e", "ASYNC_DEVICE_NAME=e2e-docker",
            "-v", $"{Path.Combine(work, "docker-config")}:/config", "-v", $"{dockerData}:/data", dockerImage, "run", "--verbose"]);
        Check(started.Exit == 0, "start the async-cli container (enrolls from ASYNC_ENROLL_CODE)", started.Output);
        try
        {
            // The container's identity lands in the mounted /config.
            var identityFile = Path.Combine(work, "docker-config", "identity.json");
            await WaitFor(() => File.Exists(identityFile), 60);
            var dockerId = File.Exists(identityFile)
                ? Guid.Parse(JsonNode.Parse(File.ReadAllText(identityFile))!["deviceId"]!.GetValue<string>()) : Guid.Empty;
            var list = (await Send(HttpMethod.Get, "/api/devices", token: token)).Body?.AsArray() ?? [];
            Check(list.Any(d => d?["id"]?.GetValue<string>() == dockerId.ToString() && d?["platform"]?.GetValue<string>() == "Cli"),
                "the container enrolled itself (platform Cli)", (await Docker(["logs", name])).Output);
            var attachDocker = await Send(HttpMethod.Put, $"/api/folders/{two}/devices", new { deviceId = dockerId, localPath = "/data/two" }, token);
            Check(attachDocker.Status == HttpStatusCode.OK, "attach the container to the TwoWay folder at /data/two", attachDocker.Raw);
            Check(await WaitFor(() => File.Exists(Path.Combine(dockerData, "two", "docs", "report.bin")), 60)
                  && Same(a.Path("two"), Path.Combine(dockerData, "two"), "docs/report.bin", "live.txt"),
                "the container downloads the folder into its /data mount", (await Docker(["logs", name])).Output);
        }
        finally
        {
            await Docker(["rm", "-f", name]);
        }
    }

    // ── Alerts: overdue and back to normal, by webhook ────────────────────────
    Section("alerts (webhook)");
    using (var hook = new WebhookListener())
    {
        var hookUrl = $"http://{webhookHost}:{hook.Port}/hook";
        var badScheme = await Send(HttpMethod.Post, "/api/alerts/channels", new { name = "ftp", settings = new { type = "Webhook", url = "ftp://example.com/x" } }, token);
        Check(badScheme.Status == HttpStatusCode.BadRequest, "a webhook with a non-http(s) address is refused", badScheme.Raw);
        var channel = await Send(HttpMethod.Post, "/api/alerts/channels",
            new { name = "e2e hook", settings = new { type = "Webhook", url = hookUrl, authorization = "Bearer e2e-hook-secret" } }, token);
        var channelId = channel.Body?["id"]?.GetValue<int>() ?? 0;
        Check(channel.Status == HttpStatusCode.OK && channel.Body?["settings"]?["authorization"] is null
              && channel.Body?["secretsSet"]?.AsArray().Any(x => x?.GetValue<string>() == "Authorization") == true,
            "add a webhook channel (its Authorization header is stored, never shown)", channel.Raw);
        var tested = await Send(HttpMethod.Post, $"/api/alerts/channels/{channelId}/test", token: token);
        var testHit = await WaitFor(() => hook.Received.Any(r => r.Body?["event"]?.GetValue<string>() == "test"), 30);
        Check(tested.Body?["ok"]?.GetValue<bool>() == true && testHit
              && hook.Received.First(r => r.Body?["event"]?.GetValue<string>() == "test").Authorization == "Bearer e2e-hook-secret",
            "\"Send test\" reaches the webhook, with the Authorization header", tested.Raw);
        await Cli(a, ["status"]); // refreshes A's cached access token
        var hookDeviceToken = JsonNode.Parse(File.ReadAllText(Path.Combine(a.Config, "token.json")))?["accessToken"]?.GetValue<string>();
        var deviceCalls = await Send(HttpMethod.Get, "/api/alerts/channels", token: hookDeviceToken);
        Check(deviceCalls.Status == HttpStatusCode.Forbidden, $"a device token cannot read alert channels → {deviceCalls.Status}", deviceCalls.Raw);

        var watch = await Folder("watch", "TwoWay", "Transport", (a, "watch", null));
        Write(a.Path("watch", "w.txt"), "watched");
        await Sync(a, watch, "A syncs the watched folder once");
        bool Mentions(JsonNode? body, string list, string kind) =>
            body?[list]?.AsArray().Any(i => i?["folderName"]?.GetValue<string>() == "watch" && i?["kind"]?.GetValue<string>() == kind
                                             && i?["deviceName"]?.GetValue<string>() == a.Name) == true;
        var overdue = await WaitFor(() => hook.Received.Any(r => Mentions(r.Body, "opened", "overdue")), 120);
        Check(overdue, "after the (shortened) grace, the webhook gets an overdue alert for A on \"watch\"",
            string.Join("\n", hook.Received.Select(r => r.Raw)));
        var alertBody = hook.Received.FirstOrDefault(r => Mentions(r.Body, "opened", "overdue")).Body;
        Check(alertBody?["version"]?.GetValue<int>() == 1 && alertBody?["user"]?.GetValue<string>() == adminUser
              && alertBody?["title"]?.GetValue<string>().StartsWith("ASync:") == true && alertBody?["text"]?.GetValue<string>().Contains("watch") == true,
            "the alert has the documented payload (version, user, title, text)", alertBody?.ToJsonString());
        var incidents = (await Send(HttpMethod.Get, "/api/alerts/incidents", token: token)).Body?.AsArray() ?? [];
        Check(incidents.Any(i => i?["folderId"]?.GetValue<int>() == watch && i?["kind"]?.GetValue<string>() == "Overdue" && i?["resolvedAt"] is null),
            "the web UI lists the open incident", incidents.ToJsonString());
        var alertsBefore = hook.Received.Count(r => Mentions(r.Body, "opened", "overdue"));
        await Task.Delay(TimeSpan.FromSeconds(8)); // a few more scans
        Check(hook.Received.Count(r => Mentions(r.Body, "opened", "overdue")) == alertsBefore, "the overdue alert is sent once, not on every scan");

        await Sync(a, watch, "A syncs again");
        var backToNormal = await WaitFor(() => hook.Received.Any(r => Mentions(r.Body, "resolved", "overdue")), 60);
        Check(backToNormal, "the webhook gets a \"back to normal\" message", string.Join("\n", hook.Received.Select(r => r.Raw)));
        var after = (await Send(HttpMethod.Get, "/api/alerts/incidents", token: token)).Body?.AsArray() ?? [];
        // (With a 20-second grace it may already be overdue again: only the resolved one matters here.)
        Check(after.Any(i => i?["folderId"]?.GetValue<int>() == watch && i?["kind"]?.GetValue<string>() == "Overdue" && i?["resolvedAt"] is not null),
            "the incident is resolved", after.ToJsonString());
        if (serverContainer is not null)
        {
            var log = await Docker(["logs", serverContainer]);
            Check(!log.Output.Contains("e2e-hook-secret"), "the webhook's secret never appears in the server log");
        }
        // Detach A and remove the channel, so nothing more is sent while the rest runs.
        await Send(HttpMethod.Delete, $"/api/folders/{watch}/devices/{a.Id}", token: token);
        await Send(HttpMethod.Delete, $"/api/alerts/channels/{channelId}", token: token);
    }


    // ── Revocation ────────────────────────────────────────────────────────────
    Section("revocation");
    var revoke = await Send(HttpMethod.Delete, $"/api/devices/{b.Id}", token: token);
    Check(revoke.Status is HttpStatusCode.NoContent or HttpStatusCode.OK, $"remove B in the web UI → {revoke.Status}");
    var cut = await Cli(b, ["folders"]);
    Check(cut.Exit == 1 && cut.Output.Contains("error"), "B's commands are refused afterwards", cut.Output);
    var cutSync = await Cli(b, ["sync"]);
    Check(cutSync.Exit == 1, "B cannot sync any more", cutSync.Output);
    Write(a.Path("two", "after-revoke.txt"), "B must not get this");
    await Sync(a, two, "A keeps syncing");
    var stopped = await WaitFor(() => runB.Process.HasExited, 30);
    Check(stopped && runB.Process.ExitCode == 1 && runB.Log.Contains("was removed"),
        "B's running agent is disconnected and stops with an explanation", runB.Log.ToString());
    Check(!File.Exists(b.Path("two", "after-revoke.txt")), "B never received the change made after the revocation");
    if (serverContainer is not null)
    {
        var serverLog = await Docker(["logs", serverContainer]);
        Check(serverLog.Output.Contains($"live connection(s) of revoked device {b.Id}"), "the server closed B's live-updates connection");
    }
    var stillA = await Cli(a, ["status"]);
    Check(stillA.Exit == 0, "A is unaffected", stillA.Output);

    if (!runB.Process.HasExited) runB.Process.Kill(entireProcessTree: true);
    File.WriteAllText(Path.Combine(work, "run-B.log"), runB.Log.ToString());
    // ── Own password change: the other browser is signed out, this one stays ──
    Section("password change");
    // A device's access token (A's, refreshed by a command): it must get 403 on everything in this part.
    await Cli(a, ["status"]);
    deviceToken = JsonNode.Parse(File.ReadAllText(Path.Combine(a.Config, "token.json")))?["accessToken"]?.GetValue<string>();
    var pwName = $"e2e-pw-{runId}";
    var pwUser = await Send(HttpMethod.Post, "/api/users", new { username = pwName, password = "first-password", isAdmin = false }, token);
    Check(pwUser.Status == HttpStatusCode.OK, $"admin creates {pwName}", pwUser.Raw);
    var pwId = pwUser.Body?["id"]?.GetValue<int>() ?? 0;
    var browser1 = new Browser(server);
    var browser2 = new Browser(server);
    var t1 = await browser1.SignInAsync(pwName, "first-password");
    var t2 = await browser2.SignInAsync(pwName, "first-password");
    Check(t1 is not null && t2 is not null, $"{pwName} signs in in two browsers");
    var asDevice = await Send(HttpMethod.Post, "/api/auth/password", new { currentPassword = "first-password", newPassword = "x-device-pw" }, deviceToken);
    Check(asDevice.Status == HttpStatusCode.Forbidden, $"a device token cannot change a password → {asDevice.Status}");
    var wrongCurrent = await browser1.SendAsync(HttpMethod.Post, "/api/auth/password", new { currentPassword = "not-it", newPassword = "second-password" }, t1);
    Check(wrongCurrent.Status == HttpStatusCode.BadRequest, $"a wrong current password is refused → {wrongCurrent.Status}", wrongCurrent.Raw);
    var tooShort = await browser1.SendAsync(HttpMethod.Post, "/api/auth/password", new { currentPassword = "first-password", newPassword = "short" }, t1);
    Check(tooShort.Status == HttpStatusCode.BadRequest && tooShort.Raw.Contains("8 characters"), "the usual password rules apply", tooShort.Raw);
    var changed = await browser1.SendAsync(HttpMethod.Post, "/api/auth/password", new { currentPassword = "first-password", newPassword = "second-password" }, t1);
    Check(changed.Status == HttpStatusCode.NoContent, $"{pwName} changes their password in browser 1 → {changed.Status}", changed.Raw);
    var refresh2 = await browser2.SendAsync(HttpMethod.Post, "/api/auth/refresh");
    Check(refresh2.Status == HttpStatusCode.Unauthorized, $"browser 2 is signed out (refresh → {refresh2.Status})", refresh2.Raw);
    var refresh1 = await browser1.SendAsync(HttpMethod.Post, "/api/auth/refresh");
    Check(refresh1.Status == HttpStatusCode.OK, $"browser 1 stays signed in (refresh → {refresh1.Status})", refresh1.Raw);
    var t1b = refresh1.Body?["accessToken"]?.GetValue<string>();
    Check(await new Browser(server).SignInAsync(pwName, "first-password") is null, "the old password no longer signs in");
    Check(await new Browser(server).SignInAsync(pwName, "second-password") is not null, "the new password signs in");

    // ── Deleting a folder with its stored data ────────────────────────────────
    Section("deleting a folder with its data");
    async Task<bool?> OnStorage(string path)
    {
        if (serverContainer is null) return null;
        return (await Docker(["exec", serverContainer, "test", "-e", path])).Exit == 0;
    }
    async Task<JsonNode?> WaitForPurge(string id, int seconds)
    {
        JsonNode? job = null;
        await WaitForAsync(async () =>
        {
            job = (await Send(HttpMethod.Get, $"/api/purges/{id}", token: token)).Body;
            return job?["status"]?.GetValue<string>() is "Done" or "Failed";
        }, seconds, 1);
        return job;
    }
    var purgeMe = await Folder("purge-me", "Backup", "Transport", (a, "purge-me", null));
    var keepMe = await Folder("purge-me-not", "Backup", "Transport", (a, "purge-me-not", null));
    Write(a.Path("purge-me", "big.bin"), RandomBytes(3 * 1024 * 1024));
    Write(a.Path("purge-me", "sub", "note.txt"), "to be purged");
    Write(a.Path("purge-me-not", "keep.txt"), "must survive");
    await Sync(a, purgeMe, "A backs up purge-me");
    await Sync(a, keepMe, "A backs up purge-me-not");
    if (serverContainer is not null)
        Check(await OnStorage($"{storageRoot}/purge-me/config") == true && await OnStorage($"{storageRoot}/purge-me-not/config") == true,
            "both repositories are on the storage");
    var asDeviceDelete = await Send(HttpMethod.Delete, $"/api/folders/{purgeMe}?purge=true&confirm=purge-me", token: deviceToken);
    Check(asDeviceDelete.Status == HttpStatusCode.Forbidden, $"a device token cannot delete a folder → {asDeviceDelete.Status}");
    var asDevicePurges = await Send(HttpMethod.Get, "/api/purges", token: deviceToken);
    Check(asDevicePurges.Status == HttpStatusCode.Forbidden, $"a device token cannot list purges → {asDevicePurges.Status}");
    var asOther = await browser1.SendAsync(HttpMethod.Delete, $"/api/folders/{purgeMe}?purge=true&confirm=purge-me", token: t1b);
    Check(asOther.Status == HttpStatusCode.NotFound, $"another person cannot delete it → {asOther.Status}");
    var wrongName = await Send(HttpMethod.Delete, $"/api/folders/{purgeMe}?purge=true&confirm=purge", token: token);
    Check(wrongName.Status == HttpStatusCode.BadRequest, $"a purge needs the folder's exact name → {wrongName.Status}", wrongName.Raw);
    // The server's scheduled prune/check of the new folders may hold the repository for a moment: then 409, try again.
    (HttpStatusCode Status, JsonNode? Body, string Raw) purged = default;
    for (var attempt = 0; attempt < 20; attempt++)
    {
        purged = await Send(HttpMethod.Delete, $"/api/folders/{purgeMe}?purge=true&confirm=purge-me", token: token);
        if (purged.Status != HttpStatusCode.Conflict) break;
        await Task.Delay(3000);
    }
    Check(purged.Status == HttpStatusCode.Accepted, $"delete purge-me with its data → {purged.Status}", purged.Raw);
    var purgeId = purged.Body?["id"]?.GetValue<string>() ?? "";
    var purgeJob = await WaitForPurge(purgeId, 60);
    var purgedFiles = purgeJob?["filesDeleted"]?.GetValue<long>() ?? 0;
    var purgedBytes = purgeJob?["bytesDeleted"]?.GetValue<long>() ?? 0;
    Check(purgeJob?["status"]?.GetValue<string>() == "Done" && purgedFiles > 3 && purgedBytes > 3_000_000,
        $"the purge finishes and reports what it removed ({purgedFiles} files, {purgedBytes} bytes)", purgeJob?.ToJsonString());
    var listed = (await Send(HttpMethod.Get, "/api/purges", token: token)).Body?.AsArray() ?? [];
    Check(listed.Any(j => j?["id"]?.GetValue<string>() == purgeId), "the purge is listed for the person who started it");
    if (serverContainer is not null)
    {
        Check(await OnStorage($"{storageRoot}/purge-me") == false, "purge-me's directory is gone from the storage");
        Check(await OnStorage($"{storageRoot}/purge-me-not/config") == true, "purge-me-not's repository is untouched");
    }
    var goneFolder = await Cli(a, ["sync", "--folder", $"{purgeMe}"]);
    Check(goneFolder.Exit != 0, "A cannot sync the deleted folder", goneFolder.Output);
    Write(a.Path("purge-me-not", "later.txt"), "still backing up");
    await Sync(a, keepMe, "A keeps backing up purge-me-not");
    var keptRestore = await Cli(a, ["restore", "--folder", $"{keepMe}", "--target", Path.Combine(work, "restore-kept")]);
    Check(keptRestore.Exit == 0 && File.Exists(Path.Combine(work, "restore-kept", "keep.txt")), "purge-me-not still restores", keptRestore.Output);
    var reuse = await Send(HttpMethod.Post, "/api/folders", new
    {
        name = "purge-me", mode = "Mirror", encryption = "Transport", kind = "Files", storageBackendId = backendId, rootPath = "purge-me", intervalMinutes = 60,
    }, token);
    Check(reuse.Status == HttpStatusCode.OK, $"its directory can be used again once the purge is done → {reuse.Status}", reuse.Raw);

    // ── Deleting a user: their running agent stops, their stored data is purged ──
    Section("deleting a user");
    var shared = await Send(HttpMethod.Post, "/api/storage-backends",
        new { name = $"e2e shared {runId}", shared = true, settings = new { type = "Local", path = storageRoot + "-shared" } }, token);
    Check(shared.Status == HttpStatusCode.OK, $"admin creates a shared back-end → {shared.Status}", shared.Raw);
    var sharedId = shared.Body?["id"]?.GetValue<int>() ?? 0;
    var goneName = $"e2e-gone-{runId}";
    var goneUser = await Send(HttpMethod.Post, "/api/users", new { username = goneName, password = "gone-password", isAdmin = false }, token);
    var goneId = goneUser.Body?["id"]?.GetValue<int>() ?? 0;
    var goneBrowser = new Browser(server);
    var tg = await goneBrowser.SignInAsync(goneName, "gone-password");
    Check(goneId > 0 && tg is not null, $"admin creates {goneName}, who signs in", goneUser.Raw);
    var agentC = new Agent("e2e-C", Path.Combine(work, "C"));
    var codeC = (await goneBrowser.SendAsync(HttpMethod.Post, "/api/devices/enrollments", new { reusable = false, label = agentC.Name }, tg)).Body?["code"]?.GetValue<string>();
    var enrolledC = await Cli(agentC, ["enroll", "--server", server, "--code", codeC ?? "", "--name", agentC.Name]);
    Check(enrolledC.Exit == 0, $"{goneName} enrolls device C", enrolledC.Output);
    agentC.Id = Guid.Parse(JsonNode.Parse(File.ReadAllText(Path.Combine(agentC.Config, "identity.json")))!["deviceId"]!.GetValue<string>());
    var goneFolderRes = await goneBrowser.SendAsync(HttpMethod.Post, "/api/folders", new
    {
        name = "gone-docs", mode = "TwoWay", encryption = "Transport", kind = "Files", storageBackendId = sharedId, rootPath = "docs", intervalMinutes = 60,
    }, tg);
    var goneFolderId = goneFolderRes.Body?["id"]?.GetValue<int>() ?? 0;
    Write(agentC.Path("docs", "letter.txt"), "goodbye");
    var attachC = await goneBrowser.SendAsync(HttpMethod.Put, $"/api/folders/{goneFolderId}/devices", new { deviceId = agentC.Id, localPath = agentC.Path("docs") }, tg);
    Check(goneFolderRes.Status == HttpStatusCode.OK && attachC.Status == HttpStatusCode.OK,
        $"{goneName} creates a TwoWay folder on the shared back-end with C", goneFolderRes.Raw + attachC.Raw);
    await Sync(agentC, goneFolderId, "C syncs it");
    var goneArea = $"{storageRoot}-shared/users/{goneId}/docs";
    if (serverContainer is not null) Check(await OnStorage($"{goneArea}/letter.txt") == true, $"the file is in {goneName}'s area of the shared back-end");
    var runC = Start(agentC, ["run", "--verbose"]);
    Check(await WaitFor(() => runC.Log.Contains("Syncing \"gone-docs\""), 30), "C's agent runs", runC.Log.ToString());

    var preview = await Send(HttpMethod.Get, $"/api/users/{goneId}/deletion", token: token);
    Check(preview.Status == HttpStatusCode.OK && preview.Body?["devices"]?.GetValue<int>() == 1
          && preview.Body?["folders"]?.AsArray().Any(f => f?.GetValue<string>() == "gone-docs") == true,
        "the confirmation lists the user's device and folder", preview.Raw);
    var adminId = session.Body?["user"]?["id"]?.GetValue<int>() ?? 0;
    var self = await Send(HttpMethod.Delete, $"/api/users/{adminId}", token: token);
    Check(self.Status == HttpStatusCode.BadRequest && self.Raw.Contains("own account"), $"an admin cannot delete themselves → {self.Status}", self.Raw);
    var byUser = await goneBrowser.SendAsync(HttpMethod.Delete, $"/api/users/{pwId}", token: tg);
    Check(byUser.Status == HttpStatusCode.Forbidden, $"a non-admin cannot delete users → {byUser.Status}");
    var byDeviceDelete = await Send(HttpMethod.Delete, $"/api/users/{goneId}", token: deviceToken);
    Check(byDeviceDelete.Status == HttpStatusCode.Forbidden, $"a device token cannot delete users → {byDeviceDelete.Status}");
    var badConfirm = await Send(HttpMethod.Delete, $"/api/users/{goneId}?purge=true&confirm=nope", token: token);
    Check(badConfirm.Status == HttpStatusCode.BadRequest, $"a purge needs the username typed → {badConfirm.Status}", badConfirm.Raw);
    var deletedUser = await Send(HttpMethod.Delete, $"/api/users/{goneId}?purge=true&confirm={goneName}", token: token);
    var userPurges = deletedUser.Body?["purges"]?.AsArray() ?? [];
    Check(deletedUser.Status == HttpStatusCode.OK && userPurges.Count == 1, $"admin deletes {goneName} with their data → {deletedUser.Status}", deletedUser.Raw);
    var stoppedC = await WaitFor(() => runC.Process.HasExited, 30);
    Check(stoppedC && runC.Process.ExitCode == 1 && runC.Log.Contains("was removed"), "C's running agent stops with an explanation", runC.Log.ToString());
    var goneSession = await goneBrowser.SendAsync(HttpMethod.Get, "/api/folders", token: tg);
    Check(goneSession.Status == HttpStatusCode.Unauthorized, $"the deleted user's access token stops working at once → {goneSession.Status}");
    var goneRefresh = await goneBrowser.SendAsync(HttpMethod.Post, "/api/auth/refresh");
    Check(goneRefresh.Status == HttpStatusCode.Unauthorized, $"and their browser session is gone → {goneRefresh.Status}");
    var userList = (await Send(HttpMethod.Get, "/api/users", token: token)).Body?.AsArray() ?? [];
    Check(userList.All(u => u?["username"]?.GetValue<string>() != goneName), "the user is no longer listed");
    // The agent may have held the folder's lease when it was stopped: the purge waits for it (at most 2 minutes).
    var userPurge = await WaitForPurge(userPurges.FirstOrDefault()?["id"]?.GetValue<string>() ?? "", 180);
    Check(userPurge?["status"]?.GetValue<string>() == "Done" && userPurge?["filesDeleted"]?.GetValue<long>() >= 1,
        "the user's folder data is purged", userPurge?.ToJsonString());
    if (serverContainer is not null)
    {
        Check(await OnStorage(goneArea) == false, "their directory on the shared back-end is gone");
        Check(await OnStorage($"{storageRoot}/purge-me-not/config") == true, "other folders' data stays");
    }
    var backendList = (await Send(HttpMethod.Get, "/api/storage-backends", token: token)).Body?.AsArray() ?? [];
    Check(backendList.Any(x => x?["id"]?.GetValue<int>() == sharedId), "the shared back-end itself stays");
    if (!runC.Process.HasExited) runC.Process.Kill(entireProcessTree: true);
    File.WriteAllText(Path.Combine(work, "run-C.log"), runC.Log.ToString());

    // ── Quotas: over quota, uploads are refused and an alert goes out; deleting brings the person back under ──
    Section("quota (refused upload, alert, back under after a scan)");
    using (var hook = new WebhookListener())
    {
        var quotaName = $"e2e-quota-{runId}";
        var quotaUser = await Send(HttpMethod.Post, "/api/users", new { username = quotaName, password = "quota-password", isAdmin = false }, token);
        var quotaId = quotaUser.Body?["id"]?.GetValue<int>() ?? 0;
        var qb = new Browser(server);
        var tq = await qb.SignInAsync(quotaName, "quota-password");
        var agentD = new Agent("e2e-D", Path.Combine(work, "D"));
        var codeD = (await qb.SendAsync(HttpMethod.Post, "/api/devices/enrollments", new { reusable = false, label = agentD.Name }, tq)).Body?["code"]?.GetValue<string>();
        var enrolledD = await Cli(agentD, ["enroll", "--server", server, "--code", codeD ?? "", "--name", agentD.Name]);
        agentD.Id = Guid.Parse(JsonNode.Parse(File.ReadAllText(Path.Combine(agentD.Config, "identity.json")))!["deviceId"]!.GetValue<string>());
        var quotaFolderRes = await qb.SendAsync(HttpMethod.Post, "/api/folders", new
        {
            name = "quota", mode = "TwoWay", encryption = "Transport", kind = "Files", storageBackendId = sharedId, rootPath = "quota",
            intervalMinutes = 60, trashKeepDays = 30,
        }, tq);
        var quotaFolder = quotaFolderRes.Body?["id"]?.GetValue<int>() ?? 0;
        var attachD = await qb.SendAsync(HttpMethod.Put, $"/api/folders/{quotaFolder}/devices", new { deviceId = agentD.Id, localPath = agentD.Path("quota") }, tq);
        Directory.CreateDirectory(agentD.Path("quota"));
        var qChannel = await qb.SendAsync(HttpMethod.Post, "/api/alerts/channels",
            new { name = "quota hook", settings = new { type = "Webhook", url = $"http://{webhookHost}:{hook.Port}/hook" } }, tq);
        Check(tq is not null && enrolledD.Exit == 0 && attachD.Status == HttpStatusCode.OK && qChannel.Status == HttpStatusCode.OK,
            $"{quotaName} signs in, enrolls D, makes a TwoWay folder on the shared back-end and a webhook channel",
            enrolledD.Output + quotaFolderRes.Raw + attachD.Raw + qChannel.Raw);

        var notMine = await qb.SendAsync(HttpMethod.Put, $"/api/users/{quotaId}/quota", new { quotaBytes = (long?)null }, tq);
        Check(notMine.Status == HttpStatusCode.Forbidden, $"a user cannot change their own quota → {notMine.Status}");
        Write(agentD.Path("quota", "photo.bin"), RandomBytes(600 * 1024));
        await Sync(agentD, quotaFolder, "D uploads 600 KB (no quota yet)");
        var setQuota = await Send(HttpMethod.Put, $"/api/users/{quotaId}/quota", new { quotaBytes = 512 * 1024 }, token);
        Check(setQuota.Status == HttpStatusCode.OK && setQuota.Body?["quotaBytes"]?.GetValue<long>() == 512 * 1024,
            "the admin sets a 512 KB quota (below what is stored: the uploads were counted as they arrived)", setQuota.Raw);
        var usage = await qb.SendAsync(HttpMethod.Get, "/api/usage", token: tq);
        Check(usage.Body?["total"]?["state"]?.GetValue<string>() == "Over" && usage.Body?["total"]?["usedBytes"]?.GetValue<long>() >= 600 * 1024,
            "GET /api/usage says they are over quota", usage.Raw);

        Write(agentD.Path("quota", "more.bin"), RandomBytes(100 * 1024));
        var quotaRefused = await Cli(agentD, ["sync", "--folder", $"{quotaFolder}"]);
        Check(quotaRefused.Exit != 0 && quotaRefused.Output.Contains("Storage quota exceeded"), "D's next upload is refused: the sync fails and says why", quotaRefused.Output);
        if (serverContainer is not null) Check(await OnStorage($"{storageRoot}-shared/users/{quotaId}/quota/more.bin") == false, "the refused file is not on the storage");
        bool Quota(JsonNode? body, string list) =>
            body?[list]?.AsArray().Any(i => i?["kind"]?.GetValue<string>() == "overQuota" && i?["folderId"] is null) == true;
        var overAlert = await WaitFor(() => hook.Received.Any(r => Quota(r.Body, "opened")), 60);
        Check(overAlert, "the webhook gets an over-quota alert", string.Join("\n", hook.Received.Select(r => r.Raw)));
        var reported = (await qb.SendAsync(HttpMethod.Get, "/api/folders", token: tq)).Body?.AsArray().FirstOrDefault()?["devices"]?[0];
        Check(reported?["lastSyncOk"]?.GetValue<bool>() == false && reported?["lastSyncMessage"]?.GetValue<string>().Contains("quota") == true,
            "the failed run is reported (so the device's failing alert follows if it goes on)", reported?.ToJsonString());

        // Deleting the file (and emptying the trash, which still takes space) does not count until the folder is listed again.
        File.Delete(agentD.Path("quota", "photo.bin"));
        var deleting = await Cli(agentD, ["sync", "--folder", $"{quotaFolder}"]);
        Check(deleting.Output.Contains("1 deleted"), "D's sync deletes photo.bin on the storage (deletions work over quota)", deleting.Output);
        var trashEmptied = await qb.SendAsync(HttpMethod.Delete, $"/api/folders/{quotaFolder}/files/trash?confirm=true", token: tq);
        Check(trashEmptied.Status == HttpStatusCode.OK, $"{quotaName} empties the folder's trash → {trashEmptied.Status}", trashEmptied.Raw);
        var stillOver = await qb.SendAsync(HttpMethod.Get, "/api/usage", token: tq);
        Check(stillOver.Body?["total"]?["state"]?.GetValue<string>() == "Over", "until the next scan they still count as over quota", stillOver.Raw);
        var scan = await qb.SendAsync(HttpMethod.Post, "/api/usage/scan", token: tq);
        var under = await WaitForAsync(async () =>
            (await qb.SendAsync(HttpMethod.Get, "/api/usage", token: tq)).Body?["total"]?["state"]?.GetValue<string>() is "Ok" or "Warning", 60, 1);
        Check(scan.Status == HttpStatusCode.Accepted && under, "after \"Count again\" (a scan) they are back under quota",
            (await qb.SendAsync(HttpMethod.Get, "/api/usage", token: tq)).Raw);
        var folderUsage = (await qb.SendAsync(HttpMethod.Get, "/api/folders", token: tq)).Body?.AsArray().FirstOrDefault()?["usage"];
        Check(folderUsage?["scannedAt"] is not null && folderUsage?["bytes"]?.GetValue<long>() < 512 * 1024,
            "the folder shows its counted size", folderUsage?.ToJsonString());
        var resolvedAlert = await WaitFor(() => hook.Received.Any(r => Quota(r.Body, "resolved")), 60);
        Check(resolvedAlert, "the webhook gets a \"back to normal\" message for the quota", string.Join("\n", hook.Received.Select(r => r.Raw)));
        await Sync(agentD, quotaFolder, "D's upload now goes through");
        if (serverContainer is not null) Check(await OnStorage($"{storageRoot}-shared/users/{quotaId}/quota/more.bin") == true, "more.bin is on the storage");

        // ── Health checks: a back-end that cannot be reached alerts its owner without anyone pressing "Test" ──
        Section("storage health checks");
        var dead = await qb.SendAsync(HttpMethod.Post, "/api/storage-backends",
            new { name = "unreachable", settings = new { type = "WebDav", url = "http://127.0.0.1:9/dav/", username = "x", password = "y" } }, tq);
        var deadId = dead.Body?["id"]?.GetValue<int>() ?? 0;
        Check(dead.Status == HttpStatusCode.OK, $"{quotaName} adds a WebDAV back-end nobody answers on", dead.Raw);
        var failingAlert = await WaitFor(() => hook.Received.Any(r =>
            r.Body?["opened"]?.AsArray().Any(i => i?["kind"]?.GetValue<string>() == "storageFailing" && i?["storageBackendId"]?.GetValue<int>() == deadId) == true), 90);
        var deadNow = (await qb.SendAsync(HttpMethod.Get, "/api/storage-backends", token: tq)).Body?.AsArray().FirstOrDefault(x => x?["id"]?.GetValue<int>() == deadId);
        Check(failingAlert && deadNow?["lastCheckOk"]?.GetValue<bool>() == false && deadNow?["checkFailures"]?.GetValue<int>() >= 2,
            "the periodic check fails twice and the owner gets a storage-failing alert", deadNow?.ToJsonString() + string.Join("\n", hook.Received.Select(r => r.Raw)));
        var sharedNow = (await Send(HttpMethod.Get, "/api/storage-backends", token: token)).Body?.AsArray().FirstOrDefault(x => x?["id"]?.GetValue<int>() == sharedId);
        Check(sharedNow?["lastCheckOk"]?.GetValue<bool>() == true && sharedNow?["totalUsedBytes"] is not null,
            "the shared back-end was tested by the periodic check (working) and admins see its total use", sharedNow?.ToJsonString());
        await qb.SendAsync(HttpMethod.Delete, $"/api/storage-backends/{deadId}", token: tq);
        await qb.SendAsync(HttpMethod.Delete, $"/api/folders/{quotaFolder}/devices/{agentD.Id}", token: tq);
    }

    Console.WriteLine(failures.Count == 0 ? "\nAll end-to-end checks passed." : $"\n{failures.Count} check(s) failed:\n  - {string.Join("\n  - ", failures)}");
    return failures.Count == 0 ? 0 : 1;
}

// ── Helpers ───────────────────────────────────────────────────────────────────

async Task<(int Exit, string Output)> Sync(Agent agent, int folderId, string what)
{
    // Another device may hold the folder's lease for a moment (an agent in `run` mode): try again then.
    for (var attempt = 0; ; attempt++)
    {
        var r = await Cli(agent, ["sync", "--folder", $"{folderId}"]);
        // ...or a prune or check has the repository for a moment.
        if ((r.Output.Contains("Another device is syncing") || r.Output.Contains("The repository is busy")) && attempt < 10) { await Task.Delay(2000); continue; }
        Check(r.Exit == 0 && !r.Output.Contains("FAILED"), $"{agent.Name}: {what}", r.Output);
        return r;
    }
}

Task<(int Exit, string Output)> Cli(Agent agent, string[] cliArgs, Dictionary<string, string>? env = null) =>
    Exec("dotnet", [cli, "--config", agent.Config, .. cliArgs], env);

async Task<(int Exit, string Output)> Exec(string file, string[] execArgs, Dictionary<string, string>? env = null)
{
    var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
    foreach (var a in execArgs) psi.ArgumentList.Add(a);
    psi.Environment.Remove("ASYNC_PASSPHRASE");
    psi.Environment.Remove("ASYNC_RECOVERY_KEY");
    foreach (var (k, v) in env ?? []) psi.Environment[k] = v;
    using var p = Process.Start(psi)!;
    p.StandardInput.Close();
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    try { await p.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException) { p.Kill(entireProcessTree: true); return (-1, $"timed out: {file} {string.Join(' ', execArgs)}"); }
    return (p.ExitCode, await stdout + await stderr);
}

Task<(int Exit, string Output)> Docker(string[] dockerArgs) => Exec("docker", dockerArgs);

(Process Process, Log Log) Start(Agent agent, string[] cliArgs)
{
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in (string[])[cli, "--config", agent.Config, .. cliArgs]) psi.ArgumentList.Add(a);
    var log = new Log();
    var p = new Process { StartInfo = psi };
    p.OutputDataReceived += (_, e) => log.Add(e.Data);
    p.ErrorDataReceived += (_, e) => log.Add(e.Data);
    p.Start();
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();
    background.Add(p);
    return (p, log);
}

static Task<bool> WaitFor(Func<bool> condition, int seconds) => WaitForAsync(() => Task.FromResult(condition()), seconds, 0.5);

static async Task<bool> WaitForAsync(Func<Task<bool>> condition, int seconds, double pollSeconds)
{
    var until = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < until)
    {
        if (await condition()) return true;
        await Task.Delay(TimeSpan.FromSeconds(pollSeconds));
    }
    return await condition();
}

async Task<(HttpStatusCode Status, JsonNode? Body, string Raw)> Send(HttpMethod method, string path, object? body = null, string? token = null)
{
    using var req = new HttpRequestMessage(method, path);
    if (body is not null) req.Content = JsonContent.Create(body);
    if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var res = await http.SendAsync(req);
    var text = await res.Content.ReadAsStringAsync();
    var isJson = res.Content.Headers.ContentType?.MediaType?.Contains("json") == true;
    return (res.StatusCode, isJson && !string.IsNullOrWhiteSpace(text) ? JsonNode.Parse(text) : null, $"{(int)res.StatusCode} {text}");
}

static byte[] RandomBytes(int length) => RandomNumberGenerator.GetBytes(length);

/// <summary>Writes <paramref name="length"/> random bytes without holding them in memory; returns their SHA-256.</summary>
static string WriteRandomFile(string path, long length)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    using var file = File.Create(path);
    var buffer = new byte[1 << 20];
    for (var left = length; left > 0;)
    {
        var n = (int)Math.Min(buffer.Length, left);
        RandomNumberGenerator.Fill(buffer.AsSpan(0, n));
        file.Write(buffer, 0, n);
        hash.AppendData(buffer, 0, n);
        left -= n;
    }
    return Convert.ToHexString(hash.GetHashAndReset());
}

static string HashFile(string path)
{
    using var file = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(file));
}

/// <summary>The snapshot id in a backup's result line ("Snapshot 1a2b3c4d: …").</summary>
static string ShortId(string syncOutput)
{
    var at = syncOutput.IndexOf("Snapshot ", StringComparison.Ordinal);
    return at < 0 ? "latest" : syncOutput[(at + 9)..].Split(':')[0].Trim();
}

static void Write(string path, object content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    if (content is byte[] bytes) File.WriteAllBytes(path, bytes);
    else File.WriteAllText(path, (string)content);
}

static bool Same(string left, string right, params string[] files) => files.All(f =>
{
    var l = Path.Combine(left, f);
    var r = Path.Combine(right, f);
    return File.Exists(l) && File.Exists(r) && File.ReadAllBytes(l).AsSpan().SequenceEqual(File.ReadAllBytes(r));
});

sealed class Agent(string name, string home)
{
    public string Name { get; } = name;
    public Guid Id { get; set; }
    public string Config => System.IO.Path.Combine(home, "config");
    public string Path(params string[] parts) => System.IO.Path.Combine([home, .. parts.SelectMany(p => p.Split('/'))]);
}

/// <summary>A browser of its own: its own cookie jar, so its refresh cookie (its sign-in session) is separate.</summary>
sealed class Browser(string server)
{
    private readonly HttpClient _http = new(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) { BaseAddress = new Uri(server) };

    /// <summary>The access token, or null when the sign-in is refused.</summary>
    public async Task<string?> SignInAsync(string username, string password)
    {
        var r = await SendAsync(HttpMethod.Post, "/api/auth/login", new { username, password });
        return r.Status == HttpStatusCode.OK ? r.Body?["accessToken"]?.GetValue<string>() : null;
    }

    public async Task<(HttpStatusCode Status, JsonNode? Body, string Raw)> SendAsync(HttpMethod method, string path, object? body = null, string? token = null)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        var isJson = res.Content.Headers.ContentType?.MediaType?.Contains("json") == true;
        return (res.StatusCode, isJson && !string.IsNullOrWhiteSpace(text) ? JsonNode.Parse(text) : null, $"{(int)res.StatusCode} {text}");
    }
}

/// <summary>Output of a background agent, filled from its output events.</summary>
sealed class Log
{
    private readonly StringBuilder _text = new();
    public void Add(string? line) { if (line is not null) lock (_text) _text.AppendLine(line); }
    public bool Contains(string value) => ToString().Contains(value, StringComparison.Ordinal);
    public override string ToString() { lock (_text) return _text.ToString(); }
}

/// <summary>
/// A minimal HTTP listener for webhook alerts: accepts POSTs on every address of this machine (so the server's
/// container reaches it via host.docker.internal), answers 200 and keeps each body with its Authorization header.
/// </summary>
sealed class WebhookListener : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Any, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<(string Raw, JsonNode? Body, string? Authorization)> _queue = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public IReadOnlyList<(string Raw, JsonNode? Body, string? Authorization)> Received => [.. _queue];

    public WebhookListener()
    {
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new List<byte>();
                var chunk = new byte[8192];
                int headerEnd;
                while ((headerEnd = IndexOf(buffer, "\r\n\r\n"u8.ToArray())) < 0)
                {
                    var n = await stream.ReadAsync(chunk);
                    if (n == 0) return;
                    buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                }
                var lines = Encoding.ASCII.GetString(buffer.GetRange(0, headerEnd).ToArray()).Split("\r\n");
                string? Header(string name) => lines.Skip(1).Select(l => l.Split(':', 2))
                    .Where(p => p.Length == 2 && p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => p[1].Trim()).FirstOrDefault();
                var length = int.TryParse(Header("Content-Length"), out var l) ? l : 0;
                while (buffer.Count - headerEnd - 4 < length)
                {
                    var n = await stream.ReadAsync(chunk);
                    if (n == 0) break;
                    buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                }
                var body = Encoding.UTF8.GetString(buffer.Skip(headerEnd + 4).Take(length).ToArray());
                JsonNode? json = null;
                try { json = JsonNode.Parse(body); } catch (System.Text.Json.JsonException) { }
                _queue.Enqueue(($"{lines[0]} {body}", json, Header("Authorization")));
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
            }
            catch (IOException) { }
        }
    }

    private static int IndexOf(List<byte> data, byte[] pattern)
    {
        for (var i = 0; i + pattern.Length <= data.Count; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length && match; j++) match = data[i + j] == pattern[j];
            if (match) return i;
        }
        return -1;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
