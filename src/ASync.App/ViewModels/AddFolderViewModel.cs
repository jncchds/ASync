using System.Collections.ObjectModel;
using System.Text;
using ASync.Agent.Control;
using ASync.App.Services;
using ASync.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ASync.App.ViewModels;

/// <summary>A storage back-end in the New folder form (names and types only: the device never sees settings).</summary>
public sealed record StorageChoice(int Id, string Name, StorageBackendType Type, bool Shared)
{
    public string Label => $"{Name} ({Format.StorageType(Type)}{(Shared ? ", shared" : "")})";
}

/// <summary>A sync interval the form offers.</summary>
public sealed record IntervalChoice(int Minutes, string Label);

/// <summary>One of the owner's folders this device could join.</summary>
public sealed record JoinableFolder(DeviceJoinableFolderDto Folder)
{
    public int Id => Folder.FolderId;
    public string Name => Folder.Name;

    public string Description
    {
        get
        {
            var what = Format.Mode(Folder.Mode) +(Folder.Encryption == FolderEncryption.EndToEnd ? ", end-to-end" : "");
            var where = $"{Folder.StorageBackendName}: {Folder.RootPath}";
            var devices = Folder.Devices.Length == 0 ? "no devices yet" : string.Join(", ", Folder.Devices);
            return $"{what} · {where} · {devices}";
        }
    }
}

