// End-to-end sync test: a running server plus real `async-cli` agents with separate config directories.
//   dotnet run scripts/e2e-sync.cs -- --cli <async-cli.dll> [--server URL] [--admin USER] [--password PW]
//                                     [--work DIR] [--server-container NAME] [--docker-image IMG --docker-network NET]
// Usually started by scripts/e2e-sync.sh, which builds the CLI, starts a throw-away server stack and cleans up.
// The person's side (admin account, a Local back-end under /data, folders, codes, attaching devices, revoking) goes
// through the REST API; the devices' side is the CLI. With --server-container it also looks at the storage on the
// server's disk; with --docker-image a third agent runs from the async-cli image.
#:property TargetFramework=net10.0
#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false

using System.Diagnostics;
using System.Net;
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

/// <summary>Output of a background agent, filled from its output events.</summary>
sealed class Log
{
    private readonly StringBuilder _text = new();
    public void Add(string? line) { if (line is not null) lock (_text) _text.AppendLine(line); }
    public bool Contains(string value) => ToString().Contains(value, StringComparison.Ordinal);
    public override string ToString() { lock (_text) return _text.ToString(); }
}
