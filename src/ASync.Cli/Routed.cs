using System.Globalization;
using ASync.Agent;
using ASync.Agent.Control;
using ASync.Contracts;

namespace ASync.Cli;

/// <summary>
/// The commands that, while an agent runs for the config directory (a service or <c>async-cli run</c>), go to it over
/// the control channel instead of doing the work here: so one folder is never synced by two agents at once, and the
/// running agent reacts at once (a pause stops its worker, an unlock starts a backup). Output and exit codes are the
/// same as in-process.
/// </summary>
internal static class Routed
{
    /// <summary>
    /// The running agent of <paramref name="configDirectory"/>, or null when none runs (work in-process then). An agent
    /// that holds the lock but cannot be reached is an error: running in-process next to it is what the lock prevents.
    /// </summary>
    public static async Task<ControlClient?> ConnectAsync(string configDirectory, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("ASYNC_IN_PROCESS") == "1") return null;
        var client = await ControlClient.TryConnectAsync(configDirectory, ct);
        if (client is null && AgentLock.IsHeld(configDirectory))
            throw new CliException($"An agent is running for {configDirectory}, but its control channel ({ControlEndpoint.Discover(configDirectory)}) " +
                                   "does not answer. Run this command as the agent's user, or stop the agent first.");
        return client;
    }

    public static async Task<int> StatusAsync(ControlClient agent, TextWriter output, CancellationToken ct)
    {
        var status = await agent.StatusAsync(ct);
        if (status.Device is { } d)
        {
            output.WriteLine($"Device:  {d.DeviceName} ({d.DeviceId})");
            output.WriteLine($"Account: {d.Username} on {d.ServerUrl}");
        }
        var how = status.Mode == AgentMode.Service ? "as a service" : "in the foreground (`async-cli run`)";
        output.WriteLine($"Agent:   {AgentLine(status)} — running {how}, pid {status.ProcessId}, v{status.AgentVersion}, since {status.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (status.State == AgentState.NotEnrolled)
        {
            output.WriteLine("This device is not enrolled. Run `async-cli enroll --server <url>`: the agent starts syncing by itself afterwards.");
            return 0;
        }
        if (status.State == AgentState.Revoked) return 1;
        PrintFolders(status.Folders, output);
        return 0;
    }

    private static string AgentLine(AgentStatusDto s) => s.State switch
    {
        AgentState.Connected => "connected (live updates on)",
        AgentState.Connecting => "connecting…",
        AgentState.Reconnecting => $"reconnecting{Problem(s)}",
        AgentState.Offline => $"server unreachable{Problem(s)}; folders retry on their schedule",
        AgentState.Revoked => $"REVOKED{Problem(s)}",
        AgentState.NotEnrolled => "waiting for enrollment",
        _ => "stopping",
    };

    private static string Problem(AgentStatusDto s) => s.Problem is { Length: > 0 } p ? $": {p}" : "";

    private static void PrintFolders(List<FolderStatusDto> folders, TextWriter output)
    {
        if (folders.Count == 0)
        {
            output.WriteLine("No folders yet: assign some to this device in the web UI.");
            return;
        }
        output.WriteLine();
        foreach (var f in folders)
        {
            var mode = f.Mode == SyncMode.Mirror ? $"Mirror {f.Direction}" : f.Mode.ToString();
            var notes = new List<string>();
            if (f.Paused) notes.Add("paused");
            if (f.Encryption == FolderEncryption.EndToEnd) notes.Add(f.Locked ? "E2E: locked here" : "E2E");
            output.WriteLine($"  [{f.FolderId}] {f.Name,-24} {mode,-16} every {f.IntervalMinutes} min  {f.LocalPath}{(notes.Count > 0 ? $"  ({string.Join(", ", notes)})" : "")}");
            output.WriteLine($"        {Activity(f)}");
        }
    }

    internal static string Activity(FolderStatusDto f)
    {
        var parts = new List<string>();
        parts.Add(f.Activity switch
        {
            FolderActivity.Syncing => $"syncing{Progress(f.Progress)}",
            FolderActivity.BackingUp => $"backing up{Progress(f.Progress)}",
            FolderActivity.Maintenance => "pruning or checking",
            FolderActivity.Paused => "paused",
            _ => "idle",
        });
        if (f.LastResult is { } r)
            parts.Add($"last run {r.At.ToLocalTime():yyyy-MM-dd HH:mm}: {(r.Ok ? "" : "FAILED: ")}{r.Message}");
        if (f.NextRunAt is { } next && f.Activity == FolderActivity.Idle) parts.Add($"next {next.ToLocalTime():HH:mm}");
        return string.Join("; ", parts);
    }

    internal static string Progress(ProgressDto? p)
    {
        if (p is null) return "";
        var files = p.TotalFiles is int total ? $"{p.Files} of {total} files" : $"{p.Files} files";
        var bytes = p.TotalBytes is long totalBytes && totalBytes > 0 ? $"{Size(p.Bytes)} of {Size(totalBytes)}" : Size(p.Bytes);
        return $" ({files}, {bytes}{(p.CurrentFile is { Length: > 0 } c ? $", {c}" : "")})";
    }

    private static string Size(long bytes) => ASync.Engine.Maintenance.ByteSize.Format(bytes);

    public static async Task<int> SyncAsync(ControlClient agent, int? only, bool allowMassDelete, bool progress, TextWriter output, CancellationToken ct)
    {
        var status = await agent.StatusAsync(ct);
        if (status.State == AgentState.Revoked) throw new CliException(status.Problem ?? "This device was removed from its account.");
        var folders = status.Folders.Where(f => only is null || f.FolderId == only).ToList();
        if (only is not null && folders.Count == 0)
        {
            // The agent reloads its folders for a folder it does not know yet (attached a moment ago).
            folders = [new FolderStatusDto { FolderId = only.Value, Name = $"Folder {only}" }];
        }
        if (folders.Count == 0) throw new CliException("No folders yet: assign some to this device in the web UI.");

        var failed = 0;
        foreach (var folder in folders)
        {
            if (folder.LocalPath.Length > 0) output.WriteLine($"{folder.Name}…"); // not for a placeholder
            using var watching = new CancellationTokenSource();
            var printer = progress ? PrintProgressAsync(agent, folder.FolderId, output, watching.Token) : Task.CompletedTask;
            List<FolderRunDto> results;
            try
            {
                results = await agent.SyncAsync(folder.FolderId, allowMassDelete, ct);
            }
            finally
            {
                await watching.CancelAsync();
                await printer;
            }
            foreach (var r in results)
            {
                output.WriteLine($"  {(r.Ok ? "" : "FAILED: ")}{r.Message}");
                if (!r.Ok) failed++;
            }
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>--progress through the agent: its progress events for the folder, at most one line a second.</summary>
    private static async Task PrintProgressAsync(ControlClient agent, int folderId, TextWriter output, CancellationToken ct)
    {
        var last = DateTime.MinValue;
        try
        {
            await foreach (var e in agent.SubscribeAsync(ct))
            {
                if (e.Folder is not { } f || f.FolderId != folderId || f.Progress is not { } p) continue;
                if (DateTime.UtcNow - last < TimeSpan.FromSeconds(1)) continue;
                last = DateTime.UtcNow;
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  progress:{Progress(p)}"));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or ControlException) { }
    }
}
