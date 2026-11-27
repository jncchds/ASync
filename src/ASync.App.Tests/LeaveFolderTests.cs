using ASync.Agent.Control;
using ASync.App.ViewModels;
using ASync.App.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace ASync.App.Tests;

/// <summary>
/// Stopping a folder on this device from the app ("Stop syncing here…"): asked first, done by the agent's leave-folder,
/// a refusal kept on the card, a document tree's permission given back, and the card in a phone-sized window.
/// </summary>
public class LeaveFolderTests
{
    [Fact]
    public async Task StopSyncingHere_AsksFirst_ThenTheAgentLeaves()
    {
        await using var f = await new ShellFixture().StartAndWait(Statuses.Connected(Statuses.Folder()));
        var folder = f.Shell.Dashboard.Folders.Single();
        Assert.Equal("Stop syncing \"Docs\" on this computer?", folder.LeaveQuestion);
        Assert.Contains("Its files stay", folder.LeaveHint);

        folder.AskLeaveCommand.Execute(null);
        Assert.True(folder.ConfirmingLeave);
        folder.CancelLeaveCommand.Execute(null);
        Assert.False(folder.ConfirmingLeave);
        Assert.DoesNotContain(f.Agent.Calls, c => c.StartsWith("leave-folder", StringComparison.Ordinal));

        folder.AskLeaveCommand.Execute(null);
        await folder.ConfirmLeaveCommand.ExecuteAsync(null);
        Assert.Contains("leave-folder 1", f.Agent.Calls);
        Assert.False(folder.ConfirmingLeave);
        Assert.Equal("left", f.Shell.Message);

        // The agent's next status no longer has it: the card goes.
        f.Agent.PushStatus(Statuses.Connected());
        await ShellFixture.Until(() => f.Shell.Dashboard.Folders.Count == 0);
    }

    [Fact]
    public async Task ARefusal_StaysOnTheCard_WithTheAgentsMessage()
    {
        await using var f = await new ShellFixture().StartAndWait(Statuses.Connected(Statuses.Folder()));
        var folder = f.Shell.Dashboard.Folders.Single();
        f.Agent.Fail = new ControlException("Cannot reach the server, so nothing was changed: offline");
        folder.AskLeaveCommand.Execute(null);
        await folder.ConfirmLeaveCommand.ExecuteAsync(null);
        Assert.True(folder.ConfirmingLeave);
        Assert.Equal(("Cannot reach the server, so nothing was changed: offline", true), (f.Shell.Message, f.Shell.MessageIsError));
    }

    [Fact]
    public async Task OnAPhone_ADocumentTreeGivesItsPermissionBack_AndPhotosFoldersSayNothingIsDeleted()
    {
        var f = new ShellFixture();
        await using var _ = f;
        var storage = new FakeStorage();
        f.Platform.StorageAccess = storage;
        f.Platform.Phone = true;
        const string tree = "content://com.android.externalstorage.documents/tree/primary%3ADocs";
        var photos = Statuses.Folder(2, "Camera", "") with { Kind = ASync.Contracts.FolderKind.Photos };
        await f.StartAndWait(Statuses.Connected(Statuses.Folder(path: tree), photos));
        var docs = f.Shell.Dashboard.Folders.Single(x => x.Id == 1);
        var camera = f.Shell.Dashboard.Folders.Single(x => x.Id == 2);
        Assert.Contains("Nothing on this phone is deleted", camera.LeaveHint);

        docs.AskLeaveCommand.Execute(null);
        await docs.ConfirmLeaveCommand.ExecuteAsync(null);
        Assert.Contains("leave-folder 1", f.Agent.Calls);
        Assert.Equal(["release " + tree], storage.Calls);
    }

    [AvaloniaFact]
    public async Task TheCard_HasTheButton_AndTheQuestionFitsAPhone()
    {
        var f = new ShellFixture();
        await using var _ = f;
        f.Platform.Phone = true;
        f.Start(Statuses.Connected(Statuses.Folder()));
        var window = new Window { Width = 360, Height = 760, Content = new ShellView { DataContext = f.Shell } };
        window.Show();
        await Pump(() => f.Shell.Status is not null && Shows(window, "AskLeave"));
        Assert.False(Shows(window, "LeaveConfirm"));

        Named<Button>(window, "AskLeave").Command!.Execute(null);
        await Pump(() => Shows(window, "LeaveConfirm"));
        Assert.False(Shows(window, "AskLeave"));
        Assert.True(Named<Border>(window, "LeaveConfirm").Bounds.Width <= 360);
        Named<Button>(window, "ConfirmLeave").Command!.Execute(null);
        await Pump(() => f.Agent.Calls.Contains("leave-folder 1"));
    }

    private static async Task Pump(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name && c.IsEffectivelyVisible);

    private static bool Shows(Window window, string name) =>
        window.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name && c.IsEffectivelyVisible);
}
