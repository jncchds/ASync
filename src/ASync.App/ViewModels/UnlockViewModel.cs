using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ASync.App.ViewModels;

/// <summary>
/// Unlocks an end-to-end folder on this device with its passphrase or recovery key. The secret is only passed to the
/// agent: it is never shown (a password box), logged or kept; the field is cleared as soon as it is sent.
/// </summary>
public sealed partial class UnlockViewModel(ShellViewModel shell, int folderId, string folderName) : ObservableObject
{
    public int FolderId { get; } = folderId;
    public string FolderName { get; } = folderName;

    public string Title => $"Unlock \"{FolderName}\"";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    public partial string Secret { get; set; } = "";

    /// <summary>The recovery key instead of the passphrase (only the label differs: the agent accepts either).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecretLabel))]
    public partial bool UseRecoveryKey { get; set; }

    public string SecretLabel => UseRecoveryKey ? "Recovery key" : "Passphrase";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    private bool CanUnlock() => !IsBusy && Secret.Length > 0;

    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private async Task Unlock()
    {
        var secret = Secret;
        Secret = "";
        IsBusy = true;
        Error = null;
        try
        {
            var ok = await shell.RunAsync((s, ct) => s.UnlockAsync(FolderId, secret, ct)!);
            if (ok) shell.Dialog = null;
            else if (shell.MessageIsError)
            {
                Error = shell.Message;
                shell.ShowMessage(null);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Secret = "";
        shell.Dialog = null;
    }
}
