using ASync.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ASync.App.ViewModels;

/// <summary>
/// Settings → <i>Folders on this phone</i>: whether ASync holds "All files access", and the system page to change it.
/// When it changes (the app comes back to the front), the folder cards look again whether to offer their real paths and an
/// open folder dialog follows (<see cref="LocalFolderViewModel.Refresh"/>). Only on platforms with
/// <see cref="IPlatformServices.Storage"/>.
/// </summary>
public sealed partial class StorageViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly IStorageAccess _storage;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public StorageViewModel(ShellViewModel shell, IStorageAccess storage)
    {
        _shell = shell;
        _storage = storage;
        storage.Changed += () =>
        {
            if (_context is null) Refresh();
            else _context.Post(_ => Refresh(), null);
        };
        Refresh();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ChangeText))]
    public partial bool HasAllFilesAccess { get; private set; }

    public string StatusText => HasAllFilesAccess
        ? "All files access is on: folders can be anywhere on this phone, also at the top of the storage, on an SD card or in Download."
        : "All files access is off: folders are chosen with Android's own picker, which refuses the top of the storage, Download and some other places.";

    public string ChangeText => HasAllFilesAccess ? "Change in system settings" : "Allow all files access";

    public static string UnreachableNote => PhoneStorage.UnreachableNote;

    public void Refresh()
    {
        HasAllFilesAccess = _storage.HasAllFilesAccess;
        _shell.Dashboard.RefreshStorage();
    }

    [RelayCommand]
    private void Change() => _storage.RequestAllFilesAccess();
}
