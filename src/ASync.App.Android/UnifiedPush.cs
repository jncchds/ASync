using System.Text.Json;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Util;
using ASync.Agent;
using ASync.Agent.Control;
using ASync.App.Services;
using ASync.Contracts;
using AUri = Android.Net.Uri;

namespace ASync.App.Android;

/// <summary>
/// UnifiedPush, the Android connector side of its broadcast protocol (unifiedpush.org, "Android spec"), without a library:
/// distributors (ntfy, NextPush, …) are apps with a receiver for <c>org.unifiedpush.android.distributor.REGISTER</c>. The
/// app registers with a random token; the distributor answers <see cref="PushReceiver"/> with NEW_ENDPOINT (a URL the
/// server POSTs to), then delivers each POST as MESSAGE. The endpoint goes to the server as this device's push target
/// (<c>PUT /api/sync/push</c>); a message ("wake", no user data) starts an expedited sync. Broadcasts carrying another token
/// are ignored, so another app cannot swap the endpoint or fake messages without knowing it (and a faked wake only starts
/// a sync).
/// </summary>
internal static class UnifiedPush
{
    public const string ActionRegister = "org.unifiedpush.android.distributor.REGISTER";
    public const string ActionUnregister = "org.unifiedpush.android.distributor.UNREGISTER";
    public const string ActionAck = "org.unifiedpush.android.distributor.MESSAGE_ACK";
    public const string NewEndpoint = "org.unifiedpush.android.connector.NEW_ENDPOINT";
    public const string RegistrationFailed = "org.unifiedpush.android.connector.REGISTRATION_FAILED";
    public const string Unregistered = "org.unifiedpush.android.connector.UNREGISTERED";
    public const string Message = "org.unifiedpush.android.connector.MESSAGE";

