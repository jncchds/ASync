using ASync.Contracts;

namespace ASync.Api.Services.Push;

/// <summary>
/// <c>Push__Mode</c>: how this server wakes devices (AGENTS.md "Push"). <c>UnifiedPush</c> (the default) posts a
/// "wake" straight to the endpoint a device registered from its UnifiedPush distributor; nothing is sent to a device that
/// registered none. <c>Direct</c> also sends APNs pushes to iOS devices itself, with the <c>Push__Apns__*</c> key of whoever
/// signs the app (<see cref="ApnsOptions"/>; without it, a warning and UnifiedPush only). <c>None</c> sends nothing.
/// <c>Relay</c> (our hosted relay for the published apps) is not built yet: it acts as <c>UnifiedPush</c>, with a warning.
/// FCM is not built either. Alerts never need push.
/// </summary>
public enum PushMode { None, UnifiedPush, Relay, Direct }

public sealed record PushSettings(PushMode Mode, string? Warning)
{
    /// <summary>A device is woken for content changes at most this often (the wake only starts a sync).</summary>
    public TimeSpan ChangedInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>An overdue device is woken at most this often while it stays overdue.</summary>
    public TimeSpan OverdueInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>How long one wake-up POST may take (user-supplied address: kept short, like alert webhooks).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The APNs credentials (<c>Direct</c> only); null = no APNs pushes.</summary>
    public ApnsOptions? Apns { get; init; }

    /// <summary>Whether wake-ups go to UnifiedPush endpoints.</summary>
    public bool UnifiedPush => Mode != PushMode.None;

    /// <summary>Whether wake-ups go to iOS devices' APNs tokens.</summary>
    public bool ApnsEnabled => Mode == PushMode.Direct && Apns is not null;

    /// <summary>Whether this server sends wake-ups to targets of this kind.</summary>
    public bool Sends(PushTargetType type) => type == PushTargetType.Apns ? ApnsEnabled : UnifiedPush;

    public static PushSettings FromConfiguration(IConfiguration c)
    {
        var raw = c["Push:Mode"];
        var (mode, warning) = string.IsNullOrWhiteSpace(raw) ? (PushMode.UnifiedPush, null)
            : !Enum.TryParse<PushMode>(raw, ignoreCase: true, out var parsed) || int.TryParse(raw, out _)
                ? (PushMode.UnifiedPush, $"Push__Mode={raw} is not a known mode (None, UnifiedPush, Relay, Direct); using UnifiedPush.")
            : parsed is PushMode.Relay
                ? (PushMode.UnifiedPush, $"Push__Mode={raw}: the relay is not available yet; UnifiedPush wake-ups are sent (Direct sends APNs pushes with your own key). Overdue alerts work without push.")
            : (parsed, (string?)null);
        ApnsOptions? apns = null;
        if (mode == PushMode.Direct)
        {
            (apns, var problem) = ApnsOptions.FromConfiguration(c);
            if (problem is not null) warning = $"Push__Mode=Direct: {problem} No APNs pushes are sent; UnifiedPush wake-ups are (FCM is not built yet).";
        }
        static TimeSpan Read(IConfiguration c, string key, TimeSpan fallback, Func<double, TimeSpan> unit) =>
            double.TryParse(c[key], System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? unit(v) : fallback;
        var defaults = new PushSettings(mode, warning) { Apns = apns };
        return defaults with
        {
            ChangedInterval = Read(c, "Push:ChangedSeconds", defaults.ChangedInterval, TimeSpan.FromSeconds),
            OverdueInterval = Read(c, "Push:OverdueHours", defaults.OverdueInterval, TimeSpan.FromHours),
            Timeout = Read(c, "Push:TimeoutSeconds", defaults.Timeout, TimeSpan.FromSeconds),
        };
    }
}
