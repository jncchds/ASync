using ASync.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ASync.App.ViewModels;

/// <summary>
/// The phone's background side, in Settings and as the dashboard's prompt: the battery-optimisation exemption (explained,
/// offered, the maker's extra steps as text), UnifiedPush wake-ups through an installed distributor, and the local
/// "nothing synced in N days" reminder. Only exists when the platform has <see cref="IPlatformServices.Background"/>.
/// </summary>
public sealed partial class BackgroundViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly IBackgroundServices _services;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private bool _loading;

    public BackgroundViewModel(ShellViewModel shell, IBackgroundServices services)
    {
        _shell = shell;
        _services = services;
        services.Changed += () =>
        {
            if (_context is null) Refresh();
            else _context.Post(_ => Refresh(), null);
        };
        Refresh();
    }

    public static readonly int[] ReminderChoices = [0, 1, 2, 3, 7, 14];

    public IReadOnlyList<string> ReminderOptions { get; } =
        [.. ReminderChoices.Select(d => d switch { 0 => "Never", 1 => "After 1 day", _ => $"After {d} days" })];

    // ── Battery ───────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBatteryPrompt), nameof(BatteryText))]
    public partial bool BatteryUnrestricted { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBatteryPrompt))]
    public partial bool BatteryPromptDismissed { get; private set; }

    /// <summary>The dashboard's prompt: optimised, and not dismissed.</summary>
    public bool ShowBatteryPrompt => !BatteryUnrestricted && !BatteryPromptDismissed;

    public string BatteryText => BatteryUnrestricted
        ? "ASync may run in the background without battery restrictions."
        : "Battery optimisation is on for ASync: Android may delay or skip its background syncs, most of all when the phone is idle.";

    public const string BatteryExplanation =
        "Folders sync in the background on their schedule, also with the app closed. Android's battery optimisation can hold that " +
        "work back for hours. Allowing ASync to run in the background lets it sync on time; transfers still wait for the " +
        "conditions you set (Wi-Fi, charging).";

    public string? VendorHint => _services.VendorHint;
    public bool HasVendorHint => VendorHint is not null;

    [RelayCommand]
    private void AllowBattery() => _services.RequestIgnoreBatteryOptimizations();

    [RelayCommand]
    private void OpenBatterySettings() => _services.OpenBatterySettings();

    [RelayCommand]
    private void DismissBatteryPrompt()
    {
        BatteryPromptDismissed = true;
        _shell.SaveSettings(_shell.Settings with { BatteryPromptDismissed = true });
    }

    // ── UnifiedPush ───────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDistributors), nameof(NoDistributor))]
    public partial IReadOnlyList<PushDistributor> Distributors { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterPushCommand))]
    public partial PushDistributor? SelectedDistributor { get; set; }

    public bool HasDistributors => Distributors.Count > 0;
    public bool NoDistributor => Distributors.Count == 0 && !PushActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoDistributor))]
    [NotifyCanExecuteChangedFor(nameof(UnregisterPushCommand))]
    public partial bool PushActive { get; private set; }

    [ObservableProperty] public partial string PushText { get; private set; } = "";

    [ObservableProperty] public partial bool PushProblem { get; private set; }

    public const string PushExplanation =
        "With a UnifiedPush distributor such as ntfy installed, the server can wake this phone when another device changes a " +
        "folder, when it has not synced for too long, or when you browse its folders from the web. Nothing else goes through it: " +
        "the message only says \"wake up\". " +
        "Without one, folders still sync on their schedule.";

    private bool CanRegister() => SelectedDistributor is not null;

    [RelayCommand(CanExecute = nameof(CanRegister))]
    private void RegisterPush()
    {
        if (SelectedDistributor is { } d) _services.RegisterPush(d.Id);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(PushActive))]
    private void UnregisterPush()
    {
        _services.UnregisterPush();
        Refresh();
    }

    // ── Reminder ──────────────────────────────────────────────────────────────

    [ObservableProperty] public partial int ReminderIndex { get; set; }

    partial void OnReminderIndexChanged(int value)
    {
        if (_loading || value < 0 || value >= ReminderChoices.Length) return;
        _services.ReminderDays = ReminderChoices[value];
    }

    /// <summary>Reads everything again (after the system's battery question, a distributor's answer, the app coming back).</summary>
    public void Refresh()
    {
        _loading = true;
        try
        {
            BatteryUnrestricted = _services.IsIgnoringBatteryOptimizations;
            BatteryPromptDismissed = _shell.Settings.BatteryPromptDismissed;
            Distributors = _services.Distributors();
            var push = _services.Push;
            PushActive = push.Distributor is not null;
            SelectedDistributor = Distributors.FirstOrDefault(d => d.Id == push.Distributor) ?? SelectedDistributor ?? Distributors.FirstOrDefault();
            PushProblem = push.Problem is not null;
            PushText = push switch
            {
                { Problem: { } problem } => problem,
                { Registered: true } => $"The server can wake this phone through {push.DistributorName ?? push.Distributor}.",
                { HasEndpoint: true } => $"{push.DistributorName ?? push.Distributor} gave an address; sending it to the server…",
                { Distributor: not null } => $"Waiting for {push.DistributorName ?? push.Distributor} to answer…",
                _ when Distributors.Count == 0 => "No UnifiedPush distributor is installed. Install ntfy (F-Droid or GitHub) to let the server wake this phone.",
                _ => "Off: folders sync on their schedule only.",
            };
            var days = _services.ReminderDays;
            ReminderIndex = Array.IndexOf(ReminderChoices, days) is >= 0 and var i ? i : Array.IndexOf(ReminderChoices, 3);
        }
        finally
        {
            _loading = false;
        }
    }
}
