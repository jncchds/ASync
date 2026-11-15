using ASync.Agent.Control;
using ASync.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ASync.App.ViewModels;

/// <summary>
/// First run: the server's address, then a code from the web UI (or its pasted <c>async://pair</c> link), or the
/// device-code flow (show a code, open the approval link, the agent waits for the approval). The agent enrolls and keeps
/// the identity; the page switches to the dashboard when its status says so.
/// </summary>
public sealed partial class EnrollViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public EnrollViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Server = shell.Settings.LastServer ?? "";
        DeviceName = shell.Platform.MachineName;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnrollCommand), nameof(StartDeviceCodeCommand))]
    public partial string Server { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnrollCommand), nameof(StartDeviceCodeCommand))]
    public partial string DeviceName { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnrollCommand))]
    public partial string Code { get; set; } = "";

    /// <summary>Approve in the browser instead of typing a code.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseCode))]
    public partial bool UseDeviceCode { get; set; }

    public bool UseCode
    {
        get => !UseDeviceCode;
        set => UseDeviceCode = !value;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnrollCommand), nameof(StartDeviceCodeCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>A device-code request waiting for approval.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(IsNotWaiting), nameof(ExpiresText))]
    public partial PendingEnrollmentDto? Pending { get; private set; }

    public bool IsWaiting => Pending is not null;
    public bool IsNotWaiting => Pending is null;

    public string ExpiresText => Pending is { } p ? $"The code expires at {p.ExpiresAt.ToLocalTime():HH:mm}." : "";

    /// <summary>A pasted pairing link fills in the server and the code.</summary>
    partial void OnCodeChanged(string value)
    {
        if (!PairingLink.TryParse(value, out var server, out var code)) return;
        Server = server;
        Code = code;
    }

    public void Update(AgentStatusDto status)
    {
        if (status.Enrollment is { Error: { } failed })
        {
            Pending = null;
            Error = failed;
        }
        else
        {
            Pending = status.Enrollment;
        }
    }

    private bool CanEnroll() => !IsBusy && Server.Trim().Length > 0 && DeviceName.Trim().Length > 0 && Code.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanEnroll))]
    private async Task Enroll()
    {
        IsBusy = true;
        Error = null;
        try
        {
            var ok = await _shell.RunAsync(async (s, ct) =>
            {
                var status = await s.EnrollAsync(Server.Trim(), Code.Trim(), DeviceName.Trim(), _shell.Platform.Platform, ct);
                Remember(status.Device?.ServerUrl);
                return $"Enrolled \"{status.Device?.DeviceName}\". Attach folders to it in the web UI.";
            });
            if (ok) Code = "";
            else TakeError();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStart() => !IsBusy && Server.Trim().Length > 0 && DeviceName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartDeviceCode()
    {
        IsBusy = true;
        Error = null;
        try
        {
            PendingEnrollmentDto? started = null;
            var ok = await _shell.RunAsync(async (s, ct) =>
            {
                started = await s.StartDeviceCodeAsync(Server.Trim(), DeviceName.Trim(), _shell.Platform.Platform, ct);
                return null;
            });
            if (!ok || started is null)
            {
                TakeError();
                return;
            }
            Pending = started;
            Remember(started.Server);
            await _shell.Platform.OpenUrlAsync(started.VerificationUri);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenApprovalLink()
    {
        if (Pending is { } p) await _shell.Platform.OpenUrlAsync(p.VerificationUri);
    }

    /// <summary>Gives up on the device-code request (the agent forgets it).</summary>
    [RelayCommand]
    private async Task CancelPending()
    {
        await _shell.RunAsync(async (s, ct) =>
        {
            await s.ForgetAsync(ct);
            return null;
        });
        Pending = null;
    }

    /// <summary>The error goes next to the form rather than into the banner.</summary>
    private void TakeError()
    {
        if (!_shell.MessageIsError) return;
        Error = _shell.Message;
        _shell.ShowMessage(null);
    }

    private void Remember(string? server)
    {
        if (server is { Length: > 0 }) _shell.SaveSettings(_shell.Settings with { LastServer = server });
    }
}
