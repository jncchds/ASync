using System.Collections.ObjectModel;
using ASync.Agent.Control;
using ASync.App.Services;
using ASync.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ASync.App.ViewModels;

/// <summary>A contact account or calendar of the phone in the Contacts / Calendars screen.</summary>
public sealed partial class CollectionChoice(CollectionDto c, FolderKind kind) : ObservableObject
{
    public string Id { get; } = c.Id;
    public string Name { get; } = c.Name;
    /// <summary>"Google · 300 contacts", "alice@gmail.com · 120 events".</summary>
    public string Details { get; } = string.Join(" · ", new[] { c.Account, Format.Count(c.Count, kind == FolderKind.Contacts ? "contact" : "event") }
        .Where(s => s is { Length: > 0 }));
    public string Color { get; } = c.Color ?? "#8a6d9a";
    public bool HasColor { get; } = c.Color is not null;
    [ObservableProperty] public partial bool IsChecked { get; set; }
}

/// <summary>How far back a Calendars folder keeps events.</summary>
public sealed record PastChoice(int? Days, string Label)
{
    public static readonly PastChoice[] All =
    [
        new(365, "The last year"), new(730, "The last 2 years"), new(1825, "The last 5 years"), new(null, "Everything"),
    ];

    public override string ToString() => Label;
}

