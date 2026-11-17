using System.Collections.ObjectModel;
using ASync.Agent.Control;
using ASync.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ASync.App.ViewModels;

/// <summary>The kinds of the web UI's pills (<c>.pill-success</c> …).</summary>
public enum PillKind { Muted, Accent, Success, Warning, Danger }

/// <summary>A badge: its text and colour. The views pick the colour with style classes.</summary>
public sealed record Pill(string Text, PillKind Kind)
{
    public bool IsAccent => Kind == PillKind.Accent;
    public bool IsSuccess => Kind == PillKind.Success;
    public bool IsWarning => Kind == PillKind.Warning;
    public bool IsDanger => Kind == PillKind.Danger;
}

/// <summary>The main page: this device and server, the connection, and every folder assigned to the device.</summary>
public sealed partial class DashboardViewModel(ShellViewModel shell) : ObservableObject
{
    public ShellViewModel Shell => shell;

    public ObservableCollection<FolderViewModel> Folders { get; } = [];

    [ObservableProperty] public partial string DeviceName { get; private set; } = "";
    [ObservableProperty] public partial string Account { get; private set; } = "";
    [ObservableProperty] public partial string ServerUrl { get; private set; } = "";
    [ObservableProperty] public partial Pill State { get; private set; } = new("Connecting…", PillKind.Muted);
    [ObservableProperty] public partial string? Problem { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoFolders))]
    public partial bool HasFolders { get; private set; }

    public bool HasNoFolders => !HasFolders;

    /// <summary>Where folders come from: the web UI, on a computer when this is a phone.</summary>
    public string NoFoldersHint => Shell.Platform.OffersWebUi
        ? "Create one with New folder…, add this device to one of your folders with Add to a folder…, or attach folders in the web UI " +
          "(Folders → a folder → Devices). They appear here at once."
        : $"Create one with New folder…, or add this {Shell.Platform.DeviceNoun} to one of your folders with Add to a folder…. " +
          $"Folders attached in the web UI on your computer appear here at once too; then choose where each one lives on this {Shell.Platform.DeviceNoun}.";

    public void Update(AgentStatusDto status)
    {
        DeviceName = status.Device?.DeviceName ?? "";
        Account = status.Device?.Username ?? "";
        ServerUrl = status.Device?.ServerUrl ?? "";
        State = new Pill(Format.State(status.State), status.State switch
        {
            AgentState.Connected => PillKind.Success,
            AgentState.Connecting or AgentState.Reconnecting or AgentState.Stopping => PillKind.Warning,
            _ => PillKind.Danger,
        });
        Problem = status.State is AgentState.Connected ? null : status.Problem;
        if (status.State == AgentState.Revoked)
            Problem = (status.Problem is { Length: > 0 } p ? p + " " : "") + "Sign out of this device in Settings, then enroll it again.";

        var wanted = status.Folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (var i = Folders.Count - 1; i >= 0; i--)
            if (wanted.All(f => f.FolderId != Folders[i].Id)) Folders.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var existing = Folders.FirstOrDefault(f => f.Id == wanted[i].FolderId);
            if (existing is null)
            {
                existing = new FolderViewModel(shell, wanted[i].FolderId);
                Folders.Insert(i, existing);
            }
            else if (Folders.IndexOf(existing) != i)
            {
                Folders.Move(Folders.IndexOf(existing), i);
            }
            existing.Update(wanted[i]);
            existing.UpdateRestore(status.MediaRestore);
        }
        HasFolders = Folders.Count > 0;
    }

    /// <summary>All files access may have changed: the folders' offers to switch to their real paths.</summary>
    public void RefreshStorage()
    {
        foreach (var f in Folders) f.RefreshStorage();
    }

    public void UpdateFolder(FolderStatusDto folder) => Folders.FirstOrDefault(f => f.Id == folder.FolderId)?.Update(folder);

    /// <summary>Refreshes "5 min ago" and "in 10 min".</summary>
    public void Tick()
    {
        foreach (var f in Folders) f.RefreshTimes();
    }
}
