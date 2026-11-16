using System.Collections.ObjectModel;
using ASync.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ASync.App.ViewModels;

/// <summary>
/// Choosing where a folder lives on a phone (<see cref="IPlatformServices.Storage"/>). Without "All files access" it
/// first explains, in one short screen, why ASync asks for it, and offers the system's page for it or today's system
/// picker (a document tree) instead. With it, a folder picker over real paths: each volume's root (internal storage, SD
/// cards, USB drives), Download, anything but Android/data and Android/obb. Coming back from the system page with the
/// permission switches to the picker (<see cref="IStorageAccess.Changed"/>).
/// </summary>
public sealed partial class LocalFolderViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly IStorageAccess _storage;
    private readonly Func<string, Task<bool>> _apply;
    private readonly Func<Task> _systemPicker;
    private readonly string? _startAt;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    /// <param name="apply">Sets the chosen real path as the folder's local path; false when that failed (the dialog stays).</param>
    /// <param name="systemPicker">Today's system picker (ACTION_OPEN_DOCUMENT_TREE), the fallback without the permission.</param>
    /// <param name="startAt">The folder's current local path (a real path or a document tree), where the picker opens.</param>
    public LocalFolderViewModel(ShellViewModel shell, IStorageAccess storage, string folderName, Func<string, Task<bool>> apply, Func<Task> systemPicker, string? startAt)
    {
        _shell = shell;
        _storage = storage;
        _apply = apply;
        _systemPicker = systemPicker;
        _startAt = startAt;
        Title = $"Where \"{folderName}\" syncs on this {shell.Platform.DeviceNoun}";
        storage.Changed += OnStorageChanged;
        if (storage.HasAllFilesAccess) StartPicking();
        else IsExplaining = true;
    }

    public string Title { get; }

    public const string Explanation =
        "To sync any folder on this phone, including the top of the internal storage, an SD card or Download, ASync needs " +
        "\"All files access\". Android's own folder picker refuses those places to protect privacy. ASync uses the " +
        "permission only for the folders you choose here; it sends nothing anywhere else.";

    public const string FallbackNote = "Without it, the system's folder picker still works for folders below those places.";

    public static string UnreachableNote => PhoneStorage.UnreachableNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPicking))]
    public partial bool IsExplaining { get; private set; }

    public bool IsPicking => !IsExplaining;

    /// <summary>The system page was opened and the app came back without the permission.</summary>
    [ObservableProperty] public partial bool StillOff { get; private set; }

    private bool _askedSystem;

    // ── The picker ────────────────────────────────────────────────────────────

    public ObservableCollection<StorageVolumeInfo> Volumes { get; } = [];
    public ObservableCollection<string> Entries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Breadcrumb), nameof(CanGoUp))]
    [NotifyCanExecuteChangedFor(nameof(UpCommand), nameof(UseThisFolderCommand))]
    public partial string CurrentPath { get; private set; } = "";

    [ObservableProperty] public partial StorageVolumeInfo? CurrentVolume { get; private set; }

    public string Breadcrumb => PhoneStorage.Breadcrumb(CurrentPath, [.. Volumes]);

    public bool CanGoUp => CurrentVolume is { } v && PhoneStorage.Normalize(CurrentPath) != v.Path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty] public partial bool IsEmpty { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateFolderCommand))]
    public partial string NewFolderName { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseThisFolderCommand))]
    public partial bool Busy { get; private set; }

    private void OnStorageChanged()
    {
        if (_context is null) Refresh();
        else _context.Post(_ => Refresh(), null);
    }

    /// <summary>Back from the system settings: picks up the permission, or says it is still off.</summary>
    public void Refresh()
    {
        if (_storage.HasAllFilesAccess)
        {
            if (IsExplaining) StartPicking();
        }
        else if (IsExplaining && _askedSystem)
        {
            StillOff = true;
        }
        else if (!IsExplaining)
        {
            // Turned off while the picker was open.
            IsExplaining = true;
            StillOff = true;
        }
    }

    private void StartPicking()
    {
        IsExplaining = false;
        Volumes.Clear();
        foreach (var v in _storage.Volumes()) Volumes.Add(v);
        var volumes = Volumes.ToList();
        var start = _startAt is { Length: > 0 } s ? s.StartsWith('/') ? s : _storage.RealPathOf(s) : null;
        while (start is not null && (PhoneStorage.VolumeOf(start, volumes) is null || PhoneStorage.IsUnreachable(start, volumes) || !Directory.Exists(start)))
        {
            var parent = Path.GetDirectoryName(start);
            start = parent is { Length: > 1 } && PhoneStorage.VolumeOf(parent, volumes) is not null ? parent : null;
        }
        if (start is not null) Navigate(start);
        else if (volumes.Count > 0) Navigate(volumes[0].Path);
        else Error = "No storage is mounted on this phone right now.";
    }

    /// <summary>Shows a directory: its subdirectories, without hidden ones and without Android/data and Android/obb.</summary>
    public void Navigate(string path)
    {
        path = PhoneStorage.Normalize(path);
        var volumes = Volumes.ToList();
        if (PhoneStorage.Check(path, volumes) is { } invalid)
        {
            Error = invalid;
            return;
        }
        List<string> names;
        try
        {
            names = [.. Directory.EnumerateDirectories(path)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => n.Length > 0 && !n.StartsWith('.'))
                .Where(n => !PhoneStorage.IsUnreachable($"{path.TrimEnd('/')}/{n}", volumes))
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Error = _storage.HasAllFilesAccess ? $"Cannot open {PhoneStorage.Breadcrumb(path, volumes)}: {e.Message}" : "All files access was turned off.";
            return;
        }
        Error = null;
        CurrentPath = path;
        CurrentVolume = PhoneStorage.VolumeOf(path, volumes);
        Entries.Clear();
        foreach (var n in names) Entries.Add(n);
        IsEmpty = names.Count == 0;
    }

    [RelayCommand]
    private void SelectVolume(StorageVolumeInfo? volume)
    {
        if (volume is not null) Navigate(volume.Path);
    }

    [RelayCommand]
    private void Open(string? name)
    {
        if (name is { Length: > 0 }) Navigate($"{CurrentPath.TrimEnd('/')}/{name}");
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void Up()
    {
        if (Path.GetDirectoryName(CurrentPath) is { } parent) Navigate(parent);
    }

    private bool CanCreate() => NewFolderName.Trim() is { Length: > 0 } n && n.IndexOfAny(['/', '\0']) < 0 && n is not ("." or "..");

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void CreateFolder()
    {
        var name = NewFolderName.Trim();
        var path = $"{CurrentPath.TrimEnd('/')}/{name}";
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Error = $"Cannot create {name}: {e.Message}";
            return;
        }
        NewFolderName = "";
        Navigate(path);
    }

    private bool CanUse() => !Busy && CurrentPath.Length > 0;

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task UseThisFolder()
    {
        if (PhoneStorage.Check(CurrentPath, [.. Volumes]) is { } invalid)
        {
            Error = invalid;
            return;
        }
        Busy = true;
        try
        {
            if (await _apply(CurrentPath)) Close();
        }
        finally
        {
            Busy = false;
        }
    }

    // ── The explanation ───────────────────────────────────────────────────────

    [RelayCommand]
    private void AllowAccess()
    {
        _askedSystem = true;
        StillOff = false;
        _storage.RequestAllFilesAccess();
    }

    [RelayCommand]
    private async Task UseSystemPicker()
    {
        Close();
        await _systemPicker();
    }

    [RelayCommand]
    public void Cancel() => Close();

    private void Close()
    {
        _storage.Changed -= OnStorageChanged;
        if (_shell.FolderDialog == this) _shell.FolderDialog = null;
    }
}
