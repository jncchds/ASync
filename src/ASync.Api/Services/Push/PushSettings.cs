namespace ASync.Api.Services.Push;

/// <summary>
/// <c>Push__Mode</c>: how this server wakes devices (AGENTS.md "Push"). <c>UnifiedPush</c> (the default) posts a
/// "wake" straight to the endpoint a device registered from its UnifiedPush distributor; nothing is sent to a device that
/// registered none. <c>None</c> sends nothing. <c>Relay</c> and <c>Direct</c> (APNs/FCM) come later: until then they act
/// as <c>UnifiedPush</c>, with a warning. Alerts never need push.
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

    /// <summary>Whether wake-ups go to UnifiedPush endpoints.</summary>
    public bool UnifiedPush => Mode != PushMode.None;

    public static PushSettings FromConfiguration(IConfiguration c)
    {
        var raw = c["Push:Mode"];
        var (mode, warning) = string.IsNullOrWhiteSpace(raw) ? (PushMode.UnifiedPush, null)
            : !Enum.TryParse<PushMode>(raw, ignoreCase: true, out var parsed) || int.TryParse(raw, out _)
                ? (PushMode.UnifiedPush, $"Push__Mode={raw} is not a known mode (None, UnifiedPush, Relay, Direct); using UnifiedPush.")
            : parsed is PushMode.Relay or PushMode.Direct
                ? (PushMode.UnifiedPush, $"Push__Mode={raw}: the relay and direct APNs/FCM pushes are not available yet; UnifiedPush wake-ups are sent. Overdue alerts work without push.")
            : (parsed, (string?)null);
        static TimeSpan Read(IConfiguration c, string key, TimeSpan fallback, Func<double, TimeSpan> unit) =>
            double.TryParse(c[key], System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? unit(v) : fallback;
        var defaults = new PushSettings(mode, warning);
        return defaults with
        {
            ChangedInterval = Read(c, "Push:ChangedSeconds", defaults.ChangedInterval, TimeSpan.FromSeconds),
            OverdueInterval = Read(c, "Push:OverdueHours", defaults.OverdueInterval, TimeSpan.FromHours),
            Timeout = Read(c, "Push:TimeoutSeconds", defaults.Timeout, TimeSpan.FromSeconds),
        };
    }
}
