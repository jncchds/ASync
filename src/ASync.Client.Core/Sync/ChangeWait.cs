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
