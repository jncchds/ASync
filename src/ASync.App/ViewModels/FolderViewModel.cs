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
        nameof(SwitchToRealPathCommand), nameof(SaveConditionsCommand), nameof(ChoosePhotosCommand), nameof(RestorePhotosCommand),
        nameof(ChooseCollectionsCommand))]
    public partial bool Working { get; private set; }

    public string PauseText => Paused ? "Resume" : "Pause";

    public bool NeedsPath => !HasLocalPath && !IsPhotos && !IsCollection;

    /// <summary>The buttons of a folder on disk (open, change): not for a Photos folder, which has a selection instead.</summary>
    public bool ShowFolderButtons => HasLocalPath && !IsPhotos && !IsCollection;

    /// <summary>Sync now / Back up now: a folder with a place on this device, or a Photos folder.</summary>
    public bool CanRunHere => HasLocalPath || IsPhotos || IsCollection;

    public string RunNowText => IsPhotos || IsCollection ? "Back up now" : "Sync now";

    /// <summary>A Photos, Contacts or Calendars folder: restored by importing into the phone.</summary>
    public bool CanImport => IsPhotos || IsCollection;

    // ── Contacts and Calendars folders (task 20) ──────────────────────────────

    /// <summary>A Contacts or Calendars folder: a selection of accounts or calendars instead of a local path.</summary>
    [ObservableProperty] public partial bool IsCollection { get; private set; }

    [ObservableProperty] public partial FolderKind Kind { get; private set; }

    public string ChooseCollectionsText => Kind == FolderKind.Calendars ? "Calendars…" : "Contacts…";

    public string RestoreButtonText => IsCollection ? $"Restore into the {CollectionTexts.Noun(Kind)}…" : "Restore into the library…";

    public string RestoreQuestion => IsCollection ? "Restore which snapshot, and into which " + (Kind == FolderKind.Calendars ? "calendar?" : "account?")
        : "Restore which snapshot into the library?";

    public string RestoreHint => IsCollection
        ? (Kind == FolderKind.Calendars ? "Events already in the calendar are skipped; nothing on the phone is changed or deleted."
            : "Contacts already in the account are skipped; nothing on the phone is changed or deleted.")
        : "Photos still on the phone are skipped. An older snapshot brings back what was deleted since.";

    private ICollectionAccess? CollectionAccess => shell.Platform.CollectionAccess(Kind);

    /// <summary>The Contacts or Calendars screen: the permission's explanation while it is missing, then everything or chosen ones.</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ChooseCollections()
    {
        var dialog = new CollectionsViewModel(shell, Kind, Name, _folder.Collections, async selection =>
        {
            Working = true;
            try { return await shell.RunAsync((s, ct) => s.SetCollectionsAsync(Id, selection, ct)!); }
            finally { Working = false; }
        });
        shell.CollectionsDialog = dialog;
        await dialog.LoadAsync();
    }

    /// <summary>Where a restore imports to: the original accounts / calendars, or one of the phone's.</summary>
    public System.Collections.ObjectModel.ObservableCollection<RestoreTarget> RestoreTargets { get; } = [];

    [ObservableProperty] public partial RestoreTarget? SelectedTarget { get; set; }

    public bool ChoosesTarget => IsCollection && ChoosingRestore;

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

    private bool CanRestorePhotos() => !Working && !RestoreRunning && CanImport && (_folder.LastResult is not null || IsCollection);

    /// <summary>The snapshots to choose from (newest first), while <see cref="ChoosingRestore"/>.</summary>
    public System.Collections.ObjectModel.ObservableCollection<RestoreChoice> RestoreChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChoosesTarget))]
    public partial bool ChoosingRestore { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmRestoreCommand))]
    public partial RestoreChoice? SelectedRestore { get; set; }

    /// <summary>Restore into the library: first which snapshot (this phone's newest preselected; an older one brings back what was deleted since).</summary>
    [RelayCommand(CanExecute = nameof(CanRestorePhotos))]
    private async Task RestorePhotos()
    {
        if (shell.Session is not { } session) return;
        Working = true;
        try
        {
            var snapshots = IsCollection ? await session.CollectionSnapshotsAsync(Id, CancellationToken.None) : await session.MediaSnapshotsAsync(Id, CancellationToken.None);
            if (snapshots.Count == 0)
            {
                shell.ShowMessage($"\"{Name}\" has no snapshots yet.");
                return;
            }
            RestoreChoices.Clear();
            var now = shell.Time.GetUtcNow();
            foreach (var s in snapshots) RestoreChoices.Add(new RestoreChoice(s, now));
            SelectedRestore = RestoreChoices.FirstOrDefault(c => c.Snapshot.Mine) ?? RestoreChoices[0];
            if (IsCollection)
            {
                RestoreTargets.Clear();
                RestoreTargets.Add(new RestoreTarget(null, Kind == FolderKind.Calendars ? "The calendars they came from" : "The accounts they came from"));
                try
                {
                    var list = await session.CollectionListAsync(Kind, CancellationToken.None);
                    foreach (var c in list.Collections.Where(c => !c.ReadOnly))
                        RestoreTargets.Add(new RestoreTarget(c.Id, c.Account is { Length: > 0 } a ? $"{c.Name} ({a})" : c.Name));
                }
                catch (Exception e) when (e is ControlException or IOException) { }
                SelectedTarget = RestoreTargets[0];
            }
            ChoosingRestore = true;
        }
        catch (Exception e) when (e is ControlException or IOException or InvalidDataException or ObjectDisposedException)
        {
            shell.ShowMessage(e.Message, error: true);
        }
        finally { Working = false; }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmRestore))]
    private async Task ConfirmRestore()
    {
        var chosen = SelectedRestore!.Snapshot.Id;
        var target = SelectedTarget?.Id;
        ChoosingRestore = false;
        Working = true;
        try
        {
            if (IsCollection)
            {
                // Adding contacts or events is asked only now, for the restore.
                if (CollectionAccess is { CanWrite: false } access && !await AskWriteAsync(access))
                {
                    shell.ShowMessage($"ASync may not add {CollectionTexts.Noun(Kind)}: allow it to restore (in the app's settings if the question no longer appears).", error: true);
                    return;
                }
                await shell.RunAsync((s, ct) => s.RestoreCollectionsAsync(Id, chosen, target, ct)!);
            }
            else await shell.RunAsync((s, ct) => s.RestoreMediaAsync(Id, chosen, ct)!);
        }
        finally { Working = false; }
    }

    private bool CanConfirmRestore() => SelectedRestore is not null;

    /// <summary>Asks for the permission to add and waits for the answer (at most a minute).</summary>
    private static async Task<bool> AskWriteAsync(ICollectionAccess access)
    {
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged() => answered.TrySetResult();
        access.Changed += OnChanged;
        try
        {
            access.RequestWrite();
            if (!access.CanWrite) await Task.WhenAny(answered.Task, Task.Delay(TimeSpan.FromMinutes(1)));
            return access.CanWrite;
        }
        finally { access.Changed -= OnChanged; }
    }

    [RelayCommand]
    private void CancelRestore() => ChoosingRestore = false;

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
            // Progress and text first, the running flag last: whoever reacts to RestoreRunning sees the matching numbers.
            RestorePercent = restore.Total > 0 ? 100.0 * restore.Done / restore.Total : 0;
            var into = IsCollection ? $"the {CollectionTexts.Noun(Kind)}" : "the photo library";
            RestoreText = restore.Running
                ? $"Restoring snapshot {restore.Snapshot} into {into}: {restore.Done} of {restore.Total} ({restore.Restored} restored, {restore.Skipped} already there)"
                : $"Restore of snapshot {restore.Snapshot}: {restore.Message}";
            RestoreRunning = restore.Running;
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
        Kind = f.Kind;
        IsCollection = f.Kind is FolderKind.Contacts or FolderKind.Calendars;
        MediaText = IsPhotos ? (f.Media ?? MediaSelectionDto.Everything).Describe() : null;
        MediaCountsText = f.MediaCounts is { } counts ? $"{Format.MediaCounts(counts.Photos, counts.Videos)} · {Format.Bytes(counts.Bytes)}"
            : IsCollection && f.CollectionCounts is { } items ? $"{CollectionTexts.Counts(f.Kind, items.Items, items.Collections)} · {Format.Bytes(items.Bytes)}"
            : null;
        PhotoAccessText = !IsPhotos ? null : PhotoAccess switch
        {
            MediaAccess.None => "ASync may not read the photos yet: open Photos… to allow it.",
            MediaAccess.Partial => (shell.Platform.Photos?.Texts ?? PhotoTexts.Android).CardPartial,
            MediaAccess.Unsupported => $"This {shell.Platform.DeviceNoun} has no photo library: Photos folders are backed up by phones.",
            _ => null,
        };
        if (IsCollection)
            PhotoAccessText = (CollectionAccess?.Access ?? MediaAccess.Unsupported) switch
            {
                MediaAccess.None => $"ASync may not read the {CollectionTexts.Noun(f.Kind)} yet: open {ChooseCollectionsText} to allow it.",
                MediaAccess.Partial => CollectionAccess?.PartialText,
                MediaAccess.Unsupported => $"This {shell.Platform.DeviceNoun} has no {CollectionTexts.Noun(f.Kind)}: {f.Kind} folders are backed up by phones.",
                _ => null,
            };
        LocalPath = IsPhotos ? $"Photo library: {MediaText}"
            : IsCollection ? (f.Collections ?? CollectionSelectionDto.Everything).Describe(f.Kind)
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
            { Kind: FolderKind.Contacts or FolderKind.Calendars } when (CollectionAccess?.Access ?? MediaAccess.Unsupported) is MediaAccess.None or MediaAccess.Unsupported
                => new Pill("No access", PillKind.Warning),
            { Kind: FolderKind.Files, LocalPath.Length: 0 } => new Pill("No local folder", PillKind.Warning),
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
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(ChooseCollectionsText));
        OnPropertyChanged(nameof(RestoreButtonText));
        OnPropertyChanged(nameof(RestoreQuestion));
        OnPropertyChanged(nameof(RestoreHint));
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
            { Kind: FolderKind.Files, LocalPath.Length: 0 } => "Waits for a local folder",
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

/// <summary>Where a Contacts or Calendars restore imports to: <paramref name="Id"/> null = each into the one it came from.</summary>
public sealed record RestoreTarget(string? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A snapshot in the restore's choice: "Today 06:53 · Pixel 8 · 22 files".</summary>
public sealed record RestoreChoice(MediaSnapshotDto Snapshot, DateTimeOffset Now)
{
    public string Label => $"{Format.Ago(Snapshot.Time, Now)} ({Snapshot.Time.ToLocalTime():yyyy-MM-dd HH:mm}) · {(Snapshot.Mine ? "this phone" : Snapshot.Device ?? "another device")} · " +
                           $"{Snapshot.Files} files · {Snapshot.ShortId}";
}
