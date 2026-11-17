using ASync.App.Services;
using ASync.App.ViewModels;
using ASync.App.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.iOS;
using Avalonia.Threading;
using Foundation;
using UIKit;

namespace ASync.App.iOS;

public static class Program
{
    private static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

/// <summary>
/// The application delegate: Avalonia's (one scene, one view), with the Inter font, plus what background work needs from the
/// app delegate (task 22): the background tasks registered before launch ends, the APNs token, silent pushes.
/// </summary>
[Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<IosShellApp>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).WithInterFont();

    /// <summary>Before Avalonia's didFinishLaunching: iOS wants the BGTask handlers registered by the end of launch.</summary>
    [Export("application:willFinishLaunchingWithOptions:")]
    public bool WillFinishLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        IosBackground.RegisterTasks();
        return true;
    }

    [Export("application:didRegisterForRemoteNotificationsWithDeviceToken:")]
    public void RegisteredForRemoteNotifications(UIApplication application, NSData deviceToken) => IosBackground.PushToken(deviceToken.ToArray());

    [Export("application:didFailToRegisterForRemoteNotificationsWithError:")]
    public void FailedToRegisterForRemoteNotifications(UIApplication application, NSError error) => IosBackground.PushFailed(error.LocalizedDescription);

    [Export("application:didReceiveRemoteNotification:fetchCompletionHandler:")]
    public void DidReceiveRemoteNotification(UIApplication application, NSDictionary userInfo, Action<UIBackgroundFetchResult> completionHandler) =>
        IosBackground.RemoteNotification(userInfo, completionHandler);
}

/// <summary>
/// The shared <see cref="App"/> on a single view: the <see cref="ShellView"/> over one <see cref="ShellViewModel"/> per
/// process, connected to the in-process agent. Pairing links (<c>async://pair</c>, opened from another app, the Camera
/// app's QR reader, or at launch) arrive as Avalonia's protocol activations and fill in the enrollment form.
/// </summary>
public sealed class IosShellApp : App
{
    public static readonly PairingLinkInbox Links = new();

    public static ShellViewModel? Shell { get; private set; }

    /// <summary>The shell's view (the automation scrolls it for screenshots).</summary>
    public static Control? View { get; private set; }

    public static IosPlatform? Platform { get; private set; }

    public override void OnFrameworkInitializationCompleted()
    {
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e is ProtocolActivatedEventArgs { Uri: var uri })
                {
                    Console.WriteLine($"ASync: opened {uri.Scheme}://{uri.Host}");
                    // async://sync: "Back up now" from Shortcuts (Open URL, also in automations) or another app.
                    if (uri.Scheme == "async" && uri.Host == "sync") IosBackground.RunAllNow("async://sync");
                    else OpenLink(uri.OriginalString);
                }
                // Coming to the front (also at launch) is IosAgent's: UIApplication.didBecomeActive.
            };
        }
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            IosAgent.EnsureStarted();
            if (Shell is null)
            {
                Platform = new IosPlatform(IosAgent.Trees);
                var settings = new JsonSettingsStore(Path.Combine(IosAgent.ConfigDirectory, "app-settings.json"));
                Shell = new ShellViewModel(IosAgent.Connector, new NoAgentService(), Platform, settings,
                    updates: new NoUpdates("Install a newer .ipa from the project's GitHub releases (SideStore or AltStore) to update."));
                Links.Attach(Shell);
                Shell.Start();
            }
            var view = new ShellView { DataContext = Shell };
            View = view;
            single.MainView = view;
            Automation.StartIfRequested(Shell, Platform!);
            DeliverPendingLink();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>A pairing link fills in the enrollment form (kept until the shell exists).</summary>
    public static void OpenLink(string link)
    {
        if (Links.Offer(link)) DeliverPendingLink();
    }

    private static void DeliverPendingLink() => Dispatcher.UIThread.Post(() => Links.Deliver(Shell));
}