/// <summary>
/// Adding a folder from this device (task 18): <b>New folder</b> (name, mode, Mirror direction, encryption, storage, the
/// directory on the storage, interval, and where it lives here) or <b>Add this device to a folder</b> (one of the owner's
/// folders, then where it lives here). The agent asks the server (<c>folder-options</c>, <c>create-folder</c>,
/// <c>join-folder</c>), which applies the web UI's rules and the device's "May add folders" setting. Local folders are
/// chosen with the platform's pickers: on a phone the All files access picker (<see cref="LocalFolderViewModel"/>) or the
/// system's, elsewhere the native folder picker or a typed path. A new end-to-end Backup folder goes straight into the
/// encryption setup; a new standard Backup folder shows its recovery key once.
/// </summary>
public sealed partial class AddFolderViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _rootPathEdited;
    private bool _settingRootPath;

    public AddFolderViewModel(ShellViewModel shell, bool join)
    {
        _shell = shell;
        IsJoining = join;
        SelectedInterval = Intervals[2];
    }

    public static IReadOnlyList<IntervalChoice> Intervals { get; } =
    [
        new(5, "Every 5 minutes"), new(15, "Every 15 minutes"), new(60, "Every hour"), new(360, "Every 6 hours"), new(1440, "Once a day"),
    ];

    public string DeviceNoun => _shell.Platform.DeviceNoun;

    /// <summary>A phone: local folders come from the phone's pickers and are not typed.</summary>
    public bool IsPhone => _shell.Platform.Storage is not null;

    public bool CanTypePath => !IsPhone;

    // ── Which form ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreating), nameof(Title), nameof(ShowDirection), nameof(ShowJoinForm), nameof(ShowNewForm), nameof(LocalTitle), nameof(ShowLocal), nameof(SubmitText),
        nameof(ForPhotos), nameof(ForFiles), nameof(ForCollections), nameof(CollectionsText), nameof(ChooseCollectionsText))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial bool IsJoining { get; set; }

    public bool IsCreating
    {
        get => !IsJoining;
        set { if (value) IsJoining = false; }
    }

    partial void OnIsJoiningChanged(bool value) => Error = null;

    public string Title => IsJoining ? $"Add this {DeviceNoun} to a folder" : "New folder";

    // ── Loading what the device may do ────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNewForm), nameof(ShowJoinForm), nameof(IsRefused), nameof(ShowLocal))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial bool Loaded { get; private set; }

    /// <summary>Why this device may not add folders (its "May add folders" is off), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRefused), nameof(ShowNewForm), nameof(ShowJoinForm), nameof(ShowLocal))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string? Refusal { get; private set; }

    public bool IsRefused => Refusal is not null;

    public bool ShowNewForm => Loaded && !IsRefused && IsCreating && RecoveryKey is null;
    public bool ShowJoinForm => Loaded && !IsRefused && IsJoining;

    /// <summary>The local folder card and the buttons: with either form.</summary>
    public bool ShowLocal => ShowNewForm || ShowJoinForm;

    public string JoinTabText => $"Add this {DeviceNoun} to a folder";

    public string UploadText => $"Upload: this {DeviceNoun} is the original";
    public string DownloadText => $"Download: this {DeviceNoun} gets a copy";

    /// <summary>The tabs: "new" or "join".</summary>
    [RelayCommand]
    private void Switch(string? kind)
    {
        IsJoining = kind == "join";
        Error = null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand), nameof(ReloadCommand))]
    public partial bool IsBusy { get; private set; }

    public ObservableCollection<StorageChoice> Storages { get; } = [];
    public ObservableCollection<JoinableFolder> JoinableFolders { get; } = [];

    [ObservableProperty] public partial bool HasNoStorage { get; private set; }
    [ObservableProperty] public partial bool HasNothingToJoin { get; private set; }

    /// <summary>Asks the agent (and so the server) what this device may do. Errors (offline, an old agent) stay on the page.</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    public async Task Reload()
    {
        if (_shell.Session is not { } session)
        {
            Error = "The ASync agent is not running.";
            return;
        }
        IsBusy = true;
        Error = null;
        try
        {
            var options = await session.FolderOptionsAsync(CancellationToken.None);
            Apply(options);
        }
        catch (Exception e) when (e is ControlException or IOException or InvalidDataException or ObjectDisposedException)
        {
            Error = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool NotBusy() => !IsBusy;

    internal void Apply(DeviceFolderOptionsDto options)
    {
        Refusal = options.MayAddFolders ? null : options.Refusal ?? FolderOptionsRefusal;
        var selectedStorage = SelectedStorage?.Id;
        Storages.Clear();
        foreach (var s in options.Storages) Storages.Add(new StorageChoice(s.Id, s.Name, s.Type, s.Shared));
        SelectedStorage = Storages.FirstOrDefault(s => s.Id == selectedStorage) ?? Storages.FirstOrDefault();
        var selectedFolder = SelectedFolder?.Id;
        JoinableFolders.Clear();
        // Photos folders only for a device with a photo library (the server refuses the others).
        foreach (var f in options.Folders.Where(f => (f.Kind != FolderKind.Photos || OffersPhotos) && (f.Kind is not (FolderKind.Contacts or FolderKind.Calendars) || OffersCollections))
                     .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            JoinableFolders.Add(new JoinableFolder(f));
        SelectedFolder = JoinableFolders.FirstOrDefault(f => f.Id == selectedFolder);
        HasNoStorage = options.MayAddFolders && Storages.Count == 0;
        HasNothingToJoin = options.MayAddFolders && JoinableFolders.Count == 0;
        Loaded = true;
    }

    public const string FolderOptionsRefusal = "Adding folders from this device is turned off. Allowed in the web UI under Devices.";

    // ── New folder ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalTitle))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string Name { get; set; } = "";

    partial void OnNameChanged(string value)
    {
        if (_rootPathEdited) return;
        _settingRootPath = true;
        RootPath = DefaultRootPath(value);
        _settingRootPath = false;
    }

    /// <summary>The directory on the storage. Follows the name until it is edited.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string RootPath { get; set; } = "";

    partial void OnRootPathChanged(string value)
    {
        if (!_settingRootPath) _rootPathEdited = value.Length > 0;
    }

    /// <summary>"Holiday photos" → "holiday-photos": lower case, letters, digits, dots, dashes and underscores.</summary>
    public static string DefaultRootPath(string name)
    {
        var b = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '_' or '-') b.Append(c);
            else if (b.Length > 0 && b[^1] != '-') b.Append('-');
        }
        var path = b.ToString().Trim('-', '.');
        return path.Length > 60 ? path[..60].TrimEnd('-', '.') : path;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBackup), nameof(IsMirror), nameof(IsTwoWay), nameof(ShowDirection), nameof(CanEncrypt), nameof(ModeHint))]
    public partial SyncMode Mode { get; set; } = SyncMode.TwoWay;

    partial void OnModeChanged(SyncMode value)
    {
        if (value != SyncMode.Backup) EndToEnd = false;
    }

    /// <summary>The device has a photo library (phones): New folder offers "Photos" too (task 19).</summary>
    public bool OffersPhotos => _shell.Platform.Photos is not null;

    /// <summary>A new Photos folder: a Backup of the phone's photo library instead of a directory.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBackup), nameof(IsMirror), nameof(IsTwoWay), nameof(IsPhotosMode), nameof(ModeHint), nameof(ForPhotos), nameof(ForFiles), nameof(LocalTitle),
        nameof(ForCollections),
        nameof(ShowDirection))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial bool IsPhotosKind { get; set; }

    partial void OnIsPhotosKindChanged(bool value)
    {
        if (!value) return;
        CollectionKind = null;
        Mode = SyncMode.Backup;
        if (Name.Trim().Length == 0 || Name is "Contacts" or "Calendars") Name = "Photos";
    }

    /// <summary>The form is about a Photos folder (a new one, or the one to join): a media selection instead of a local folder.</summary>
    public bool ForPhotos => IsJoining ? SelectedFolder?.Folder.Kind == FolderKind.Photos : IsPhotosKind;
    public bool ForFiles => !ForPhotos && !ForCollections;

    // ── Contacts and Calendars folders (task 20) ──────────────────────────────

    /// <summary>The device has contacts and calendars (phones): New folder offers them too.</summary>
    public bool OffersCollections => _shell.Platform.Contacts is not null || _shell.Platform.Calendars is not null;
    public bool OffersContacts => _shell.Platform.Contacts is not null;
    public bool OffersCalendars => _shell.Platform.Calendars is not null;

    /// <summary>A new Contacts or Calendars folder (null: neither).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBackup), nameof(IsMirror), nameof(IsTwoWay), nameof(IsPhotosMode), nameof(IsContactsMode), nameof(IsCalendarsMode), nameof(ModeHint),
        nameof(ForPhotos), nameof(ForFiles), nameof(ForCollections), nameof(LocalTitle), nameof(ShowDirection), nameof(CollectionsText), nameof(ChooseCollectionsText))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial FolderKind? CollectionKind { get; set; }

    partial void OnCollectionKindChanged(FolderKind? value)
    {
        if (value is not { } kind) return;
        IsPhotosKind = false;
        Mode = SyncMode.Backup;
        Collections = CollectionSelectionDto.Everything;
        if (Name.Trim().Length == 0 || Name is "Photos" or "Contacts" or "Calendars") Name = kind == FolderKind.Contacts ? "Contacts" : "Calendars";
    }

    /// <summary>The kind of the Contacts or Calendars folder the form is about (a new one, or the one to join); null otherwise.</summary>
    public FolderKind? FormCollectionKind => IsJoining
        ? SelectedFolder?.Folder.Kind is FolderKind.Contacts or FolderKind.Calendars ? SelectedFolder.Folder.Kind : null
        : CollectionKind;

    public bool ForCollections => FormCollectionKind is not null;

    public bool IsContactsMode { get => CollectionKind == FolderKind.Contacts; set { if (value) CollectionKind = FolderKind.Contacts; } }
    public bool IsCalendarsMode { get => CollectionKind == FolderKind.Calendars; set { if (value) CollectionKind = FolderKind.Calendars; } }

    /// <summary>What the Contacts or Calendars folder backs up here; everything until chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CollectionsText))]
    public partial CollectionSelectionDto Collections { get; set; } = CollectionSelectionDto.Everything;

    public string CollectionsText => FormCollectionKind is { } kind ? Collections.Describe(kind) : "";

    public string ChooseCollectionsText => FormCollectionKind == FolderKind.Calendars ? "Choose calendars…" : "Choose contacts…";

    [RelayCommand]
    private async Task ChooseCollections()
    {
        if (FormCollectionKind is not { } kind) return;
        var dialog = new CollectionsViewModel(_shell, kind, FolderLabel.Trim('"'), Collections, selection =>
        {
            Collections = selection;
            return Task.FromResult(true);
        });
        _shell.CollectionsDialog = dialog;
        await dialog.LoadAsync();
    }

    /// <summary>What the Photos folder backs up here; everything until chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MediaText))]
    public partial MediaSelectionDto Media { get; set; } = MediaSelectionDto.Everything;

    public string MediaText => Media.Describe();

    [RelayCommand]
    private async Task ChoosePhotos()
    {
        var dialog = new PhotosViewModel(_shell, FolderLabel.Trim('"'), Media, selection =>
        {
            Media = selection;
            return Task.FromResult(true);
        });
        _shell.PhotosDialog = dialog;
        await dialog.LoadAlbumsAsync();
    }

    public bool IsBackup { get => Mode == SyncMode.Backup && !IsPhotosKind && CollectionKind is null; set { if (value) { IsPhotosKind = false; CollectionKind = null; Mode = SyncMode.Backup; } } }
    public bool IsPhotosMode { get => IsPhotosKind; set { if (value) IsPhotosKind = true; } }
    public bool IsMirror { get => Mode == SyncMode.Mirror; set { if (value) { IsPhotosKind = false; CollectionKind = null; Mode = SyncMode.Mirror; } } }
    public bool IsTwoWay { get => Mode == SyncMode.TwoWay; set { if (value) { IsPhotosKind = false; CollectionKind = null; Mode = SyncMode.TwoWay; } } }

    public string ModeHint => IsPhotosKind
        ? $"Snapshots of this {DeviceNoun}'s photos and videos (all of them, or chosen albums), originals as they are; several phones can share the folder."
        : CollectionKind == FolderKind.Contacts
        ? $"Snapshots of this {DeviceNoun}'s contacts (all accounts, or chosen ones), one .vcf file each with its photo; a restore imports them."
        : CollectionKind == FolderKind.Calendars
        ? $"Snapshots of this {DeviceNoun}'s calendars (all, or chosen ones), one .ics file per event; a restore imports them."
        : Mode switch
    {
        SyncMode.Backup => $"Snapshots of this {DeviceNoun}'s folder, kept by the folder's retention; nothing comes back unless you restore.",
        SyncMode.Mirror => "One side is a copy of the other: choose which.",
        _ => "Changes on any device are copied to the others; edits on both sides keep both versions.",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpload), nameof(IsDownload))]
    public partial MirrorDirection Direction { get; set; } = MirrorDirection.Upload;

    public bool IsUpload { get => Direction == MirrorDirection.Upload; set { if (value) Direction = MirrorDirection.Upload; } }
    public bool IsDownload { get => Direction == MirrorDirection.Download; set { if (value) Direction = MirrorDirection.Download; } }

    /// <summary>Mirror folders need a direction (a new one, or the folder to join).</summary>
    public bool ShowDirection => IsJoining ? SelectedFolder?.Folder.Mode == SyncMode.Mirror : Mode == SyncMode.Mirror && !IsPhotosKind && CollectionKind is null;

    /// <summary>End-to-end encryption is for Backup folders for now (the server's rule).</summary>
    public bool CanEncrypt => Mode == SyncMode.Backup;  // Photos folders too: they are Backup folders

    [ObservableProperty] public partial bool EndToEnd { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial StorageChoice? SelectedStorage { get; set; }

    [ObservableProperty] public partial IntervalChoice SelectedInterval { get; set; }

    // ── Joining ───────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDirection), nameof(LocalTitle), nameof(ForPhotos), nameof(ForFiles), nameof(ForCollections), nameof(CollectionsText),
        nameof(ChooseCollectionsText))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial JoinableFolder? SelectedFolder { get; set; }

    // ── Where it lives on this device ─────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalDisplay), nameof(HasLocalPath))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string LocalPath { get; set; } = "";

    public bool HasLocalPath => LocalPath.Trim().Length > 0;

    public string LocalDisplay => HasLocalPath ? _shell.Platform.DisplayPath(LocalPath.Trim()) : "Not chosen yet";

    private string FolderLabel => IsJoining ? SelectedFolder?.Name ?? "the folder" : Name.Trim() is { Length: > 0 } n ? n : "the new folder";

    public string LocalTitle => ForPhotos || ForCollections ? $"What {Quote(FolderLabel)} backs up on this {DeviceNoun}" : $"Where {Quote(FolderLabel)} lives on this {DeviceNoun}";

    private static string Quote(string name) => name.StartsWith("the ", StringComparison.Ordinal) ? name : $"\"{name}\"";

    /// <summary>
    /// The platform's picker: on a phone the All files access picker (or first its explanation, which offers the system
    /// picker), elsewhere the native folder picker.
    /// </summary>
    [RelayCommand]
    private async Task ChooseLocal()
    {
        var title = $"Where {Quote(FolderLabel)} lives on this {DeviceNoun}";
        if (_shell.Platform.Storage is { } storage)
        {
            _shell.FolderDialog = new LocalFolderViewModel(_shell, storage, FolderLabel, path =>
            {
                LocalPath = path;
                return Task.FromResult(true);
            }, ChooseSystemLocalAsync, HasLocalPath ? LocalPath : null);
            return;
        }
        if (await _shell.Platform.PickFolderAsync(title, HasLocalPath ? LocalPath : null) is { } picked) LocalPath = picked;
    }

    private async Task ChooseSystemLocalAsync()
    {
        var previous = LocalPath;
        if (await _shell.Platform.PickFolderAsync(LocalTitle, HasLocalPath ? LocalPath : null) is not { } picked) return;
        LocalPath = picked;
        // A document tree this form took and no longer uses gives its permission back.
        if (previous.StartsWith("content://", StringComparison.Ordinal) && previous != picked) _shell.Platform.Storage?.ReleaseTree(previous);
    }

    // ── Submitting ────────────────────────────────────────────────────────────

    private bool CanSubmit() => !IsBusy && Loaded && !IsRefused && (HasLocalPath || ForPhotos || ForCollections) && RecoveryKey is null
                                && (IsJoining ? SelectedFolder is not null : Name.Trim().Length > 0 && RootPath.Trim().Length > 0 && SelectedStorage is not null);

    public string SubmitText => IsJoining ? "Add this " + DeviceNoun : "Create folder";

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task Submit()
    {
        if (_shell.Session is not { } session)
        {
            Error = "The ASync agent is not running.";
            return;
        }
        IsBusy = true;
        Error = null;
        try
        {
            if (IsJoining) await JoinAsync(session);
            else await CreateAsync(session);
        }
        catch (Exception e) when (e is ControlException or IOException or InvalidDataException or ObjectDisposedException)
        {
            Error = e.Message;
            // Turned off in the meantime: show it as the page's state.
            if (e.Message.Contains("Allowed in the web UI under Devices", StringComparison.Ordinal)) Refusal = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CreateAsync(IAgentSession session)
    {
        var name = Name.Trim();
        var encryption = Mode == SyncMode.Backup && EndToEnd ? FolderEncryption.EndToEnd : FolderEncryption.Transport;
        var photos = IsPhotosKind;
        var pim = CollectionKind;
        var noPath = photos || pim is not null;
        var request = new DeviceCreateFolderRequest(
            new SaveFolderRequest(name, noPath ? SyncMode.Backup : Mode, encryption, photos ? FolderKind.Photos : pim ?? FolderKind.Files, SelectedStorage!.Id,
                RootPath.Trim(), SelectedInterval.Minutes),
            noPath ? "" : LocalPath.Trim(), !noPath && Mode == SyncMode.Mirror ? Direction : null, photos ? Media : null, pim is not null ? Collections : null);
        var added = await session.CreateFolderAsync(request, CancellationToken.None);
        if (added.RecoveryKey is { } key)
        {
            // A standard Backup folder: the server made its repository and this is its recovery key, shown once.
            CreatedName = name;
            RecoveryKey = key;
            _shell.ShowMessage(added.Message);
            return;
        }
        Close();
        _shell.ShowMessage(added.Message);
        if (encryption == FolderEncryption.EndToEnd) _shell.Dialog = new UnlockViewModel(_shell, added.FolderId, name, setup: true);
    }

    private async Task JoinAsync(IAgentSession session)
    {
        var folder = SelectedFolder!.Folder;
        var added = folder.Kind == FolderKind.Photos
            ? await session.JoinPhotosAsync(folder.FolderId, Media, CancellationToken.None)
            : folder.Kind is FolderKind.Contacts or FolderKind.Calendars
            ? await session.JoinCollectionsAsync(folder.FolderId, Collections, CancellationToken.None)
            : await session.JoinFolderAsync(folder.FolderId, LocalPath.Trim(), folder.Mode == SyncMode.Mirror ? Direction : null, CancellationToken.None);
        Close();
        _shell.ShowMessage(added.Message);
        if (folder.Encryption == FolderEncryption.EndToEnd)
            _shell.Dialog = new UnlockViewModel(_shell, added.FolderId, folder.Name, setup: !folder.RepositoryReady);
    }

    /// <summary>A new standard Backup folder's recovery key, shown until the page closes and never kept.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecoveryKey), nameof(ShowNewForm), nameof(ShowLocal))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string? RecoveryKey { get; private set; }

    public bool HasRecoveryKey => RecoveryKey is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreatedText))]
    public partial string CreatedName { get; private set; } = "";

    public string CreatedText => $"\"{CreatedName}\" was created";

    [RelayCommand]
    public void Close()
    {
        RecoveryKey = null;
        if (_shell.AddFolderPage == this) _shell.AddFolderPage = null;
    }
}