/// <summary>
/// A Contacts or Calendars folder on this phone (task 20), like <see cref="PhotosViewModel"/>: first, while ASync may not read them,
/// why it asks and what it reads (contacts with their photos / events with their reminders and guests, never changed or deleted, only to
/// the own server's storage; adding is asked only for a restore); then what to back up: everything, or chosen accounts / calendars of
/// the phone's own list, and for calendars how far back. Opened from a folder's card and from New folder / Add to a folder.
/// </summary>
public sealed partial class CollectionsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly ICollectionAccess? _access;
    private readonly Func<CollectionSelectionDto, Task<bool>> _apply;
    private readonly HashSet<string> _initial;
    private readonly Dictionary<string, string> _initialNames;

    public CollectionsViewModel(ShellViewModel shell, FolderKind kind, string folderName, CollectionSelectionDto? current, Func<CollectionSelectionDto, Task<bool>> apply)
    {
        _shell = shell;
        Kind = kind;
        _access = shell.Platform.CollectionAccess(kind);
        _apply = apply;
        FolderName = folderName;
        var selection = current ?? CollectionSelectionDto.Everything;
        BackUpAll = selection.All;
        SelectedPast = PastChoice.All.FirstOrDefault(p => p.Days == selection.PastDays) ?? new PastChoice(selection.PastDays, CollectionSelectionDto.DescribePast(selection.PastDays));
        if (!PastChoices.Contains(SelectedPast)) PastChoices.Insert(0, SelectedPast);
        _initial = [.. (selection.Collections ?? []).Select(c => c.Id)];
        _initialNames = (selection.Collections ?? []).DistinctBy(c => c.Id).ToDictionary(c => c.Id, c => c.Name);
        if (_access is not null) _access.Changed += OnAccessChanged;
        RefreshAccess();
    }

    public FolderKind Kind { get; }
    public bool IsCalendars => Kind == FolderKind.Calendars;
    public string FolderName { get; }
    public string Noun => CollectionTexts.Noun(Kind);
    public string Title => IsCalendars ? $"Calendars for \"{FolderName}\"" : $"Contacts for \"{FolderName}\"";

    // ── Permission ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsPermission), nameof(CanChoose), nameof(AccessText))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial MediaAccess Access { get; private set; } = MediaAccess.None;

    public bool NeedsPermission => Access is MediaAccess.None;
    public bool CanChoose => Access is MediaAccess.Full or MediaAccess.Partial;

    public string AccessText => Access switch
    {
        MediaAccess.Partial when _access?.PartialText is { } partial => partial,
        MediaAccess.Full or MediaAccess.Partial => IsCalendars ? "ASync may read the calendars on this phone." : "ASync may read the contacts on this phone.",
        MediaAccess.Unsupported => $"This {_shell.Platform.DeviceNoun} has no {Noun} ASync can read.",
        _ => $"ASync may not read the {Noun} yet.",
    };

    public string PermissionQuestion => IsCalendars ? "Allow ASync to read your calendars?" : "Allow ASync to read your contacts?";

    public string Explanation => IsCalendars
        ? "To back up your calendars ASync needs to read them: every event with its repetitions, changes, reminders and guests. It never " +
          "changes or deletes anything, and sends them only to your own server's storage (encrypted there if the folder is end-to-end). " +
          "ASync asks to add events only when you restore a backup."
        : "To back up your contacts ASync needs to read them: names, numbers, addresses, photos and the rest, of the accounts you choose. " +
          "It never changes or deletes anything, and sends them only to your own server's storage (encrypted there if the folder is " +
          "end-to-end). ASync asks to add contacts only when you restore a backup.";

    public string AllowText => IsCalendars ? "Allow access to calendars" : "Allow access to contacts";

    /// <summary>The platform's own words below the explanation (iOS: "Allow Full Access", notes not backed up); null for none.</summary>
    public string? PlatformNote => _access?.Note;

    public bool HasPlatformNote => PlatformNote is not null;

    private void OnAccessChanged()
    {
        void Apply()
        {
            RefreshAccess();
            _ = LoadAsync();
        }
        if (_context is null) Apply();
        else _context.Post(_ => Apply(), null);
    }

    private void RefreshAccess() => Access = _access?.Access ?? MediaAccess.Unsupported;

    [RelayCommand]
    private void Allow() => _access?.RequestRead();

    [RelayCommand]
    private void OpenSettings() => _access?.OpenSettings();

    // ── What to back up ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChooseSome))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool BackUpAll { get; set; }

    public bool ChooseSome
    {
        get => !BackUpAll;
        set { if (value) BackUpAll = false; }
    }

    public string AllText => IsCalendars ? "All calendars" : "All contacts";
    public string SomeText => IsCalendars ? "Chosen calendars" : "Chosen accounts";

    public ObservableCollection<CollectionChoice> Choices { get; } = [];

    public ObservableCollection<PastChoice> PastChoices { get; } = [.. PastChoice.All];

    [ObservableProperty] public partial PastChoice SelectedPast { get; set; }

    [ObservableProperty] public partial string? ListText { get; private set; }
    [ObservableProperty] public partial bool Loading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public string Footnote => IsCalendars
        ? "Each event is kept as an .ics file (with its repetitions, changes and reminders), so any calendar app can import it. Changes are backed up soon after you make them."
        : "Each contact is kept as a .vcf file with its photo, so any address book can import it. Changes are backed up soon after you make them.";

    /// <summary>The phone's accounts or calendars (through the agent's <c>collection-list</c>), the chosen ones ticked.</summary>
    public async Task LoadAsync()
    {
        if (!CanChoose || _shell.Session is not { } session) return;
        Loading = true;
        try
        {
            var list = await session.CollectionListAsync(Kind, CancellationToken.None);
            var checkedNow = Choices.Count > 0 ? Choices.Where(c => c.IsChecked).Select(c => c.Id).ToHashSet() : _initial;
            Choices.Clear();
            foreach (var c in list.Collections)
            {
                var choice = new CollectionChoice(c, Kind) { IsChecked = checkedNow.Contains(c.Id) };
                choice.PropertyChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
                Choices.Add(choice);
            }
            ListText = list.Problem ?? $"{CollectionTexts.Counts(Kind, list.Items, list.Collections.Count)} on this {_shell.Platform.DeviceNoun}.";
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

    public CollectionSelectionDto Selection() => new(BackUpAll,
        BackUpAll ? [] : [.. Choices.Where(c => c.IsChecked).Select(c => new CollectionRef(c.Id, c.Name))
            .Concat(_initial.Where(id => Choices.All(c => c.Id != id)).Select(id => new CollectionRef(id, _initialNames.GetValueOrDefault(id, ""))))],
        IsCalendars ? SelectedPast.Days : null);

    private bool CanSave() => CanChoose && !Working && (BackUpAll || Choices.Any(c => c.IsChecked));

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
        if (_shell.CollectionsDialog == this) _shell.CollectionsDialog = null;
    }
}
