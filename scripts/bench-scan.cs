// Measures what a sync or backup run that finds nothing changed costs on a large folder (task 26):
//   dotnet run scripts/bench-scan.cs -- --work DIR [--files 100000] [--runs 3] [--mode sync|backup|both]
// The folder (DIR/src, small files in directories of 100) is made once and reused; the storage is a Local back-end in
// DIR (no server), so the numbers are the device's own work: walking the tree, listing the storage, and what the run writes
// locally. "written" is this process's write_bytes from /proc/self/io (Linux), i.e. what reached the block layer: the sync
// state, snapshots and index files (the storage is on the same disk, so a run that uploads would count too; these runs
// upload nothing). Run it against two builds to compare (the numbers of task 26 are in docs/orchestration/test-records).
#:property TargetFramework=net10.0
#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false
#:project ../src/ASync.Client.Core/ASync.Client.Core.csproj

using System.Diagnostics;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Backup;
using ASync.Engine.Crypto;
using ASync.Engine.Local;
using ASync.Engine.Storage;
using Repo = ASync.Engine.Repository.Repository;

string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var work = Path.GetFullPath(Arg("--work", Path.Combine(Path.GetTempPath(), "async-bench")));
var files = int.Parse(Arg("--files", "100000"));
var runs = int.Parse(Arg("--runs", "3"));
var mode = Arg("--mode", "both");
var src = Path.Combine(work, "src");

if (!Directory.Exists(src))
{
    var sw = Stopwatch.StartNew();
    var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    for (var i = 0; i < files; i++)
    {
        var dir = Path.Combine(src, $"d{i / 10000:000}", $"e{i / 100 % 100:00}");
        if (i % 100 == 0) Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"f{i:000000}.txt");
        File.WriteAllText(path, $"file {i}\n");
        File.SetLastWriteTimeUtc(path, t0.AddSeconds(i));
    }
    Console.WriteLine($"made {files} files in {sw.Elapsed.TotalSeconds:0.0} s");
}

static long WriteBytes()
{
    if (!File.Exists("/proc/self/io")) return 0;
    foreach (var line in File.ReadLines("/proc/self/io"))
        if (line.StartsWith("write_bytes:", StringComparison.Ordinal)) return long.Parse(line[12..].Trim());
    return 0;
}

static string Mb(long bytes) => $"{bytes / 1024.0 / 1024:0.00} MB";

async Task Measure(string label, Func<Task<string>> run)
{
    for (var i = 0; i < runs; i++)
    {
        var before = WriteBytes();
        var sw = Stopwatch.StartNew();
        var note = await run();
        sw.Stop();
        var written = WriteBytes() - before;
        Console.WriteLine($"{label} #{i + 1}: {sw.Elapsed.TotalSeconds:0.00} s, written {Mb(written)}{(note.Length > 0 ? ", " + note : "")}");
    }
}

if (mode is "sync" or "both")
{
    var storage = Path.Combine(work, "storage");
    var state = Path.Combine(work, "state");
    Directory.CreateDirectory(storage);
    var backend = new LocalBackend(storage);
    var options = new SyncOptions { Mode = SyncMode.TwoWay, DeviceName = "bench" };
    var stateFile = Path.Combine(state, "sync-state.json");
    if (!File.Exists(stateFile))
    {
        var sw = Stopwatch.StartNew();
        var first = await new FolderSyncer(backend, src, state, options).RunAsync();
        Console.WriteLine($"sync first run: {first.Uploaded} up in {sw.Elapsed.TotalSeconds:0.0} s");
    }
    Console.WriteLine($"sync-state.json: {Mb(new FileInfo(stateFile).Length)}");
    // GC and JIT out of the way of the first measured run.
    await new FolderSyncer(backend, src, state, options).RunAsync();
    await Measure("sync no-change", async () =>
    {
        var r = await new FolderSyncer(backend, src, state, options).RunAsync();
        return $"{r.Uploaded} up, {r.Downloaded} down, state file modified {File.GetLastWriteTimeUtc(stateFile):HH:mm:ss}";
    });
}

if (mode is "backup" or "both")
{
    var repoDir = Path.Combine(work, "repo");
    var backend = new LocalBackend(repoDir);
    const string passphrase = "bench-passphrase";
    Repo repo = await Repo.ExistsAsync(backend)
        ? await Repo.OpenAsync(backend, passphrase)
        : await Repo.InitAsync(backend, RepositoryKeys.Generate() is var k ? k : null!, [KeyFile.ForPassphrase(k, passphrase, iterations: 1000)]);
    var folder = new DeviceFolderDto(1, "bench", SyncMode.Backup, FolderEncryption.Transport, FolderKind.Files, 60, [],
        new RetentionDto(0, 0, 0, 0, 0, 0, null), 30, 30, 1, true, src, null, false);
    var device = Guid.Parse("0190aaaa-0000-7000-8000-0000000000be");
    if ((await repo.ListSnapshotsAsync()).Count == 0)
    {
        var sw = Stopwatch.StartNew();
        await new BackupJob(repo, folder, device, "bench").RunAsync();
        Console.WriteLine($"backup first run: {sw.Elapsed.TotalSeconds:0.0} s");
    }
    await Measure("backup no-change", async () =>
    {
        await new BackupJob(repo, folder, device, "bench").RunAsync();
        return $"{(await repo.ListSnapshotsAsync()).Count} snapshots";
    });
}
