using ASync.Agent.Control;
using ASync.App.Services;
using ASync.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ASync.App.ViewModels;

/// <summary>One folder on this device: its settings, what it is doing, its last result and next run, and what can be done with it.</summary>
public sealed partial class FolderViewModel(ShellViewModel shell, int id) : ObservableObject
{
    private FolderStatusDto _folder = new() { FolderId = id };

    public int Id { get; } = id;

    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial string ModeText { get; private set; } = "";
    [ObservableProperty] public partial bool IsEndToEnd { get; private set; }
    [ObservableProperty] public partial string LocalPath { get; private set; } = "";
    [ObservableProperty] public partial bool HasLocalPath { get; private set; }
    [ObservableProperty] public partial bool Paused { get; private set; }
    [ObservableProperty] public partial bool Locked { get; private set; }
    [ObservableProperty] public partial Pill State { get; private set; } = new("", PillKind.Muted);

    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial bool IsProgressIndeterminate { get; private set; }
    [ObservableProperty] public partial double ProgressPercent { get; private set; }
    [ObservableProperty] public partial string ProgressText { get; private set; } = "";
    [ObservableProperty] public partial string? CurrentFile { get; private set; }

    [ObservableProperty] public partial bool HasLastResult { get; private set; }
    [ObservableProperty] public partial bool LastResultOk { get; private set; }
    [ObservableProperty] public partial string LastResultText { get; private set; } = "";
    [ObservableProperty] public partial string LastRunText { get; private set; } = "";
    [ObservableProperty] public partial string NextRunText { get; private set; } = "";

    /// <summary>This device's run conditions in words ("Runs by itself only on Wi-Fi, while charging"), or null.</summary>
    [ObservableProperty] public partial string? ConditionsText { get; private set; }

    /// <summary>Why automatic runs wait now (the conditions), or null.</summary>
    [ObservableProperty] public partial string? WaitingText { get; private set; }

    /// <summary>The run-conditions editor is open.</summary>
    [ObservableProperty] public partial bool EditingConditions { get; private set; }
    [ObservableProperty] public partial bool EditUnmeteredOnly { get; set; }
    [ObservableProperty] public partial bool EditChargingOnly { get; set; }
    [ObservableProperty] public partial bool EditBatteryNotLow { get; set; }

    /// <summary>What this platform ignores of the conditions.</summary>
    public string? ConditionsNote => shell.Platform.ConditionsNote;
    public bool HasConditionsNote => ConditionsNote is not null;

    /// <summary>What the server's overdue detection says (Overdue, Failing, a failed check).</summary>
    [ObservableProperty] public partial string? AlertText { get; private set; }

    /// <summary>A request of this folder is running (sync now, pause, a new path).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand), nameof(TogglePauseCommand), nameof(ChooseFolderCommand), nameof(SaveConditionsCommand))]
    public partial bool Working { get; private set; }

    public string PauseText => Paused ? "Resume" : "Pause";

    public bool NeedsPath => !HasLocalPath;

    public string ChooseFolderText => HasLocalPath ? "Change folder…" : "Choose folder…";

    public void Update(FolderStatusDto f)
    {
        _folder = f;
        Name = f.Name;
        ModeText = Format.Mode(f.Mode, f.Direction);
        IsEndToEnd = f.Encryption == FolderEncryption.EndToEnd;
        LocalPath = f.LocalPath is { Length: > 0 } path ? shell.Platform.DisplayPath(path) : $"No folder on this {shell.Platform.DeviceNoun} yet";
        HasLocalPath = f.LocalPath is { Length: > 0 };
        Paused = f.Paused;
        Locked = f.Locked;

        var busy = f.Activity is FolderActivity.Syncing or FolderActivity.BackingUp or FolderActivity.Maintenance;
        IsBusy = busy;
        if (busy && f.Progress is { } p)
        {
            var fraction = p.TotalBytes is > 0 and long totalBytes ? (double)p.Bytes / totalBytes
                : p.TotalFiles is > 0 and int totalFiles ? (double)p.Files / totalFiles
                : (double?)null;
            IsProgressIndeterminate = fraction is null;
            ProgressPercent = Math.Clamp((fraction ?? 0) * 100, 0, 100);
            ProgressText = Format.Progress(p);
            CurrentFile = p.CurrentFile;
        }
        else
        {
            IsProgressIndeterminate = busy;
            ProgressPercent = 0;
            ProgressText = busy ? "Starting…" : "";
            CurrentFile = null;
        }

        HasLastResult = f.LastResult is not null;
        LastResultOk = f.LastResult is { Ok: true } or { Skipped: true };
        LastResultText = f.LastResult is { } r ? (r.Ok || r.Skipped ? r.Message : $"Failed: {r.Message}") : "Not run on this device yet";
        ConditionsText = Describe(f.Conditions);
        WaitingText = f.Waiting;
        AlertText = f.Alerts is { Count: > 0 } alerts
            ? string.Join("\n", alerts.Select(a => $"{Format.Alert(a.Kind)} (server): {a.Detail}"))
            : null;

        State = f switch
        {
            { Activity: FolderActivity.Syncing } => new Pill("Syncing", PillKind.Accent),
            { Activity: FolderActivity.BackingUp } => new Pill("Backing up", PillKind.Accent),
            { Activity: FolderActivity.Maintenance } => new Pill("Maintenance", PillKind.Accent),
            { Paused: true } => new Pill("Paused", PillKind.Muted),
            { Locked: true } => new Pill("Locked", PillKind.Warning),
            { LocalPath.Length: 0 } => new Pill("No local folder", PillKind.Warning),
            { LastResult: { Ok: false, Skipped: false } } => new Pill("Failed", PillKind.Danger),
            { Alerts: [{ Kind: AlertKind.Failing or AlertKind.CheckFailed or AlertKind.PruneFailed }, ..] } => new Pill("Problem", PillKind.Danger),
            { Alerts: [{ Kind: AlertKind.Overdue }, ..] } => new Pill("Overdue", PillKind.Warning),
            { Waiting: not null } => new Pill("Waiting", PillKind.Muted),
            { LastResult: not null } => new Pill("Up to date", PillKind.Success),
            _ => new Pill("Waiting", PillKind.Muted),
        };
        OnPropertyChanged(nameof(PauseText));
        OnPropertyChanged(nameof(NeedsPath));
        OnPropertyChanged(nameof(ChooseFolderText));
        SyncNowCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        UnlockCommand.NotifyCanExecuteChanged();
        RefreshTimes();
    }

