using System.CommandLine;
using ASync.Cli;
using ASync.Client.Core;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Backup;
using ASync.Engine.Repository;
using Microsoft.Extensions.Logging;

// ── Global options ────────────────────────────────────────────────────────────
var configOption = new Option<string>("--config")
{
    Description = "Directory with this device's identity and state (default: ASYNC_CONFIG_DIR or ~/.config/async).",
    DefaultValueFactory = _ => FileClientStore.DefaultRoot(),
    Recursive = true,
};
var verboseOption = new Option<bool>("--verbose", "-v") { Description = "More detailed logs.", Recursive = true };
var folderOption = new Option<int>("--folder", "-f") { Description = "Folder id (see `folders`).", Required = true };
var optionalFolderOption = new Option<int?>("--folder", "-f") { Description = "Only this folder." };
var massDeleteOption = new Option<bool>("--allow-mass-delete") { Description = "Allow a sync to delete more than half of a folder's files." };
var passphraseFileOption = new Option<string?>("--passphrase-file") { Description = "Read the passphrase from this file (else ASYNC_PASSPHRASE, else ask)." };

var root = new RootCommand("ASync agent: syncs and backs up this machine's folders through an ASync server.");
root.Options.Add(configOption);
root.Options.Add(verboseOption);

// Runs a command with its context, turning expected failures into one readable line and exit code 1.
Command Define(string name, string description, Func<CliContext, ParseResult, CancellationToken, Task<int>> run, params Option[] options)
{
    var command = new Command(name, description);
    foreach (var o in options) command.Options.Add(o);
    command.SetAction(async (parse, ct) =>
    {
        using var ctx = new CliContext(parse.GetValue(configOption)!, parse.GetValue(verboseOption));
        try
        {
            return await run(ctx, parse, ct);
        }
        catch (Exception e) when (e is CliException or ApiException or SyncRefusedException or HttpRequestException or WrongKeyException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    });
    root.Subcommands.Add(command);
    return command;
}

// ── enroll ────────────────────────────────────────────────────────────────────
var serverOption = new Option<string?>("--server", "-s") { Description = "The server's address, e.g. https://sync.example.com (else ASYNC_SERVER)." };
var codeOption = new Option<string?>("--code", "-c") { Description = "An add-device code from the web UI (else ASYNC_ENROLL_CODE; without one, a code to approve is shown)." };
var nameOption = new Option<string?>("--name", "-n") { Description = "This device's name (default: ASYNC_DEVICE_NAME or the host name)." };

Define("enroll", "Add this device to your account.", async (ctx, p, ct) =>
{
    await EnrollAsync(ctx, p.GetValue(serverOption), p.GetValue(codeOption), p.GetValue(nameOption), ct);
    return 0;
}, serverOption, codeOption, nameOption);

async Task<DeviceIdentity> EnrollAsync(CliContext ctx, string? server, string? code, string? name, CancellationToken ct)
{
    server ??= Environment.GetEnvironmentVariable("ASYNC_SERVER") ?? throw new CliException("Pass --server (or set ASYNC_SERVER).");
    code ??= Environment.GetEnvironmentVariable("ASYNC_ENROLL_CODE");
    name ??= Environment.GetEnvironmentVariable("ASYNC_DEVICE_NAME") is { Length: > 0 } n ? n : Environment.MachineName;
    if (ctx.Store.LoadIdentity() is { } existing)
        throw new CliException($"Already enrolled as \"{existing.DeviceName}\" with {existing.ServerUrl}. Run `async-cli forget` first to enroll again.");

    using var api = new AsyncApiClient(server) { AppVersion = CliContext.Version };
    var identity = string.IsNullOrWhiteSpace(code)
        ? await api.EnrollWithDeviceCodeAsync(name, CliContext.Platform, started =>
        {
            Console.WriteLine();
            Console.WriteLine($"  To add \"{name}\", open {started.VerificationUri}");
            Console.WriteLine($"  and check that it shows the code {started.UserCode}.");
            Console.WriteLine();
            Console.WriteLine($"Waiting for approval (expires {started.ExpiresAt.ToLocalTime():HH:mm})…");
        }, ct)
        : await api.EnrollAsync(code, name, CliContext.Platform, ct);
    ctx.Store.SaveIdentity(identity);
    Console.WriteLine($"Enrolled \"{identity.DeviceName}\" for {identity.Username}. Assign folders to it in the web UI, then run `async-cli run`.");
    return identity;
}

Define("forget", "Remove this device's identity and keys from this machine (remove it in the web UI too).", (ctx, _, _) =>
{
    ctx.Store.DeleteIdentity();
    Console.WriteLine("This device's identity was removed. Its folders' files stay where they are.");
    return Task.FromResult(0);
});

// ── status / folders ──────────────────────────────────────────────────────────
Define("status", "Show this device and its folders.", async (ctx, _, ct) =>
{
    var id = ctx.Identity;
    Console.WriteLine($"Device:  {id.DeviceName} ({id.DeviceId})");
    Console.WriteLine($"Account: {id.Username} on {id.ServerUrl}");
    var self = await ctx.Api.SelfAsync(ct);
    Console.WriteLine($"Server:  reachable, last seen {self.LastSeenAt?.ToLocalTime():yyyy-MM-dd HH:mm}");
    PrintFolders(await ctx.Api.GetFoldersAsync(ct), ctx);
    return 0;
});

Define("folders", "List the folders assigned to this device.", async (ctx, _, ct) =>
{
    PrintFolders(await ctx.Api.GetFoldersAsync(ct), ctx);
    return 0;
});

void PrintFolders(List<DeviceFolderDto> folders, CliContext ctx)
{
    if (folders.Count == 0)
    {
        Console.WriteLine("No folders yet: assign some to this device in the web UI.");
        return;
    }
    Console.WriteLine();
    foreach (var f in folders)
    {
        var mode = f.Mode == SyncMode.Mirror ? $"Mirror {f.Direction}" : f.Mode.ToString();
        var notes = new List<string>();
        if (f.Paused) notes.Add("paused");
        if (f.Encryption == FolderEncryption.EndToEnd)
            notes.Add(!f.RepositoryReady ? "E2E: not set up" : ctx.Store.LoadRepositoryKeys(f.FolderId) is null ? "E2E: locked here" : "E2E");
        Console.WriteLine($"  [{f.FolderId}] {f.Name,-24} {mode,-16} every {f.IntervalMinutes} min  {f.LocalPath}{(notes.Count > 0 ? $"  ({string.Join(", ", notes)})" : "")}");
    }
}

// ── sync / run ────────────────────────────────────────────────────────────────
Define("sync", "Sync (or back up) folders once, now.", async (ctx, p, ct) =>
{
    await using var agent = new SyncAgent(ctx.Api, ctx.Store, ctx.Loggers.CreateLogger("sync")) { AllowMassDeletion = p.GetValue(massDeleteOption) };
    var only = p.GetValue(optionalFolderOption);
    var failed = 0;
    foreach (var folder in (await ctx.Api.GetFoldersAsync(ct)).Where(f => only is null || f.FolderId == only))
    {
        Console.WriteLine($"{folder.Name}…");
        var result = await agent.RunFolderAsync(folder, ct);
        Console.WriteLine($"  {(result.Ok ? "" : "FAILED: ")}{result.Message}");
        if (!result.Ok) failed++;
    }
    return failed == 0 ? 0 : 1;
}, optionalFolderOption, massDeleteOption);

Define("run", "Keep syncing: on schedule, on file changes and when other devices change folders. Enrolls first from ASYNC_SERVER/ASYNC_ENROLL_CODE if needed.",
    async (ctx, p, ct) =>
    {
        if (ctx.Store.LoadIdentity() is null)
        {
            if (Environment.GetEnvironmentVariable("ASYNC_SERVER") is null)
                throw new CliException("Not enrolled. Run `async-cli enroll`, or set ASYNC_SERVER (and ASYNC_ENROLL_CODE) to enroll on start.");
            await EnrollAsync(ctx, null, null, null, ct);
        }
        var logger = ctx.Loggers.CreateLogger("agent");
        logger.LogInformation("ASync agent {Version}: {Device} → {Server}", CliContext.Version, ctx.Identity.DeviceName, ctx.Identity.ServerUrl);
        await using var agent = new SyncAgent(ctx.Api, ctx.Store, logger) { AllowMassDeletion = p.GetValue(massDeleteOption) };
        await agent.RunAsync(ct);
        logger.LogInformation("Stopped.");
        return 0;
    }, massDeleteOption);

// ── device-side folder settings ───────────────────────────────────────────────
var pathOption = new Option<string>("--path", "-p") { Description = "Where the folder lives on this device.", Required = true };
Define("set-path", "Change where a folder lives on this device.", async (ctx, p, ct) =>
{
    var folder = await ctx.FolderAsync(p.GetValue(folderOption), ct);
    var updated = await ctx.Api.UpdateOwnSettingsAsync(folder.FolderId, new UpdateDeviceFolderRequest(LocalPath: Path.GetFullPath(p.GetValue(pathOption)!), ExpectedRevision: folder.Revision), ct);
    Console.WriteLine($"\"{updated.Name}\" now syncs at {updated.LocalPath}.");
    return 0;
}, folderOption, pathOption);

foreach (var (name, paused) in new[] { ("pause", true), ("resume", false) })
    Define(name, $"{(paused ? "Pause" : "Resume")} syncing a folder on this device.", async (ctx, p, ct) =>
    {
        var folder = await ctx.FolderAsync(p.GetValue(folderOption), ct);
        await ctx.Api.UpdateOwnSettingsAsync(folder.FolderId, new UpdateDeviceFolderRequest(Paused: paused, ExpectedRevision: folder.Revision), ct);
        Console.WriteLine($"\"{folder.Name}\" {(paused ? "paused" : "resumed")} on this device.");
        return 0;
    }, folderOption);

// ── end-to-end encryption ─────────────────────────────────────────────────────
Define("init-encryption", "Set up an end-to-end encrypted Backup folder (once, on one device). Prints its recovery key.", async (ctx, p, ct) =>
{
    var folder = await ctx.FolderAsync(p.GetValue(folderOption), ct);
    var passphrase = Prompt.Secret("passphrase", "ASYNC_PASSPHRASE", p.GetValue(passphraseFileOption), confirm: true);
    var recovery = await RepositoryAccess.InitializeEndToEndAsync(ctx.Api, ctx.Store, folder, new RemoteStorageBackend(ctx.Api, folder.FolderId), passphrase, ct);
    Console.WriteLine();
    Console.WriteLine($"\"{folder.Name}\" is set up. Its recovery key — store it somewhere safe, it is shown only now:");
    Console.WriteLine();
    Console.WriteLine($"    {recovery}");
    Console.WriteLine();
    Console.WriteLine("Without the passphrase or this key nobody, not even the server's admin, can restore these backups.");
    return 0;
}, folderOption, passphraseFileOption);

Define("unlock", "Unlock an end-to-end encrypted folder on this device (passphrase or recovery key).", async (ctx, p, ct) =>
{
    var folder = await ctx.FolderAsync(p.GetValue(folderOption), ct);
    var secret = Prompt.Secret("passphrase or recovery key", "ASYNC_PASSPHRASE", p.GetValue(passphraseFileOption));
    await RepositoryAccess.UnlockAsync(ctx.Store, folder, new RemoteStorageBackend(ctx.Api, folder.FolderId), secret, ct);
    Console.WriteLine($"\"{folder.Name}\" is unlocked on this device; it will back up unattended.");
    return 0;
}, folderOption, passphraseFileOption);

// ── snapshots / restore ───────────────────────────────────────────────────────
Define("snapshots", "List a Backup folder's snapshots.", async (ctx, p, ct) =>
{
    var folder = await ctx.FolderAsync(p.GetValue(folderOption), ct);
    var repo = await RepositoryAccess.OpenAsync(ctx.Api, ctx.Store, folder, new RemoteStorageBackend(ctx.Api, folder.FolderId), ct);
    var snapshots = await repo.ListSnapshotsAsync(ct);
    if (snapshots.Count == 0) Console.WriteLine("No snapshots yet.");
    foreach (var s in snapshots)
        Console.WriteLine($"  {s.Id[..12]}  {s.Time.ToLocalTime():yyyy-MM-dd HH:mm}  {s.Hostname,-16} {s.Stats.FilesNew + s.Stats.FilesChanged + s.Stats.FilesUnmodified} files" +
                          $"{(s.Source == ctx.Identity.DeviceId.ToString("N") ? "  (this device)" : "")}");
    return 0;
}, folderOption);

var snapshotOption = new Option<string>("--snapshot") { Description = "Snapshot id (or a prefix), or `latest` (this device's newest).", DefaultValueFactory = _ => "latest" };
var targetOption = new Option<string>("--target", "-t") { Description = "Directory to restore into.", Required = true };
var subPathOption = new Option<string?>("--path") { Description = "Only this file or directory of the snapshot." };
Define("restore", "Restore a snapshot (or part of it) into a directory.", async (ctx, p, ct) =>
{
    var folder = await ctx.FolderAsync(p.GetValue(folderOption), ct);
    var repo = await RepositoryAccess.OpenAsync(ctx.Api, ctx.Store, folder, new RemoteStorageBackend(ctx.Api, folder.FolderId), ct);
    var snapshots = await repo.ListSnapshotsAsync(ct);
    var wanted = p.GetValue(snapshotOption)!;
    var snapshot = wanted == "latest"
        ? snapshots.LastOrDefault(s => s.Source == ctx.Identity.DeviceId.ToString("N")) ?? snapshots.LastOrDefault()
        : snapshots.SingleOrDefault(s => s.Id.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
    if (snapshot is null) throw new CliException(wanted == "latest" ? "There are no snapshots yet." : $"No single snapshot matches {wanted}.");

    var result = await new RestoreRunner(repo).RunAsync(snapshot, p.GetValue(targetOption)!, new RestoreOptions { SubPath = p.GetValue(subPathOption) ?? "" }, ct);
    Console.WriteLine($"Restored {result.FilesRestored} files ({result.BytesRestored:N0} bytes) from {snapshot.Time.ToLocalTime():yyyy-MM-dd HH:mm}" +
                      $"{(result.FilesSkipped > 0 ? $", {result.FilesSkipped} already up to date" : "")}.");
    foreach (var e in result.Errors.Take(20)) Console.Error.WriteLine($"  {e}");
    return result.Errors.Count == 0 ? 0 : 1;
}, folderOption, snapshotOption, targetOption, subPathOption);

return await root.Parse(args).InvokeAsync();
