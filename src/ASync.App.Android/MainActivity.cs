using System.Collections.Concurrent;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using ASync.App.Services;
using ASync.App.ViewModels;
using ASync.App.Views;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace ASync.App.Android;

/// <summary>The Android application: starts the in-process agent with the process (see <see cref="AndroidAgent"/>).</summary>
[Application(Label = "ASync", Icon = "@mipmap/ic_launcher", AllowBackup = false, UsesCleartextTraffic = true)]
public sealed class AndroidApplication : AvaloniaAndroidApplication<AndroidShellApp>
{
    public AndroidApplication(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer) { }

    public override void OnCreate()
    {
        base.OnCreate();
        AndroidAgent.EnsureStarted(this);
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).WithInterFont();
}

/// <summary>
/// The shared <see cref="App"/> on a single view: the <see cref="ShellView"/> over one <see cref="ShellViewModel"/> per
/// process (an activity re-created by a rotation or the system keeps it), connected to the in-process agent.
/// </summary>
public sealed class AndroidShellApp : App
{
    private static ShellViewModel? _shell;

    public static ShellViewModel? Shell => _shell;

    /// <summary>The shell's view while the activity shows it (the automation scrolls it for screenshots).</summary>
    public static Control? View { get; private set; }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            AndroidAgent.EnsureStarted(global::Android.App.Application.Context);
            if (_shell is null)
            {
                var settings = new JsonSettingsStore(Path.Combine(AndroidAgent.ConfigDirectory(global::Android.App.Application.Context), "app-settings.json"));
                _shell = new ShellViewModel(AndroidAgent.Connector, new NoAgentService(), new AndroidPlatform(() => MainActivity.Current), settings,
                    updates: new NoUpdates("Install a newer APK from the project's GitHub releases to update."));
                _shell.Start();
            }
            var view = new ShellView { DataContext = _shell };
            View = view;
            view.AttachedToVisualTree += (_, _) =>
            {
                if (TopLevel.GetTopLevel(view) is { } top)
                    top.BackRequested += (_, e) => e.Handled = _shell.GoBack();
            };
            single.MainView = view;
            MainActivity.Current?.DeliverPendingLink();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>No agent service on Android: the agent runs in the app (the setup page never shows).</summary>
internal sealed class NoAgentService : IAgentServiceManager
{
    public Task<AgentServiceState> GetStateAsync(CancellationToken ct) => Task.FromResult(AgentServiceState.Unsupported);
    public Task<string> InstallAndStartAsync(CancellationToken ct) => Task.FromResult("The agent runs inside the app on Android.");
    public Task<string> StartAsync(CancellationToken ct) => Task.FromResult("The agent runs inside the app on Android.");
    public Task<string> StopAsync(CancellationToken ct) => Task.FromResult("The agent runs inside the app on Android.");
}

/// <summary>
/// The one activity: the Avalonia view, the pairing links <see cref="LinkActivity"/> hands over, and the system activities
/// the app starts for a result (the folder picker, the QR scanner).
/// </summary>
[Activity(Name = "ua.pp.chds.async.MainActivity", Label = "ASync", Theme = "@style/ASyncTheme", Icon = "@mipmap/ic_launcher", MainLauncher = true, Exported = true,
    LaunchMode = LaunchMode.SingleTask, WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout
                           | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : AvaloniaMainActivity
{
    private static WeakReference<MainActivity>? _current;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<(bool, Intent?)>> _results = new();
    private int _nextRequest = 1000;
    private static string? _pendingLink;

    public static MainActivity? Current => _current is not null && _current.TryGetTarget(out var a) && !a.IsFinishing ? a : null;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        _current = new WeakReference<MainActivity>(this);
        base.OnCreate(savedInstanceState);
        DeliverPendingLink();
    }

    protected override void OnResume()
    {
        _current = new WeakReference<MainActivity>(this);
        base.OnResume();
        DeliverPendingLink();
        AndroidAgent.AppOpened();
        // Back from the system's battery question or a distributor: show what changed.
        if (AndroidShellApp.Shell?.Background is { } background) Dispatcher.UIThread.Post(background.Refresh);
        // Back from the All files access page: the folder dialog, Settings and the folder cards look again.
        AndroidStorage.Instance.NotifyChanged();
        // Back from the app's settings ("Allow all" photos, the location): the Photos screen and the cards look again.
        AndroidPhotoAccess.Instance.NotifyChanged();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == 2) AndroidStorage.Instance.NotifyChanged(); // the storage permission on Android 8–10
        if (requestCode == AndroidPhotoAccess.RequestCode) AndroidPhotoAccess.Instance.NotifyChanged(); // photos and videos (task 19)
    }

    /// <summary>A pairing link to fill in when the activity shows (from <see cref="LinkActivity"/>).</summary>
    internal static void SetPendingLink(string link) => _pendingLink = link;

    /// <summary>A pairing link fills in the enrollment form (once the shell exists).</summary>
    internal void DeliverPendingLink()
    {
        if (_pendingLink is not { } link || AndroidShellApp.Shell is not { } shell) return;
        _pendingLink = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (shell.Page is not EnrollViewModel) return; // enrolled already: the link is of no use now
            shell.Enroll.ApplyLink(link);
        });
    }

    /// <summary>Starts an activity and waits for its result (OK, and its data).</summary>
    public Task<(bool Ok, Intent? Data)> StartForResultAsync(Intent intent)
    {
        var code = Interlocked.Increment(ref _nextRequest);
        var done = new TaskCompletionSource<(bool, Intent?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _results[code] = done;
        try
        {
            StartActivityForResult(intent, code);
        }
        catch (ActivityNotFoundException)
        {
            _results.TryRemove(code, out _);
            done.TrySetResult((false, null));
        }
        return done.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (_results.TryRemove(requestCode, out var done)) done.TrySetResult((resultCode == Result.Ok, data));
        base.OnActivityResult(requestCode, resultCode, data);
    }

    /// <summary>Android 13+: notifications need the person's permission (asked once, when the first one is due).</summary>
    internal void AskForNotifications()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        var permission = Manifest.Permission.PostNotifications;
        if (CheckSelfPermission(permission) != Permission.Granted)
            RunOnUiThread(() => RequestPermissions([permission], 1));
    }
}

/// <summary>
/// Opens <c>async://pair?s=…&amp;c=…</c> links (the web UI's QR code read by another app, a tapped link): keeps the link for
/// <see cref="MainActivity"/> and brings it up, then closes. A trampoline because a link sent to the single-task main
/// activity only brings its task to the front when it already runs (START_TASK_TO_FRONT), without the new intent.
/// </summary>
[Activity(Name = "ua.pp.chds.async.LinkActivity", Exported = true, NoHistory = true, ExcludeFromRecents = true,
    Theme = "@android:style/Theme.Translucent.NoTitleBar")]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "async", DataHost = "pair")]
public sealed class LinkActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (Intent?.DataString is { } link) MainActivity.SetPendingLink(link);
        StartActivity(new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.ReorderToFront));
        Finish();
    }
}
