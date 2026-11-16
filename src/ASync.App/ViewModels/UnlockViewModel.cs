using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ASync.App.ViewModels;

/// <summary>
/// Unlocks an end-to-end folder on this device with its passphrase or recovery key, or (<paramref name="setup"/>) sets up a
/// new one: a passphrase typed twice, then the folder's recovery key, shown once. The secrets are only passed to the agent:
/// never shown (password boxes), logged or kept; the fields are cleared as soon as they are sent. The recovery key is shown
/// until the dialog closes and is not kept either.
/// </summary>
public sealed partial class UnlockViewModel(ShellViewModel shell, int folderId, string folderName, bool setup = false) : ObservableObject
{
    public int FolderId { get; } = folderId;
    public string FolderName { get; } = folderName;

    /// <summary>Setting up a new end-to-end folder (not unlocking one).</summary>
    public bool IsSetup { get; } = setup;
    public bool IsUnlock => !IsSetup;

    public string Title => IsSetup ? $"Set up encryption for \"{FolderName}\"" : $"Unlock \"{FolderName}\"";

    public string Intro => IsSetup
        ? "This folder is end-to-end encrypted and not set up yet. Choose its passphrase: files are encrypted on your devices with it, " +
          "and nobody, not even the server's admin, can read the backups without it. Other devices unlock the folder with the same passphrase."
        : "Its files are encrypted on this device before they leave it. Enter the folder's passphrase or its recovery key; it is only passed " +
          "to the agent, which keeps the unlocked key so backups run unattended.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand), nameof(SetUpCommand))]
    public partial string Secret { get; set; } = "";

    /// <summary>Setup: the passphrase once more.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetUpCommand))]
    public partial string Confirm { get; set; } = "";

    /// <summary>The recovery key instead of the passphrase (only the label differs: the agent accepts either).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecretLabel))]
    public partial bool UseRecoveryKey { get; set; }

    public string SecretLabel => UseRecoveryKey ? "Recovery key" : "Passphrase";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand), nameof(SetUpCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>Setup done: the recovery key to write down (then only <see cref="DoneCommand"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecoveryKey), nameof(IsEntering))]
    public partial string? RecoveryKey { get; private set; }

    public bool HasRecoveryKey => RecoveryKey is not null;

    /// <summary>The form is showing (not the recovery key).</summary>
    public bool IsEntering => RecoveryKey is null;

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
            else TakeError();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSetUp() => !IsBusy && Secret.Length > 0 && Confirm.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSetUp))]
    private async Task SetUp()
    {
        var passphrase = Secret;
        var confirm = Confirm;
        Secret = "";
        Confirm = "";
        Error = null;
        if (passphrase != confirm)
        {
            Error = "The two passphrases differ. Type it again.";
            return;
        }
        if (passphrase.Length < 8)
        {
            Error = "Choose a passphrase of at least 8 characters.";
            return;
        }
        IsBusy = true;
        try
        {
            string? key = null;
            var ok = await shell.RunAsync(async (s, ct) =>
            {
                key = await s.InitEncryptionAsync(FolderId, passphrase, ct);
                return null;
            });
            if (ok && key is not null) RecoveryKey = key;
            else TakeError();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void TakeError()
    {
        if (!shell.MessageIsError) return;
        Error = shell.Message;
        shell.ShowMessage(null);
    }

    /// <summary>The recovery key was written down: close (it is not kept anywhere).</summary>
    [RelayCommand]
    private void Done()
    {
        RecoveryKey = null;
        shell.Dialog = null;
        shell.ShowMessage($"\"{FolderName}\" is set up and backs up from this {shell.Platform.DeviceNoun}.");
    }

    [RelayCommand]
    private void Cancel()
    {
        Secret = "";
        Confirm = "";
        if (RecoveryKey is not null) Done();
        else shell.Dialog = null;
    }
}
