using System.Globalization;
using System.CommandLine;
using ASync.Agent;
using ASync.Agent.Control;
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
var progressOption = new Option<bool>("--progress") { Description = "Print a line for every part of a large upload the server has stored." };
var noBrowseOption = new Option<bool>("--no-browse") { Description = "Never let the web UI browse this device's folders (also: ASYNC_ALLOW_BROWSE=0)." };
var passphraseFileOption = new Option<string?>("--passphrase-file") { Description = "Read the passphrase from this file (else ASYNC_PASSPHRASE, else ask)." };

var root = new RootCommand("ASync agent: syncs and backs up this machine's folders through an ASync server.");
root.Options.Add(configOption);
root.Options.Add(verboseOption);

// Runs a command with its context, turning expected failures into one readable line and exit code 1.
Command Define(string name, string description, Func<CliContext, ParseResult, CancellationToken, Task<int>> run, params Option[] options) =>
    DefineIn(root, name, description, run, options);

Command DefineIn(Command parent, string name, string description, Func<CliContext, ParseResult, CancellationToken, Task<int>> run, params Option[] options)
{
    var command = new Command(name, description);
    foreach (var o in options) command.Options.Add(o);
    command.SetAction(async (parse, ct) =>
    {
        CliContext? ctx = null;
        try
        {
            ctx = new CliContext(parse.GetValue(configOption)!, parse.GetValue(verboseOption));
            return await run(ctx, parse, ct);
        }
        catch (UnauthorizedAccessException e) when (ctx is null)
        {
            Console.Error.WriteLine($"error: cannot use the config directory: {e.Message} (it must be writable by this user)");
            return 1;
        }
        catch (Exception e) when (e is CliException or ApiException or SyncRefusedException or HttpRequestException or WrongKeyException
                                     or RepositoryLockedException or ControlException or AgentAlreadyRunningException or ServiceCommandException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
        finally
        {
            ctx?.Dispose();
        }
    });
    parent.Subcommands.Add(command);
    return command;
}

// The agent of this config directory, when one runs: the commands below then go through it (see Routed).
Task<ControlClient?> AgentAsync(CliContext ctx, CancellationToken ct) => Routed.ConnectAsync(ctx.Store.Root, ct);

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
    if (AgentLock.IsHeld(ctx.Store.Root))
        throw new CliException("An agent is running with this identity. Stop it first (`async-cli service uninstall`, or end `async-cli run`).");
    ctx.Store.DeleteIdentity();
    Console.WriteLine("This device's identity was removed. Its folders' files stay where they are.");
    return Task.FromResult(0);
});

