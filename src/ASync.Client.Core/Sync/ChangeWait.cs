namespace ASync.Client.Core.Sync;

/// <summary>
/// The wait after local changes (task 26): a quiet period with a cap. Every file event restarts the quiet period; the run is
/// due when the quiet period passed since the last event, but no later than the cap after the first event not synced yet, so a
/// folder whose files are saved without pause still syncs every cap. Only local file events wait: other triggers (the
/// interval, <c>folder.changed</c>, "Sync now", a settings change) run at once, and a run, whatever started it, clears the wait
/// (<see cref="Reset"/>). Not thread-safe: the agent's worker locks around it.
/// </summary>
public sealed class ChangeWait(TimeSpan quiet, TimeSpan cap)
{
    public TimeSpan Quiet { get; } = quiet < TimeSpan.Zero ? TimeSpan.Zero : quiet;
    public TimeSpan Cap { get; } = cap < quiet ? quiet : cap;

    /// <summary>The first event not synced yet (null: nothing waits).</summary>
    public DateTimeOffset? First { get; private set; }

    /// <summary>The last event.</summary>
    public DateTimeOffset? Last { get; private set; }

    /// <summary>When the run is due (null: nothing waits).</summary>
    public DateTimeOffset? Due => First is { } first && Last is { } last
        ? Min(last + Quiet, first + Cap)
        : null;

    /// <summary>A file event at <paramref name="now"/>; returns when the run is due now.</summary>
    public DateTimeOffset Record(DateTimeOffset now)
    {
        First ??= now;
        if (Last is null || now > Last) Last = now;
        return Due!.Value;
    }

    /// <summary>A run started: what waited is in it. Returns the first event that waited (null: none).</summary>
    public DateTimeOffset? Reset()
    {
        var first = First;
        First = null;
        Last = null;
        return first;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}

/// <summary>
/// When a folder that follows recorded changes is scanned in full again (task 26): <paramref name="hours"/> after the last full
/// scan that went through, with a spread of ±20 % (devices and folders that started together do not all walk at once). On phones a
/// due rescan also waits for <paramref name="needs"/> (Wi-Fi and the charger): until they hold, runs stay incremental.
/// </summary>
public sealed class FullRescanSchedule(int hours, TimeProvider clock, Contracts.RunConditionsDto? needs = null,
    IDeviceConditionsProbe? probe = null, Func<double>? random = null)
{
    private readonly TimeSpan _every = TimeSpan.FromHours(Math.Clamp(hours, 1, Contracts.ChangeDelayDefaults.FullRescanLimitHours));
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;
    private bool _toldPostponed;

    /// <summary>When the next full rescan is due (null: none went through yet).</summary>
    public DateTimeOffset? Next { get; private set; }

    /// <summary>A full scan went through now.</summary>
    public void Done()
    {
        Next = clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Round(_every.TotalSeconds * (0.8 + 0.4 * _random())));
        _toldPostponed = false;
    }

    /// <summary>
    /// Why the next run must be full ("the full rescan is due"), or null. <paramref name="postponed"/>: set once per due rescan when
    /// it waits for the device's conditions (to log).
    /// </summary>
    public string? Due(out string? postponed)
    {
        postponed = null;
        if (Next is not { } due || clock.GetUtcNow() < due) return null;
        if (needs is { Any: true } && probe is not null && RunConditions.Blocked(needs, probe.Read()) is { } why)
        {
            if (!_toldPostponed) postponed = why;
            _toldPostponed = true;
            return null;
        }
        return "the full rescan is due";
    }
}
