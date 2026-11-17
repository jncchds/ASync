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
    /// <summary>End-to-end and not set up yet: this device can set it up (passphrase, recovery key).</summary>
    [ObservableProperty] public partial bool NeedsSetup { get; private set; }

    /// <summary>Locked but set up: the passphrase or recovery key unlocks it here.</summary>
    public bool CanBeUnlocked => Locked && !NeedsSetup;
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
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand), nameof(TogglePauseCommand), nameof(ChooseFolderCommand), nameof(ChooseSystemFolderCommand),
        nameof(SwitchToRealPathCommand), nameof(SaveConditionsCommand), nameof(ChoosePhotosCommand), nameof(RestorePhotosCommand))]
    public partial bool Working { get; private set; }

    public string PauseText => Paused ? "Resume" : "Pause";

    public bool NeedsPath => !HasLocalPath && !IsPhotos;

    /// <summary>The buttons of a folder on disk (open, change): not for a Photos folder, which has a selection instead.</summary>
    public bool ShowFolderButtons => HasLocalPath && !IsPhotos;

    /// <summary>Sync now / Back up now: a folder with a place on this device, or a Photos folder.</summary>
    public bool CanRunHere => HasLocalPath || IsPhotos;

    public string RunNowText => IsPhotos ? "Back up now" : "Sync now";

    // ── Photos folders (task 19) ──────────────────────────────────────────────

    [ObservableProperty] public partial bool IsPhotos { get; private set; }

    /// <summary>What a Photos folder backs up here ("All photos and videos", "2 albums: …").</summary>
    [ObservableProperty] public partial string? MediaText { get; private set; }

    /// <summary>What its last backup holds ("1,204 photos, 37 videos · 6.1 GB").</summary>
    [ObservableProperty] public partial string? MediaCountsText { get; private set; }

    /// <summary>ASync may not read the photos (or only some): what to do about it, or null.</summary>
    [ObservableProperty] public partial string? PhotoAccessText { get; private set; }

    [ObservableProperty] public partial bool RestoreRunning { get; private set; }
    [ObservableProperty] public partial string? RestoreText { get; private set; }
    [ObservableProperty] public partial double RestorePercent { get; private set; }

    private MediaAccess PhotoAccess => shell.Platform.Photos?.Access ?? MediaAccess.Unsupported;

    /// <summary>The Photos screen: the permission's explanation while it is missing, then all photos or chosen albums.</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ChoosePhotos()
    {
        var dialog = new PhotosViewModel(shell, Name, _folder.Media, async selection =>
        {
            Working = true;
            try { return await shell.RunAsync((s, ct) => s.SetMediaAsync(Id, selection, ct)!); }
            finally { Working = false; }
        });
        shell.PhotosDialog = dialog;
        await dialog.LoadAlbumsAsync();
    }

    private bool CanRestorePhotos() => !Working && !RestoreRunning && IsPhotos && _folder.LastResult is not null;

    /// <summary>Puts this device's newest snapshot back into the photo library (what is already there is skipped).</summary>
    [RelayCommand(CanExecute = nameof(CanRestorePhotos))]
    private async Task RestorePhotos()
    {
        Working = true;
        try { await shell.RunAsync((s, ct) => s.RestoreMediaAsync(Id, null, ct)!); }
        finally { Working = false; }
    }

    /// <summary>The agent's restore into the library, when it is this folder's.</summary>
    public void UpdateRestore(MediaRestoreDto? restore)
    {
        if (restore is null || restore.FolderId != Id)
        {
            RestoreRunning = false;
            RestoreText = null;
        }
        else
        {
            RestoreRunning = restore.Running;
            RestorePercent = restore.Total > 0 ? 100.0 * restore.Done / restore.Total : 0;
            RestoreText = restore.Running
                ? $"Restoring snapshot {restore.Snapshot} into the photo library: {restore.Done} of {restore.Total} ({restore.Restored} restored, {restore.Skipped} already there)"
                : $"Restore of snapshot {restore.Snapshot}: {restore.Message}";
        }
        RestorePhotosCommand.NotifyCanExecuteChanged();
    }

    public string ChooseFolderText => HasLocalPath ? "Change folder…" : "Choose folder…";

    public void Update(FolderStatusDto f)
    {
        _folder = f;
        Name = f.Name;
        ModeText = Format.Mode(f.Mode, f.Direction);
        IsEndToEnd = f.Encryption == FolderEncryption.EndToEnd;
        IsPhotos = f.Kind == FolderKind.Photos;
        MediaText = IsPhotos ? (f.Media ?? MediaSelectionDto.Everything).Describe() : null;
        MediaCountsText = f.MediaCounts is { } counts ? $"{Format.MediaCounts(counts.Photos, counts.Videos)} · {Format.Bytes(counts.Bytes)}" : null;
        PhotoAccessText = !IsPhotos ? null : PhotoAccess switch
        {
            MediaAccess.None => "ASync may not read the photos yet: open Photos… to allow it.",
            MediaAccess.Partial => "ASync may read only the photos chosen for it (\"Select photos\"): only those are backed up.",
            MediaAccess.Unsupported => $"This {shell.Platform.DeviceNoun} has no photo library: Photos folders are backed up by phones.",
            _ => null,
        };
        LocalPath = IsPhotos ? $"Photo library: {MediaText}"
            : f.LocalPath is { Length: > 0 } path ? shell.Platform.DisplayPath(path) : $"No folder on this {shell.Platform.DeviceNoun} yet";
        HasLocalPath = f.LocalPath is { Length: > 0 };
        Paused = f.Paused;
        Locked = f.Locked;
        NeedsSetup = f.NeedsSetup;

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
            { NeedsSetup: true } => new Pill("Not set up", PillKind.Warning),
            { Locked: true } => new Pill("Locked", PillKind.Warning),
            { Kind: FolderKind.Photos } when PhotoAccess is MediaAccess.None or MediaAccess.Unsupported => new Pill("No access", PillKind.Warning),
            { Kind: not FolderKind.Photos, LocalPath.Length: 0 } => new Pill("No local folder", PillKind.Warning),
            { LastResult: { Ok: false, Skipped: false } } => new Pill("Failed", PillKind.Danger),
            { Alerts: [{ Kind: AlertKind.Failing or AlertKind.CheckFailed or AlertKind.PruneFailed }, ..] } => new Pill("Problem", PillKind.Danger),
            { Alerts: [{ Kind: AlertKind.Overdue }, ..] } => new Pill("Overdue", PillKind.Warning),
            { Waiting: not null } => new Pill("Waiting", PillKind.Muted),
            { LastResult: not null } => new Pill("Up to date", PillKind.Success),
            _ => new Pill("Waiting", PillKind.Muted),
        };
        OnPropertyChanged(nameof(PauseText));
        OnPropertyChanged(nameof(CanBeUnlocked));
        SetUpEncryptionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(NeedsPath));
        OnPropertyChanged(nameof(ShowFolderButtons));
        OnPropertyChanged(nameof(CanRunHere));
        OnPropertyChanged(nameof(RunNowText));
        RestorePhotosCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ChooseFolderText));
        RefreshStorage();
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
            { NeedsSetup: true } => "Waits for its encryption to be set up",
            { Locked: true } => "Waits to be unlocked",
            { Kind: not FolderKind.Photos, LocalPath.Length: 0 } => "Waits for a local folder",
            { Waiting: { } waiting } => waiting,
            { NextRunAt: { } next } => $"Next run {Format.In(next, now)}",
            _ => $"Every {_folder.IntervalMinutes} min",
        };
    }

    private bool CanSync() => !Working && CanRunHere && !Paused && !Locked;

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

    /// <summary>
    /// Choose or change the folder's local path. On a phone with <see cref="IPlatformServices.Storage"/>: the in-app picker
    /// over real paths with All files access, else first the screen that explains it (<see cref="LocalFolderViewModel"/>),
    /// which also offers the system picker. Elsewhere the platform's own picker.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ChooseFolder()
    {
        if (shell.Platform.Storage is { } storage)
        {
            shell.FolderDialog = new LocalFolderViewModel(shell, storage, Name, SetRealPathAsync, () => ChooseSystemFolderCommand.ExecuteAsync(null),
                HasLocalPath ? _folder.LocalPath : null);
            return;
        }
        await ChooseSystemFolder();
    }

    /// <summary>A real path from the in-app picker; a document tree it replaces gives its permission back.</summary>
    private async Task<bool> SetRealPathAsync(string path)
    {
        var previous = _folder.LocalPath;
        Working = true;
        try
        {
            if (!await shell.RunAsync((s, ct) => s.SetPathAsync(Id, path, ct)!)) return false;
        }
        finally { Working = false; }
        if (previous.StartsWith("content://", StringComparison.Ordinal) && previous != path) shell.Platform.Storage?.ReleaseTree(previous);
        return true;
    }

    /// <summary>
    /// A document-tree folder whose real path ASync may use now (All files access granted): the offer to switch, which
    /// keeps the folder's sync state (the agent sees the same files: <c>FolderSyncer</c>'s rebase). Null otherwise.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitchToRealPath), nameof(SwitchText))]
    public partial string? RealPathOffer { get; private set; }

    public bool CanSwitchToRealPath => RealPathOffer is not null;

    public string SwitchText => RealPathOffer is { } p ? $"Use the real path {p}" : "";

    /// <summary>Looks again whether the switch can be offered (the permission may have changed).</summary>
    public void RefreshStorage()
    {
        string? offer = null;
        if (shell.Platform.Storage is { HasAllFilesAccess: true } storage && _folder.LocalPath.StartsWith("content://", StringComparison.Ordinal))
        {
            try
            {
                if (storage.RealPathOf(_folder.LocalPath) is { } real && Directory.Exists(real)) offer = real;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        RealPathOffer = offer;
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task SwitchToRealPath()
    {
        if (RealPathOffer is { } real) await SetRealPathAsync(real);
    }

    /// <summary>The platform's own picker (on Android the system's document-tree picker: the fallback without All files access).</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ChooseSystemFolder()
    {
        var path = await shell.Platform.PickFolderAsync($"Where \"{Name}\" syncs on this {shell.Platform.DeviceNoun}", HasLocalPath ? _folder.LocalPath : null);
        if (path is null) return;
        Working = true;
        try { await shell.RunAsync((s, ct) => s.SetPathAsync(Id, path, ct)!); }
        finally { Working = false; }
    }

    [RelayCommand(CanExecute = nameof(CanBeUnlocked))]
    private void Unlock() => shell.Dialog = new UnlockViewModel(shell, Id, Name);

    /// <summary>A new end-to-end folder: this device creates its repository (what `async-cli init-encryption` does).</summary>
    [RelayCommand(CanExecute = nameof(NeedsSetup))]
    private void SetUpEncryption() => shell.Dialog = new UnlockViewModel(shell, Id, Name, setup: true);

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