// ── status / folders ──────────────────────────────────────────────────────────
Define("status", "Show this device and its folders (and, when an agent runs, what each folder is doing).", async (ctx, _, ct) =>
{
    if (await AgentAsync(ctx, ct) is { } agent)
        await using (agent)
            return await Routed.StatusAsync(agent, Console.Out, ct);
    var id = ctx.Identity;
    Console.WriteLine($"Device:  {id.DeviceName} ({id.DeviceId})");
    Console.WriteLine($"Account: {id.Username} on {id.ServerUrl}");
    var self = await ctx.Api.SelfAsync(ct);
    Console.WriteLine($"Server:  reachable, last seen {self.LastSeenAt?.ToLocalTime():yyyy-MM-dd HH:mm}");
    PrintFolders(await ctx.Api.GetFoldersAsync(ct), ctx);
    Console.WriteLine();
    Console.WriteLine("Agent:   not running (`async-cli run`, or `async-cli service install` to run it in the background).");
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
Define("sync", "Sync (or back up) folders once, now (through the running agent, if there is one).", async (ctx, p, ct) =>
{
    if (await AgentAsync(ctx, ct) is { } running)
        await using (running)
            return await Routed.SyncAsync(running, p.GetValue(optionalFolderOption), p.GetValue(massDeleteOption), p.GetValue(progressOption), Console.Out, ct);
    await using var agent = new SyncAgent(ctx.Api, ctx.Store, ctx.Loggers.CreateLogger("sync"))
    {
        AllowMassDeletion = p.GetValue(massDeleteOption),
        UploadProgress = p.GetValue(progressOption) ? (_, stored, size) => Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  upload: {stored:N0} of {size:N0} bytes stored")) : null,
    };
    var only = p.GetValue(optionalFolderOption);
    var folders = (await ctx.Api.GetFoldersAsync(ct)).Where(f => only is null || f.FolderId == only).ToList();
    if (folders.Count == 0)
        throw new CliException(only is null
            ? "No folders yet: assign some to this device in the web UI."
            : $"Folder {only} is not assigned to this device. `async-cli folders` lists the ones that are.");
    var failed = 0;
    foreach (var folder in folders)
    {
        Console.WriteLine($"{folder.Name}…");
        var result = await agent.RunFolderAsync(folder, ct);
        Console.WriteLine($"  {(result.Ok ? "" : "FAILED: ")}{result.Message}");
        if (!result.Ok) failed++;
    }
    return failed == 0 ? 0 : 1;
}, optionalFolderOption, massDeleteOption, progressOption);

Define("run", "Keep syncing: on schedule, on file changes and when other devices change folders. Enrolls first from ASYNC_SERVER/ASYNC_ENROLL_CODE if needed.",
    async (ctx, p, ct) =>
    {
        if (ctx.Store.LoadIdentity() is null)
        {
            if (Environment.GetEnvironmentVariable("ASYNC_SERVER") is null)
                throw new CliException("Not enrolled. Run `async-cli enroll`, or set ASYNC_SERVER (and ASYNC_ENROLL_CODE) to enroll on start.");
            await EnrollAsync(ctx, null, null, null, ct);
        }
        // The same agent as the service (lock, control channel), in the foreground with console logs.
        await using var runtime = new AgentRuntime(new AgentOptions
        {
            ConfigDirectory = ctx.Store.Root, Mode = AgentMode.Foreground, Version = CliContext.Version, AllowMassDeletion = p.GetValue(massDeleteOption),
            BrowsingForcedOff = p.GetValue(noBrowseOption),
        }, ctx.Loggers);
        runtime.Start();
        await runtime.RunAsync(ct);
        ctx.Loggers.CreateLogger("agent").LogInformation("Stopped.");
        return 0;
    }, massDeleteOption, noBrowseOption);

// ── browsing from the web UI ──────────────────────────────────────────────────
var browseOnOption = new Option<bool>("--on") { Description = "Allow the web UI to browse this device's folders (the default)." };
var browseOffOption = new Option<bool>("--off") { Description = "Do not let the web UI browse this device's folders." };
Define("browsing", "Show, allow (--on) or forbid (--off) browsing this device's folders from the web UI.", async (ctx, p, ct) =>
{
    bool? enabled = p.GetValue(browseOnOption) ? true : p.GetValue(browseOffOption) ? false : null;
    if (p.GetValue(browseOnOption) && p.GetValue(browseOffOption)) throw new CliException("Choose --on or --off, not both.");
    if (await AgentAsync(ctx, ct) is { } agent)
        await using (agent)
        {
            if (enabled is bool value) Console.WriteLine(await agent.SetBrowsingAsync(value, ct));
            else
            {
                var b = (await agent.StatusAsync(ct)).Browsing;
                Console.WriteLine(b is null ? "This agent is too old to offer browsing."
                    : $"Browsing from the web UI: {(b.Allowed ? "allowed" : "off")}{(b.Locked ? " (forced off by --no-browse or ASYNC_ALLOW_BROWSE)" : "")}.");
            }
            return 0;
        }
    // No agent runs: the setting is read when one starts.
    var settings = AgentSettings.Load(ctx.Store.Root);
    if (enabled is bool on)
    {
        (settings with { AllowBrowsing = on }).Save(ctx.Store.Root);
        Console.WriteLine(on ? "The web UI may browse this device's folders while the agent runs." : "The web UI can no longer browse this device's folders.");
    }
    else Console.WriteLine($"Browsing from the web UI: {(settings.AllowBrowsing && !AgentSettings.BrowsingOffByEnvironment() ? "allowed" : "off")} (no agent is running).");
    return 0;
}, browseOnOption, browseOffOption);

// ── device-side folder settings ───────────────────────────────────────────────
var pathOption = new Option<string>("--path", "-p") { Description = "Where the folder lives on this device.", Required = true };
Define("set-path", "Change where a folder lives on this device.", async (ctx, p, ct) =>
{
    if (await AgentAsync(ctx, ct) is { } agent)
        await using (agent)
        {
            Console.WriteLine(await agent.SetPathAsync(p.GetValue(folderOption), Path.GetFullPath(p.GetValue(pathOption)!), ct));
            return 0;
        }
    var folder = await ctx.FolderAsync(p.GetValue(folderOption), ct);
    var updated = await ctx.Api.UpdateOwnSettingsAsync(folder.FolderId, new UpdateDeviceFolderRequest(LocalPath: Path.GetFullPath(p.GetValue(pathOption)!), ExpectedRevision: folder.Revision), ct);
    Console.WriteLine($"\"{updated.Name}\" now syncs at {updated.LocalPath}.");
    return 0;
}, folderOption, pathOption);

foreach (var (name, paused) in new[] { ("pause", true), ("resume", false) })
    Define(name, $"{(paused ? "Pause" : "Resume")} syncing a folder on this device.", async (ctx, p, ct) =>
    {
        if (await AgentAsync(ctx, ct) is { } agent)
            await using (agent)
            {
                Console.WriteLine(await agent.SetPausedAsync(p.GetValue(folderOption), paused, ct));
                return 0;
            }
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
    if (await AgentAsync(ctx, ct) is { } agent)
        await using (agent)
        {
            var typed = Prompt.Secret("passphrase or recovery key", "ASYNC_PASSPHRASE", p.GetValue(passphraseFileOption));
            Console.WriteLine(await agent.UnlockAsync(p.GetValue(folderOption), typed, ct));
            return 0;
        }
    var folder = await ctx.FolderAsync(p.GetValue(folderOption), ct);
    var secret = Prompt.Secret("passphrase or recovery key", "ASYNC_PASSPHRASE", p.GetValue(passphraseFileOption));
    await RepositoryAccess.UnlockAsync(ctx.Store, folder, new RemoteStorageBackend(ctx.Api, folder.FolderId), secret, ct);
    Console.WriteLine($"\"{folder.Name}\" is unlocked on this device; it will back up unattended.");
    return 0;
}, folderOption, passphraseFileOption);

// ── snapshots / restore ───────────────────────────────────────────────────────
// Through the server (--folder, an enrolled device) or offline, straight from the storage (--repo; see Offline).
var sourceFolderOption = new Option<int?>("--folder", "-f") { Description = "Folder id (see `folders`), through the server. Or use --repo." };
var repoOption = new Option<string?>("--repo")
{
    Description = "Offline, without the server: the folder's repository directory (a local path, or a path on --storage).",
};
var storageOption = new Option<string?>("--storage")
{
    Description = "With --repo: a JSON file with the connection settings of an S3, WebDAV, SFTP or SMB storage (as the server's API takes them).",
};
var keyFileOption = new Option<string?>("--key-file")
{
    Description = "With --repo: read the recovery key (or end-to-end passphrase) from this file (else ASYNC_RECOVERY_KEY, else ask).",
};

// The repository a snapshots/restore command works on, and this device's id when it went through the server.
async Task<(Repository Repo, string? DeviceSource, IAsyncDisposable? Storage)> OpenSourceAsync(CliContext ctx, ParseResult p, CancellationToken ct)
{
    var folderId = p.GetValue(sourceFolderOption);
    var repoPath = p.GetValue(repoOption);
    if (folderId is null == repoPath is null)
        throw new CliException("Give either --folder (through the server) or --repo (offline, straight from the storage).");
    if (repoPath is null && (p.GetValue(storageOption) ?? p.GetValue(keyFileOption)) is not null)
        throw new CliException("--storage and --key-file go with --repo.");
    if (folderId is int id)
    {
        var folder = await ctx.FolderAsync(id, ct);
        var repo = await RepositoryAccess.OpenAsync(ctx.Api, ctx.Store, folder, new RemoteStorageBackend(ctx.Api, folder.FolderId), ct);
        return (repo, ctx.Identity.DeviceId.ToString("N"), null);
    }
    var storage = Offline.OpenStorage(repoPath!, p.GetValue(storageOption));
    try
    {
        var secret = Prompt.Secret("recovery key", "ASYNC_RECOVERY_KEY", p.GetValue(keyFileOption));
        return (await Offline.OpenAsync(storage, secret, ct), null, storage);
    }
    catch
    {
        await storage.DisposeAsync();
        throw;
    }
}

Define("snapshots", "List a Backup folder's snapshots (through the server, or offline from its storage with --repo).", async (ctx, p, ct) =>
{
    var (repo, deviceSource, storage) = await OpenSourceAsync(ctx, p, ct);
    await using var _ = storage;
    var snapshots = await repo.ListSnapshotsAsync(ct);
    if (snapshots.Count == 0) Console.WriteLine("No snapshots yet.");
    foreach (var s in snapshots)
        Console.WriteLine($"  {Repository.ShortSnapshotId(s.Id)}  {s.Time.ToLocalTime():yyyy-MM-dd HH:mm}  {s.Hostname,-16} {s.Stats.FilesNew + s.Stats.FilesChanged + s.Stats.FilesUnmodified} files" +
                          $"{(s.Source == deviceSource ? "  (this device)" : "")}");
    return 0;
}, sourceFolderOption, repoOption, storageOption, keyFileOption);

var snapshotOption = new Option<string>("--snapshot")
{
    Description = "Snapshot id (its start, or its last digits as `snapshots` shows them), or `latest` (this device's newest; offline, the newest).",
    DefaultValueFactory = _ => "latest",
};
var targetOption = new Option<string>("--target", "-t") { Description = "Directory to restore into.", Required = true };
var subPathOption = new Option<string?>("--path") { Description = "Only this file or directory of the snapshot." };
Define("restore", "Restore a snapshot (or part of it) into a directory (through the server, or offline from its storage with --repo).", async (ctx, p, ct) =>
{
    var (repo, deviceSource, storage) = await OpenSourceAsync(ctx, p, ct);
    await using var _ = storage;
    var snapshots = await repo.ListSnapshotsAsync(ct);
    var wanted = p.GetValue(snapshotOption)!;
    Snapshot? snapshot;
    if (wanted == "latest")
        snapshot = snapshots.LastOrDefault(s => s.Source == deviceSource) ?? snapshots.LastOrDefault();
    else
    {
        var matches = Repository.FindSnapshots(snapshots, wanted);
        if (matches.Count > 1) throw new CliException($"{matches.Count} snapshots match {wanted}; give more digits.");
        snapshot = matches.SingleOrDefault();
    }
    if (snapshot is null) throw new CliException(wanted == "latest" ? "There are no snapshots yet." : $"No snapshot matches {wanted}.");

    var result = await new RestoreRunner(repo).RunAsync(snapshot, p.GetValue(targetOption)!, new RestoreOptions { SubPath = p.GetValue(subPathOption) ?? "" }, ct);
    Console.WriteLine($"Restored {result.FilesRestored} files ({result.BytesRestored:N0} bytes) from {snapshot.Time.ToLocalTime():yyyy-MM-dd HH:mm}" +
                      $"{(result.FilesSkipped > 0 ? $", {result.FilesSkipped} already up to date" : "")}.");
    foreach (var e in result.Errors.Take(20)) Console.Error.WriteLine($"  {e}");
    return result.Errors.Count == 0 ? 0 : 1;
}, sourceFolderOption, repoOption, storageOption, keyFileOption, snapshotOption, targetOption, subPathOption);

// ── prune / check ─────────────────────────────────────────────────────────────
var readDataOption = new Option<double>("--read-data")
{
    Description = "Also download this percentage of the stored data (0–100) and verify it. Default 0: structure only.",
};

// Runs a prune or a check on one Backup folder, or on all of this device's. Exit code 1 when any failed.
async Task<int> MaintainAsync(CliContext ctx, ParseResult p, MaintenanceKind kind, CancellationToken ct)
{
    var only = p.GetValue(optionalFolderOption);
    var readData = kind == MaintenanceKind.Check ? p.GetValue(readDataOption) : 0;
    if (readData is < 0 or > 100) throw new CliException("--read-data takes a percentage from 0 to 100.");
    var all = await ctx.Api.GetFoldersAsync(ct);
    if (only is not null && all.FirstOrDefault(f => f.FolderId == only) is { } chosen && chosen.Mode != SyncMode.Backup)
        throw new CliException($"\"{chosen.Name}\" is a {chosen.Mode} folder. Only Backup folders have a repository to {(kind == MaintenanceKind.Prune ? "prune" : "check")}.");
    var folders = all.Where(f => f.Mode == SyncMode.Backup && (only is null || f.FolderId == only)).ToList();
    if (folders.Count == 0)
        throw new CliException(only is null
            ? "This device has no Backup folders."
            : $"Folder {only} is not assigned to this device. `async-cli folders` lists the ones that are.");

    await using var agent = new SyncAgent(ctx.Api, ctx.Store, ctx.Loggers.CreateLogger("maintenance"));
    var failed = 0;
    foreach (var folder in folders)
    {
        Console.WriteLine($"{folder.Name}…");
        // Claimed so the server knows this device is pruning: over a quota, it may still write the repacked data.
        try { await ctx.Api.ClaimMaintenanceAsync(folder.FolderId, ct, manual: kind); }
        catch (Exception e) when (e is ApiException or HttpRequestException) { /* older server: run anyway */ }
        var outcome = await agent.RunMaintenanceAsync(folder, kind, readData, ct);
        if (!outcome.Ok) failed++;
        if (outcome.Busy)
        {
            Console.WriteLine($"  BUSY: {outcome.Message}");
        }
        else if (outcome.Prune is { } pruned)
        {
            Console.WriteLine(pruned.PacksDeleted + pruned.PacksRepacked == 0
                ? "  Nothing to remove: every stored pack is still used by a snapshot."
                : $"  Removed {pruned.PacksDeleted} unused pack(s), repacked {pruned.PacksRepacked}, dropped {pruned.BlobsRemoved} unused chunk(s).");
            Console.WriteLine($"  Freed {ASync.Engine.Maintenance.ByteSize.Format(pruned.BytesFreed)} ({pruned.BytesFreed:N0} bytes).");
        }
        else if (outcome.Check is { } check)
        {
            Console.WriteLine($"  Checked {check.Snapshots} snapshot(s) and {check.Packs} pack(s)" +
                              (check.PacksRead > 0 ? $"; read back and verified {check.PacksRead} pack(s)." : "; no data read back (see --read-data)."));
            foreach (var w in check.Warnings.Take(20)) Console.WriteLine($"  warning: {w}");
            if (check.Warnings.Count > 20) Console.WriteLine($"  … and {check.Warnings.Count - 20} more warnings.");
            if (check.Ok)
            {
                Console.WriteLine("  OK: no problems found.");
            }
            else
            {
                Console.WriteLine($"  FAILED: {check.Errors.Count} problem(s) found:");
                foreach (var e in check.Errors.Take(50)) Console.WriteLine($"    {e}");
                if (check.Errors.Count > 50) Console.WriteLine($"    … and {check.Errors.Count - 50} more.");
            }
        }
        else
        {
            Console.WriteLine($"  FAILED: {outcome.Message}");
        }
    }
    return failed == 0 ? 0 : 1;
}

Define("prune", "Free the space of forgotten snapshots in Backup folders (all of this device's, or one).",
    (ctx, p, ct) => MaintainAsync(ctx, p, MaintenanceKind.Prune, ct), optionalFolderOption);

Define("check", "Verify that Backup folders' repositories are complete and readable. Exit code 1 when problems are found.",
    (ctx, p, ct) => MaintainAsync(ctx, p, MaintenanceKind.Check, ct), optionalFolderOption, readDataOption);

// ── service ───────────────────────────────────────────────────────────────────
// The agent as a background service: systemd (Linux), a logon task or Windows Service (Windows), launchd (macOS).
var service = new Command("service", "Run the agent in the background as a service (per user by default): install, uninstall, status.");
root.Subcommands.Add(service);
var systemOption = new Option<bool>("--system")
{
    Description = "System-wide instead of for this user (a systemd system unit, a Windows Service, a LaunchDaemon). Needs root/administrator.",
};
var runAsOption = new Option<string?>("--run-as") { Description = "With --system on Linux and macOS: the account the agent runs as (default: SUDO_USER)." };
var noStartOption = new Option<bool>("--no-start") { Description = "Only install; start it later (or at the next logon/boot)." };
var logFileOption = new Option<string?>("--log-file") { Description = "Log to this file (rolled over at 10 MB, 5 kept) instead of the console." };

DefineIn(service, "install", "Install the agent as a service and start it.", async (ctx, p, ct) =>
{
    var system = p.GetValue(systemOption);
    var runAs = p.GetValue(runAsOption) ?? (system ? Environment.GetEnvironmentVariable("SUDO_USER") : null);
    if (system && !OperatingSystem.IsWindows() && string.IsNullOrEmpty(runAs))
        throw new CliException("Give --run-as <user>: the account the system service runs as (its config directory and folders must be theirs).");
    var result = await ServiceInstaller.InstallAsync(new ServiceInstallOptions
    {
        Program = ServiceInstaller.CurrentProgram(), ConfigDirectory = ctx.Store.Root, System = system, RunAs = runAs,
        Verbose = p.GetValue(verboseOption), LogFile = p.GetValue(logFileOption),
    }, start: !p.GetValue(noStartOption), ct);
    Console.WriteLine(result.Message);
    if (ctx.Store.LoadIdentity() is null)
        Console.WriteLine("This device is not enrolled yet: run `async-cli enroll --server <url>`; the service starts syncing by itself afterwards.");
    return result.Ok ? 0 : 1;
}, systemOption, runAsOption, noStartOption, logFileOption);

DefineIn(service, "uninstall", "Stop the agent's service and remove it (the identity and folders stay).", async (_, p, ct) =>
{
    var result = await ServiceInstaller.UninstallAsync(p.GetValue(systemOption), ct);
    Console.WriteLine(result.Message);
    return result.Ok ? 0 : 1;
}, systemOption);

DefineIn(service, "status", "Whether the agent's service is installed and running, and what it is doing.", async (ctx, p, ct) =>
{
    var (installed, running, detail) = await ServiceInstaller.StatusAsync(p.GetValue(systemOption), ct);
    Console.WriteLine($"Service: {detail}");
    if (await AgentAsync(ctx, ct) is { } agent)
        await using (agent)
            return await Routed.StatusAsync(agent, Console.Out, ct);
    Console.WriteLine("Agent:   not reachable for this config directory.");
    return installed && running ? 1 : 3; // 3: not running, like systemctl is-active
}, systemOption);

// Started by the service manager. Also usable by hand to try the service setup in a terminal.
DefineIn(service, "run", "Run the agent as the service manager starts it (systemd notify, Windows Service, logs to the system log).", async (ctx, p, ct) =>
{
    return await AgentHost.RunServiceAsync(new ServiceRunOptions
    {
        ConfigDirectory = ctx.Store.Root, Version = CliContext.Version, Verbose = p.GetValue(verboseOption), LogFile = p.GetValue(logFileOption),
        SystemService = p.GetValue(systemOption), AllowMassDeletion = p.GetValue(massDeleteOption),
    }, ct);
}, systemOption, logFileOption, massDeleteOption);

return await root.Parse(args).InvokeAsync();
