using System.Globalization;
using ASync.Engine.Repository;

namespace ASync.Engine.Maintenance;

/// <summary>
/// Which snapshots to keep, per source (restic-style). A snapshot is kept if any rule wants it: the newest
/// <see cref="Last"/>; the newest of each of the last <see cref="Hourly"/> hours, <see cref="Daily"/> days…; and all
/// within <see cref="Within"/> of the newest snapshot. The newest snapshot of each source is always kept.
/// All-zero means "keep everything".
/// </summary>
public sealed record RetentionPolicy
{
    public int Last { get; init; }
    public int Hourly { get; init; }
    public int Daily { get; init; }
    public int Weekly { get; init; }
    public int Monthly { get; init; }
    public int Yearly { get; init; }
    public TimeSpan? Within { get; init; }

    public bool KeepsEverything => Last == 0 && Hourly == 0 && Daily == 0 && Weekly == 0 && Monthly == 0 && Yearly == 0 && Within is null;

    /// <summary>A sensible default: 7 daily, 4 weekly, 12 monthly, plus everything from the last 24 hours.</summary>
    public static RetentionPolicy Default { get; } = new() { Daily = 7, Weekly = 4, Monthly = 12, Within = TimeSpan.FromHours(24) };

    /// <summary>Splits snapshots into those to keep and those to forget. Times are bucketed in UTC.</summary>
    public (List<Snapshot> Keep, List<Snapshot> Forget) Apply(IEnumerable<Snapshot> snapshots)
    {
        var keep = new List<Snapshot>();
        var forget = new List<Snapshot>();
        foreach (var group in snapshots.GroupBy(s => s.Source))
        {
            var ordered = group.OrderByDescending(s => s.Time).ToList();
            if (KeepsEverything)
            {
                keep.AddRange(ordered);
                continue;
            }

            var kept = new HashSet<string> { ordered[0].Id };
            for (var i = 0; i < Math.Min(Last, ordered.Count); i++) kept.Add(ordered[i].Id);
            if (Within is TimeSpan within)
                foreach (var s in ordered.Where(s => ordered[0].Time - s.Time <= within)) kept.Add(s.Id);

            Bucket(ordered, Hourly, s => s.Time.UtcDateTime.ToString("yyyyMMddHH", CultureInfo.InvariantCulture), kept);
            Bucket(ordered, Daily, s => s.Time.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture), kept);
            Bucket(ordered, Weekly, s => $"{ISOWeek.GetYear(s.Time.UtcDateTime)}-{ISOWeek.GetWeekOfYear(s.Time.UtcDateTime)}", kept);
            Bucket(ordered, Monthly, s => s.Time.UtcDateTime.ToString("yyyyMM", CultureInfo.InvariantCulture), kept);
            Bucket(ordered, Yearly, s => s.Time.UtcDateTime.Year.ToString(CultureInfo.InvariantCulture), kept);

            foreach (var s in ordered) (kept.Contains(s.Id) ? keep : forget).Add(s);
        }
        return (keep, forget);
    }

    /// <summary>Keeps the newest snapshot of each of the latest <paramref name="count"/> distinct periods.</summary>
    private static void Bucket(List<Snapshot> newestFirst, int count, Func<Snapshot, string> period, HashSet<string> kept)
    {
        if (count <= 0) return;
        var seen = new HashSet<string>();
        foreach (var s in newestFirst)
        {
            if (seen.Count >= count) break;
            if (seen.Add(period(s))) kept.Add(s.Id);
        }
    }
}