    public void RefreshTimes()
    {
        var now = shell.Time.GetUtcNow();
        LastRunText = _folder.LastResult is { } r ? Format.Ago(r.At, now) : "";
        NextRunText = _folder switch
        {
            { Paused: true } => "Paused",
            { Locked: true } => "Waits to be unlocked",
            { LocalPath.Length: 0 } => "Waits for a local folder",
            { Waiting: { } waiting } => waiting,
            { NextRunAt: { } next } => $"Next run {Format.In(next, now)}",
            _ => $"Every {_folder.IntervalMinutes} min",
        };
    }

    private bool CanSync() => !Working && HasLocalPath && !Paused && !Locked;

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncNow()
    {
        Working = true;
        try
        {
            await shell.RunAsync(async (s, ct) =>
            {
                var result = (await s.SyncAsync(Id, ct)).FirstOrDefault();
                return result is null ? null : result.Ok || result.Skipped ? $"{result.Name}: {result.Message}" : throw new ControlException($"{result.Name}: {result.Message}");
            });
        }
        finally
        {
            Working = false;
        }
    }

    private bool CanChange() => !Working;

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task TogglePause()
    {
        Working = true;
        try { await shell.RunAsync((s, ct) => s.SetPausedAsync(Id, !Paused, ct)!); }
        finally { Working = false; }
    }

    [RelayCommand(CanExecute = nameof(HasLocalPath))]
    private Task OpenFolder() => shell.Platform.OpenFolderAsync(_folder.LocalPath);

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ChooseFolder()
    {
        var path = await shell.Platform.PickFolderAsync($"Where \"{Name}\" syncs on this {shell.Platform.DeviceNoun}", HasLocalPath ? _folder.LocalPath : null);
        if (path is null) return;
        Working = true;
        try { await shell.RunAsync((s, ct) => s.SetPathAsync(Id, path, ct)!); }
        finally { Working = false; }
    }

    [RelayCommand(CanExecute = nameof(Locked))]
    private void Unlock() => shell.Dialog = new UnlockViewModel(shell, Id, Name);

    public static string? Describe(RunConditionsDto? c)
    {
        if (c is null || !c.Any) return null;
        var parts = new List<string>();
        if (c.UnmeteredOnly) parts.Add("on Wi-Fi");
        if (c.ChargingOnly) parts.Add("while charging");
        if (c.BatteryNotLow) parts.Add("when the battery is not low");
        return $"Runs by itself only {string.Join(", ", parts)}";
    }

    [RelayCommand]
    private void EditConditions()
    {
        var c = _folder.Conditions ?? RunConditionsDto.None;
        (EditUnmeteredOnly, EditChargingOnly, EditBatteryNotLow) = (c.UnmeteredOnly, c.ChargingOnly, c.BatteryNotLow);
        EditingConditions = true;
    }

    [RelayCommand]
    private void CancelConditions() => EditingConditions = false;

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task SaveConditions()
    {
        Working = true;
        try
        {
            var conditions = new RunConditionsDto(EditUnmeteredOnly, EditChargingOnly, EditBatteryNotLow);
            if (await shell.RunAsync((s, ct) => s.SetConditionsAsync(Id, conditions, ct)!)) EditingConditions = false;
        }
        finally { Working = false; }
    }
}
