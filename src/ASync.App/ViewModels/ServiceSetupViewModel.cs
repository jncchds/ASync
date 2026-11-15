using ASync.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ASync.App.ViewModels;

/// <summary>No agent answers: install the per-user service and start it, or start the installed one.</summary>
public sealed partial class ServiceSetupViewModel(ShellViewModel shell, IAgentServiceManager service) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Text), nameof(CanInstall), nameof(CanStart))]
    public partial AgentServiceState State { get; set; } = AgentServiceState.NotInstalled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(StartCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    public string Title => State switch
    {
        AgentServiceState.Stopped => "The ASync agent is not running",
        AgentServiceState.Unsupported => "No ASync agent here",
        _ => "Set up the ASync agent",
    };

    public string Text => State switch
    {
        AgentServiceState.Stopped =>
            "The agent is installed but stopped. It syncs your folders in the background, also while this window is closed.",
        AgentServiceState.Unsupported =>
            "This system cannot run the agent as a service. Start it by hand with `async-cli run`; this window connects to it.",
        _ => "The agent syncs your folders in the background, also while this window is closed. " +
             "It runs as you, starts when you log in, and this app shows and steers it.",
    };

    public bool CanInstall => State == AgentServiceState.NotInstalled;
    public bool CanStart => State == AgentServiceState.Stopped;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task Install() => DoAsync(service.InstallAndStartAsync);

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task Start() => DoAsync(service.StartAsync);

    [RelayCommand]
    private void Retry() => shell.Retry();

    private bool NotBusy() => !IsBusy;

    private async Task DoAsync(Func<CancellationToken, Task<string>> action)
    {
        IsBusy = true;
        Error = null;
        try
        {
            shell.ShowMessage(await action(CancellationToken.None));
            shell.Retry();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
