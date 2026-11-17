using System.Collections.ObjectModel;
using ASync.Agent.Control;
using ASync.App.Services;
using ASync.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ASync.App.ViewModels;

/// <summary>An album of the phone's library in the Photos screen.</summary>
public sealed partial class AlbumChoice(string id, string name, int photos, int videos) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Counts { get; } = Format.MediaCounts(photos, videos);
    [ObservableProperty] public partial bool IsChecked { get; set; }
}

/// <summary>
/// A Photos folder on this phone (task 19): first, while ASync may not read the photos, why it asks and what it reads (originals as
/// they are, their location only with "media location", Android 14's "Select photos" giving some photos only); then what to back
/// up: all photos and videos, or chosen albums of the phone's own list, with or without videos and screenshots. Opened from a
/// Photos folder's card (<see cref="FolderViewModel"/>) and from New folder / Add to a folder (<see cref="AddFolderViewModel"/>),
/// each with its own <c>apply</c>.
/// </summary>
public sealed partial class PhotosViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly IPhotoAccess? _access;
    private readonly Func<MediaSelectionDto, Task<bool>> _apply;
    private readonly DateTime? _since;
    private readonly HashSet<string> _initial;
    private readonly Dictionary<string, string> _initialNames;

    public PhotosViewModel(ShellViewModel shell, string folderName, MediaSelectionDto? current, Func<MediaSelectionDto, Task<bool>> apply)
    {
        _shell = shell;
        _access = shell.Platform.Photos;
        _apply = apply;
        FolderName = folderName;
        var selection = current ?? MediaSelectionDto.Everything;
        BackUpAll = selection.All;
        IncludeVideos = selection.IncludeVideos;
        IncludeScreenshots = selection.IncludeScreenshots;
        _since = selection.Since;
        _initial = [.. (selection.Albums ?? []).Select(a => a.Id)];
        _initialNames = (selection.Albums ?? []).DistinctBy(a => a.Id).ToDictionary(a => a.Id, a => a.Name);
        if (_access is not null) _access.Changed += OnAccessChanged;
        RefreshAccess();
    }

    public string FolderName { get; }
    public string Title => $"Photos for \"{FolderName}\"";

    // ── Permission ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsPermission), nameof(IsPartial), nameof(CanChoose), nameof(AccessText))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial MediaAccess Access { get; private set; } = MediaAccess.None;

    [ObservableProperty] public partial bool HasLocation { get; private set; }

    public bool NeedsPermission => Access is MediaAccess.None;
    public bool IsPartial => Access is MediaAccess.Partial;
    public bool CanChoose => Access is MediaAccess.Full or MediaAccess.Partial;
    public bool NoLocation => CanChoose && !HasLocation;

    public string AccessText => Access switch
    {
        MediaAccess.Full => HasLocation ? "ASync may read all photos and videos, with their location." : "ASync may read all photos and videos.",
        MediaAccess.Partial => "ASync may read only the photos and videos chosen for it.",
        MediaAccess.Unsupported => $"This {_shell.Platform.DeviceNoun} has no photo library ASync can read.",
        _ => "ASync may not read photos and videos yet.",
    };

    public string Explanation =>
        "To back up your photos and videos ASync needs to read them. It reads each original as it is, never changes or deletes " +
        "anything, and sends it only to your own server's storage (encrypted there if the folder is end-to-end). " +
        "Allow the location in photos too, or Android removes it from what ASync reads.";

    public string PartialText => _access?.OffersPartial == true
        ? "With \"Select photos\" Android lets ASync see only the photos you pick, and only those are backed up; new photos are not " +
          "seen until you pick them too. Choose \"Allow all\" in the settings to back up everything."
        : "";

    private void OnAccessChanged()
    {
        void Apply()
        {
            RefreshAccess();
            _ = LoadAlbumsAsync();
        }
        if (_context is null) Apply();
        else _context.Post(_ => Apply(), null);
    }

    private void RefreshAccess()
    {
        Access = _access?.Access ?? MediaAccess.Unsupported;
        HasLocation = _access?.HasLocation ?? false;
        OnPropertyChanged(nameof(NoLocation));
        OnPropertyChanged(nameof(PartialText));
    }

    [RelayCommand]
    private void Allow() => _access?.Request();

    [RelayCommand]
    private void OpenSettings() => _access?.OpenSettings();

    // ── What to back up ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChooseAlbums))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool BackUpAll { get; set; }

    public bool ChooseAlbums
    {
        get => !BackUpAll;
        set { if (value) BackUpAll = false; }
    }

    [ObservableProperty] public partial bool IncludeVideos { get; set; }
    [ObservableProperty] public partial bool IncludeScreenshots { get; set; }

    public ObservableCollection<AlbumChoice> Albums { get; } = [];

    [ObservableProperty] public partial string? LibraryText { get; private set; }
    [ObservableProperty] public partial bool Loading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>The phone's albums (through the agent's <c>media-library</c>), the chosen ones ticked.</summary>
    public async Task LoadAlbumsAsync()
    {
        if (!CanChoose || _shell.Session is not { } session) return;
        Loading = true;
        try
        {
            var library = await session.MediaLibraryAsync(CancellationToken.None);
            var checkedNow = Albums.Count > 0 ? Albums.Where(a => a.IsChecked).Select(a => a.Id).ToHashSet() : _initial;
            Albums.Clear();
            foreach (var a in library.Albums)
            {
                var choice = new AlbumChoice(a.Id, a.Name, a.Photos, a.Videos) { IsChecked = checkedNow.Contains(a.Id) };
                choice.PropertyChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
                Albums.Add(choice);
            }
            LibraryText = library.Problem ?? $"{Format.MediaCounts(library.Photos, library.Videos)} in {library.Albums.Count} album{(library.Albums.Count == 1 ? "" : "s")}.";
            Error = null;
        }
        catch (Exception e) when (e is ControlException or IOException or InvalidDataException or ObjectDisposedException)
        {
            Error = e.Message;
        }
        finally
        {
            Loading = false;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    public MediaSelectionDto Selection() => new(BackUpAll,
        BackUpAll ? [] : [.. Albums.Where(a => a.IsChecked).Select(a => new MediaAlbumRef(a.Id, a.Name))
            .Concat(_initial.Where(id => Albums.All(a => a.Id != id)).Select(id => new MediaAlbumRef(id, _initialNames.GetValueOrDefault(id, ""))))],
        _since, IncludeVideos, IncludeScreenshots);

    private bool CanSave() => CanChoose && !Working && (BackUpAll || Albums.Any(a => a.IsChecked));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool Working { get; private set; }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        Working = true;
        try
        {
            if (await _apply(Selection())) Close();
        }
        finally { Working = false; }
    }

    [RelayCommand]
    public void Close()
    {
        if (_access is not null) _access.Changed -= OnAccessChanged;
        if (_shell.PhotosDialog == this) _shell.PhotosDialog = null;
    }
}
