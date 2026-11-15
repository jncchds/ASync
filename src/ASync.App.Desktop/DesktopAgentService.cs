using ASync.Agent;
using ASync.App.Services;

namespace ASync.App.Desktop;

/// <summary>
/// The agent as a per-user service, with the code of <c>async-cli service install</c> (<see cref="ServiceInstaller"/>):
/// a systemd user unit, a LaunchAgent, or a logon task. The service runs this program (<c>async-desktop service run</c>).
/// </summary>
internal sealed class DesktopAgentService(DesktopOptions options) : IAgentServiceManager
{
    public async Task<AgentServiceState> GetStateAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return AgentServiceState.Unsupported;
        var (installed, running, _) = await ServiceInstaller.StatusAsync(system: false, ct);
        // An agent of this config directory may also run outside a service (`async-cli run` in a terminal) or as the
        // system service: its lock says so even when its channel is not up yet.
        if (running || AgentLock.IsHeld(options.ConfigDirectory)) return AgentServiceState.Running;
        return installed ? AgentServiceState.Stopped : AgentServiceState.NotInstalled;
    }

    public async Task<string> InstallAndStartAsync(CancellationToken ct)
    {
        var result = await ServiceInstaller.InstallAsync(new ServiceInstallOptions
        {
            Program = ServiceInstaller.CurrentProgram(), ConfigDirectory = options.ConfigDirectory, Verbose = options.Verbose,
        }, start: true, ct);
        if (!result.Ok) throw new ServiceCommandException(result.Message);
        return "The ASync agent is installed and runs in the background.";
    }

    public async Task<string> StartAsync(CancellationToken ct)
    {
        var result = await ServiceInstaller.StartAsync(system: false, ct);
        if (!result.Ok) throw new ServiceCommandException(result.Message);
        return result.Message;
    }
}
