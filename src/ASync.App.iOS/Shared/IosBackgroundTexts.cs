using ASync.App.Services;

namespace ASync.App.iOS;

/// <summary>UIApplication.backgroundRefreshStatus: Settings › General › Background App Refresh, or restricted (Screen Time, MDM).</summary>
public enum IosRefreshStatus { Available, Denied, Restricted }

/// <summary>
/// UNUserNotificationCenter's authorization. Provisional: granted without asking (the app asks for it at start), notifications
/// arrive quietly in Notification Center; without any authorization iOS does not even keep the reminders' pending requests.
/// </summary>
public enum IosNotificationStatus { NotDetermined, Denied, Allowed, Provisional }

/// <summary>
/// What the shared background card and the dashboard prompt say on an iPhone (<see cref="IBackgroundServices.Texts"/>): iOS has no
/// battery exemption to ask for; what holds background work back is Background App Refresh, Low Power Mode, and how often the
/// person uses the app. The prompt asks for the notification permission (the reminder, the overdue alert), the one thing the
/// app can ask for; the rest are links to iOS's Settings.
/// </summary>
public static class IosBackgroundTexts
{
    public static readonly BackgroundTexts Texts = new(
        AllowanceLabel: "Background App Refresh",
        Unrestricted: "Background App Refresh is on and notifications are allowed. iOS gives ASync background time when it chooses: " +
                      "usually a few times a day, more often for apps you use. Opening ASync syncs every folder.",
        Restricted: "iOS holds ASync's background syncs back.",
        PromptTitle: "Let ASync remind you",
        PromptExplanation:
            "iOS decides when apps run in the background: a few times a day for apps you use, rarely in Low Power Mode, never when you " +
            "close the app from the app switcher. Folders also sync every time you open ASync. Allow notifications so the iPhone can " +
            "tell you when a folder has not synced for days.",
        AllowLabel: "Allow",
        SettingsLabel: "iOS Settings",
        PushLabel: "Wake-ups (Apple push)",
        PushExplanation:
            "When the app is signed with push and the server sends Apple pushes (Push__Mode=Direct), the server can wake this iPhone " +
            "when another device changes a folder or when it has not synced for too long. The message only says \"wake up\", and iOS " +
            "delivers few of them. Without push, folders still sync on iOS's schedule and when you open the app.",
        ReminderNote: "A notification from the iPhone itself, moved on after every successful run: it needs neither the server nor push.");

    /// <summary>Nothing to ask for: Background App Refresh is available and the notification question was answered (no nagging after a no).</summary>
    public static bool Unrestricted(IosRefreshStatus refresh, IosNotificationStatus notifications) =>
        refresh == IosRefreshStatus.Available && notifications is IosNotificationStatus.Allowed or IosNotificationStatus.Denied;

    /// <summary>Whether the system's notification question is still to be asked (never asked, or only provisional so far).</summary>
    public static bool MayAsk(IosNotificationStatus notifications) =>
        notifications is IosNotificationStatus.NotDetermined or IosNotificationStatus.Provisional;

    /// <summary>What holds background work back now, or null (the card then says <see cref="BackgroundTexts.Unrestricted"/>).</summary>
    public static string? Restriction(IosRefreshStatus refresh, bool lowPowerMode, IosNotificationStatus notifications) => refresh switch
    {
        IosRefreshStatus.Denied => "Background App Refresh is off for ASync (Settings › General › Background App Refresh): folders sync only while the app is open.",
        IosRefreshStatus.Restricted => "Background App Refresh is restricted on this iPhone (Screen Time or a profile): folders sync only while the app is open.",
        _ when lowPowerMode => "Low Power Mode is on: iOS runs no background work until it is off. Folders sync when you open ASync.",
        _ => notifications switch
        {
            IosNotificationStatus.NotDetermined => "Notifications are not allowed yet: the iPhone cannot remind you when a folder has not synced for days.",
            IosNotificationStatus.Provisional => "Reminders arrive quietly in Notification Center. Allow notifications to see them on the Lock Screen too.",
            IosNotificationStatus.Denied => "Notifications are off for ASync (Settings › Notifications): reminders and overdue alerts are not shown.",
            _ => null,
        },
    };

    /// <summary>The wake-ups' state in words (<see cref="IBackgroundServices.PushStatusText"/>).</summary>
    public static string Push(ApnsDecision decision, bool hasToken, bool registered, string? problem) =>
        !decision.Register ? decision.Why
        : problem is not null ? problem
        : registered ? "This iPhone's push token is on the server: it can wake the app when it sends Apple pushes (Push__Mode=Direct)."
        : hasToken ? "Sending the push token to the server…"
        : "Asking iOS for a push token…";
}