    public static event Action? Changed;

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Log.Warn("ASync", $"UnifiedPush listener: {e.Message}"); }
    }

    public static IReadOnlyList<PushDistributor> Distributors(Context context)
    {
        var pm = context.PackageManager!;
        var found = new List<PushDistributor>();
#pragma warning disable CA1422 // QueryBroadcastReceivers(Intent, int) is deprecated on 33 in favour of the flags overload; same result
        foreach (var info in pm.QueryBroadcastReceivers(new Intent(ActionRegister), 0))
#pragma warning restore CA1422
        {
            var package = info.ActivityInfo?.PackageName;
            if (package is null || package == context.PackageName || found.Any(d => d.Id == package)) continue;
            var label = info.ActivityInfo!.ApplicationInfo is { } app ? pm.GetApplicationLabel(app) : package;
            found.Add(new PushDistributor(package, label ?? package));
        }
        return found;
    }

    public static PushState State(Context context)
    {
        var s = BackgroundState.Load(context);
        var name = s.PushDistributor is { } d ? Distributors(context).FirstOrDefault(x => x.Id == d)?.Name ?? d : null;
        return new PushState(s.PushDistributor, name, s.PushEndpoint is not null, s.PushRegistered, s.PushProblem);
    }

    public static void Register(Context context, string distributor)
    {
        var token = BackgroundState.Update(context, s =>
        {
            if (s.PushDistributor != distributor || s.PushToken is null) s.PushToken = Guid.NewGuid().ToString("N");
            s.PushDistributor = distributor;
            s.PushEndpoint = null;
            s.PushRegistered = false;
            s.PushProblem = null;
            return s.PushToken;
        });
        var intent = new Intent(ActionRegister).SetPackage(distributor)
            .PutExtra("token", token)
            .PutExtra("application", context.PackageName)
            .PutExtra("message", "ASync wake-ups")
            // AND_3: the distributor identifies the app by this PendingIntent's creator.
            .PutExtra("pi", PendingIntent.GetBroadcast(context, 0, new Intent("org.unifiedpush.dummy_app"), PendingIntentFlags.Immutable));
        context.SendBroadcast(intent);
        Log.Info("ASync", $"UnifiedPush: registering with {distributor}.");
        RaiseChanged();
    }

    public static void Unregister(Context context)
    {
        var (distributor, token) = BackgroundState.Update(context, s =>
        {
            var old = (s.PushDistributor, s.PushToken);
            s.PushDistributor = null;
            s.PushToken = null;
            s.PushEndpoint = null;
            s.PushRegistered = false;
            s.PushProblem = null;
            return old;
        });
        if (distributor is not null && token is not null)
            context.SendBroadcast(new Intent(ActionUnregister).SetPackage(distributor).PutExtra("token", token));
        _ = Task.Run(async () =>
        {
            try { await AndroidAgent.Runtime.SetPushTargetAsync(null, CancellationToken.None); }
            catch (ControlException e) { Log.Info("ASync", $"UnifiedPush: could not remove the endpoint from the server: {e.Message}"); }
        });
        RaiseChanged();
    }

    /// <summary>An endpoint not yet registered with the server goes there (when the device runs; again after a restart).</summary>
    public static void SendPending(Context context, AgentRuntime runtime)
    {
        var s = BackgroundState.Load(context);
        if (s.PushEndpoint is not { } endpoint || s.PushRegistered || !runtime.DeviceRunning) return;
        if (Interlocked.Exchange(ref _sending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            string? problem = null;
            var ok = false;
            try
            {
                await runtime.SetPushTargetAsync(new PushTargetRequest(PushTargetType.UnifiedPush, endpoint), CancellationToken.None);
                ok = true;
                Log.Info("ASync", $"UnifiedPush: the server can wake this device through {new Uri(endpoint).Host}.");
            }
            catch (ControlException e)
            {
                problem = e.Message.StartsWith("Cannot reach", StringComparison.Ordinal) || e.Message.Contains("starting", StringComparison.Ordinal)
                    ? null // try again later
                    : $"The server refused the address: {e.Message}";
                Log.Warn("ASync", $"UnifiedPush: registering the endpoint failed: {e.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _sending, 0);
            }
            BackgroundState.Update(context, st =>
            {
                if (st.PushEndpoint != endpoint) return 0; // changed meanwhile
                st.PushRegistered = ok;
                st.PushProblem = problem;
                return 0;
            });
            RaiseChanged();
        });
    }

    private static int _sending;

    /// <summary>A broadcast from the distributor (see <see cref="PushReceiver"/>).</summary>
    public static void Received(Context context, Intent intent)
    {
        var state = BackgroundState.Load(context);
        var token = intent.GetStringExtra("token");
        if (token is null || token != state.PushToken)
        {
            Log.Warn("ASync", $"UnifiedPush: ignored {intent.Action} with an unknown token.");
            return;
        }
        switch (intent.Action)
        {
            case NewEndpoint when intent.GetStringExtra("endpoint") is { Length: > 0 } endpoint:
                BackgroundState.Update(context, s =>
                {
                    if (s.PushEndpoint != endpoint) s.PushRegistered = false;
                    s.PushEndpoint = endpoint;
                    s.PushProblem = null;
                    return 0;
                });
                Log.Info("ASync", $"UnifiedPush: new endpoint on {AUri.Parse(endpoint)?.Host}.");
                AndroidAgent.EnsureStarted(context);
                SendPending(context, AndroidAgent.Runtime);
                break;
            case RegistrationFailed:
                BackgroundState.Update(context, s =>
                {
                    s.PushProblem = $"The distributor refused the registration{(intent.GetStringExtra("reason") is { } r ? $" ({r})" : "")}.";
                    return 0;
                });
                break;
            case Unregistered:
                BackgroundState.Update(context, s =>
                {
                    s.PushEndpoint = null;
                    s.PushRegistered = false;
                    s.PushProblem = "The distributor ended the registration: choose it again to turn wake-ups back on.";
                    return 0;
                });
                break;
            case Message:
            {
                // AND_2+: bytesMessage; AND_1: message (a string). Acknowledged with the message's id.
                var body = intent.GetByteArrayExtra("bytesMessage") is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : intent.GetStringExtra("message") ?? "";
                if (intent.GetStringExtra("id") is { } id && state.PushDistributor is { } distributor)
                    context.SendBroadcast(new Intent(ActionAck).SetPackage(distributor).PutExtra("token", token).PutExtra("id", id));
                BackgroundWork.Wake(context, Reason(body));
                break;
            }
        }
        RaiseChanged();
    }

    /// <summary>The wake's reason from <c>{"async":"wake","reason":"changed"}</c>; anything else counts as a test.</summary>
    public static string Reason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("reason", out var r) && r.GetString() is PushWake.Changed or PushWake.Overdue or PushWake.Test or PushWake.Browse)
                return r.GetString()!;
        }
        catch (JsonException) { }
        return PushWake.Test;
    }
}

