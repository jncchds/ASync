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
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(IsNotWaiting), nameof(ExpiresText), nameof(ApproveHint))]
    public partial PendingEnrollmentDto? Pending { get; private set; }

    public bool IsWaiting => Pending is not null;
    public bool IsNotWaiting => Pending is null;

    public string ExpiresText => Pending is { } p ? $"The code expires at {p.ExpiresAt.ToLocalTime():HH:mm}." : "";

    public string Intro => _shell.Platform.OffersWebUi
        ? $"Connect this {_shell.Platform.DeviceNoun} to your ASync server. Its folders are set up in the web UI afterwards."
        : $"Connect this {_shell.Platform.DeviceNoun} to your ASync server: scan the QR code or type the code from the web UI on your computer " +
          "(Devices → Add device), or get a code here and approve it there.";

    /// <summary>The device-code choice: approved in this device's browser, or on another device (phones).</summary>
    public string DeviceCodeLabel => _shell.Platform.OffersWebUi ? "Approve in the browser" : "Approve on your computer";

    /// <summary>Before the code is asked for: how it works here.</summary>
    public string DeviceCodeIntro => _shell.Platform.OffersWebUi
        ? "The browser opens the server's approval page; sign in there and approve this device."
        : $"This {_shell.Platform.DeviceNoun} shows a short code; enter it in the web UI on your computer (Devices → Approve) to let it in.";

    public bool OffersWebUi => _shell.Platform.OffersWebUi;

    /// <summary>Under the code: where to approve it.</summary>
    public string ApproveHint => Pending is { } p
        ? _shell.Platform.OffersWebUi
            ? $"Approve it in the browser that opened ({p.VerificationUri}), signed in to your account."
            : $"On your computer, open {p.VerificationUri} (or Devices → Approve in the web UI), sign in and enter this code."
        : "";

    /// <summary>A camera to scan the web UI's QR code with (phones).</summary>
    public bool CanScan => _shell.Platform.CanScanQrCode;

    /// <summary>
    /// A pairing link from outside the form: an opened <c>async://pair</c> link (Android's intent filter) or a scanned QR
    /// code. Fills in the server and the code; false when it is not a pairing link.
    /// </summary>
    public bool ApplyLink(string link)
    {
        if (!PairingLink.TryParse(link, out var server, out var code)) return false;
        UseCode = true;
        Error = null;
        Server = server;
        Code = code;
        return true;
    }

    /// <summary>Scans the QR code of Devices → Add device and enrolls with it at once.</summary>
    [RelayCommand]
    private async Task Scan()
    {
        var text = await _shell.Platform.ScanQrCodeAsync();
        if (text is null) return;
        if (!ApplyLink(text))
        {
            Error = "That QR code is not an ASync pairing code. Scan the one under Devices → Add device in the web UI.";
            return;
        }
        if (EnrollCommand.CanExecute(null)) await EnrollCommand.ExecuteAsync(null);
    }

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
            // A phone shows the code to approve on a computer; it does not open its own browser.
            if (_shell.Platform.OffersWebUi) await _shell.Platform.OpenUrlAsync(started.VerificationUri);
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