/// <summary>The distributor's broadcasts (exported: distributors are other apps). See <see cref="UnifiedPush"/>.</summary>
[BroadcastReceiver(Name = "ua.pp.chds.async.PushReceiver", Exported = true)]
[IntentFilter([UnifiedPush.NewEndpoint, UnifiedPush.RegistrationFailed, UnifiedPush.Unregistered, UnifiedPush.Message])]
public sealed class PushReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null) return;
        try { UnifiedPush.Received(context.ApplicationContext ?? context, intent); }
        catch (Exception e) { Log.Warn("ASync", $"UnifiedPush: {intent.Action} failed: {e.GetType().Name}: {e.Message}"); }
    }
}

/// <summary>
/// <see cref="IBackgroundServices"/> on Android: the battery-optimisation exemption (the system's question, or its settings
/// list), the maker's extra steps as text, UnifiedPush and the reminder.
/// </summary>
internal sealed class AndroidBackgroundServices : IBackgroundServices
{
    public static readonly AndroidBackgroundServices Instance = new();

    private static Context Context => Application.Context;

    private AndroidBackgroundServices() => UnifiedPush.Changed += () => Changed?.Invoke();

    public event Action? Changed;

    public bool IsIgnoringBatteryOptimizations =>
        Context.GetSystemService(Context.PowerService) is PowerManager pm && pm.IsIgnoringBatteryOptimizations(Context.PackageName);

    public void RequestIgnoreBatteryOptimizations()
    {
        // Allowed for apps outside Google Play's policy (ours is an APK); the system asks the person.
        try
        {
            Context.StartActivity(new Intent(Settings.ActionRequestIgnoreBatteryOptimizations, AUri.Parse($"package:{Context.PackageName}"))
                .AddFlags(ActivityFlags.NewTask));
        }
        catch (ActivityNotFoundException) { OpenBatterySettings(); }
    }

    public void OpenBatterySettings()
    {
        try { Context.StartActivity(new Intent(Settings.ActionIgnoreBatteryOptimizationSettings).AddFlags(ActivityFlags.NewTask)); }
        catch (ActivityNotFoundException)
        {
            Context.StartActivity(new Intent(Settings.ActionApplicationDetailsSettings, AUri.Parse($"package:{Context.PackageName}")).AddFlags(ActivityFlags.NewTask));
        }
    }

    public string? VendorHint => Hint(Build.Manufacturer ?? "");

    /// <summary>Makers known to stop background work beyond Android's own rules (dontkillmyapp.com), as text only.</summary>
    public static string? Hint(string manufacturer)
    {
        var hint = manufacturer.ToLowerInvariant() switch
        {
            "xiaomi" or "redmi" or "poco" => "Xiaomi (MIUI, HyperOS): also turn on Autostart for ASync (Security → Permissions → Autostart) and set its Battery saver to \"No restrictions\".",
            "huawei" or "honor" => "Huawei and Honor: Settings → Battery → App launch: set ASync to \"Manage manually\" and allow auto-launch, secondary launch and running in the background.",
            "samsung" => "Samsung: Settings → Battery → Background usage limits: keep ASync out of \"Sleeping apps\" and \"Deep sleeping apps\".",
            "oneplus" or "oppo" or "realme" => "OnePlus, OPPO and realme: in ASync's app info, Battery: choose \"Unrestricted\" (or \"Don't optimise\") and allow background activity and auto launch.",
            "vivo" or "iqoo" => "vivo: Settings → Battery → Background power consumption: allow ASync to use power in the background.",
            "asus" => "ASUS: turn off \"Auto-start Manager\"/PowerMaster restrictions for ASync.",
            "meizu" => "Meizu: allow ASync to run in the background in Security → Permissions.",
            "nokia" or "hmd global" => "Nokia: if background syncs stop, check that no \"Battery protection\"/\"Evenwell\" app restricts ASync.",
            "sony" => "Sony: exclude ASync from STAMINA mode's restrictions.",
            "lenovo" or "motorola" => "Motorola and Lenovo: in ASync's app info, Battery: choose \"Unrestricted\".",
            _ => null,
        };
        return hint is null ? null : hint + " More for each maker: dontkillmyapp.com.";
    }

    public IReadOnlyList<PushDistributor> Distributors() => UnifiedPush.Distributors(Context);

    public PushState Push => UnifiedPush.State(Context);

    public void RegisterPush(string distributorId) => UnifiedPush.Register(Context, distributorId);

    public void UnregisterPush() => UnifiedPush.Unregister(Context);

    public int ReminderDays
    {
        get => BackgroundState.Load(Context).ReminderDays;
        set
        {
            if (value == ReminderDays) return;
            BackgroundState.Update(Context, s => s.ReminderDays = value);
            Reminders.Reset(Context, AndroidAgent.Runtime.Folders.Select(f => f.Folder));
        }
    }
}
