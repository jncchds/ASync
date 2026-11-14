using ASync.Contracts;
using ASync.Core.Models;

namespace ASync.Core.Alerts;

/// <summary>A person's effective alert rules: their settings, or the server's defaults.</summary>
public sealed record AlertPolicy(double GraceFactor, TimeSpan MinimumGrace, int FailureThreshold, TimeSpan Reminder)
{
    /// <summary>How long a device may go without a successful run of <paramref name="folder"/>.</summary>
    public TimeSpan GraceFor(Folder folder)
    {
        var scaled = TimeSpan.FromMinutes(Math.Max(0, folder.IntervalMinutes) * Math.Max(0, GraceFactor));
        return scaled > MinimumGrace ? scaled : MinimumGrace;
    }
}

/// <summary>A problem found by one scan. <see cref="Subject"/> identifies what it is about across scans.</summary>
public sealed record AlertFinding(AlertKind Kind, int FolderId, Guid? DeviceId, string FolderName, string? DeviceName, string Detail)
{
    public string Subject => AlertRules.SubjectOf(Kind, FolderId, DeviceId);
}

/// <summary>
/// What one scan of a person's folders found: the problems, and every subject that is watched at all (so an open
/// incident whose subject is gone — device detached, paused or revoked, folder no longer a Backup — is closed
/// quietly rather than announced as "back to normal").
/// </summary>
public sealed record AlertScan(List<AlertFinding> Findings, HashSet<string> Watched);

/// <summary>
/// Overdue and failure detection. Pure: it reads what devices and maintenance runs already reported
/// (<see cref="FolderDevice"/> and <see cref="Folder"/> fields) and needs no push channel.
/// <list type="bullet">
/// <item><b>Failing</b>: the last run failed and either <see cref="AlertPolicy.FailureThreshold"/> runs in a row failed or
///   the failures started longer than the grace ago.</item>
/// <item><b>Overdue</b>: no successful run for longer than the grace, counted from the last success or from attaching or
///   resuming the device, whichever is later. Paused and revoked devices are not watched.</item>
/// <item><b>CheckFailed / PruneFailed</b>: a Backup folder's last check or prune failed.</item>
/// </list>
/// Time the server was not running does not count towards opening a new Overdue incident (<c>watchingSince</c>):
/// devices cannot sync while it is down, so a restart must not raise a wave of alerts. Open incidents stay open.
/// </summary>
public static class AlertRules
{
    public static string SubjectOf(AlertKind kind, int folderId, Guid? deviceId) => kind switch
    {
        AlertKind.Overdue or AlertKind.Failing => $"device:{folderId}:{deviceId:N}",
        AlertKind.CheckFailed => $"check:{folderId}",
        _ => $"prune:{folderId}",
    };

    public static string SubjectOf(AlertIncident incident) => SubjectOf(incident.Kind, incident.FolderId, incident.DeviceId);

    /// <param name="folders">One person's folders, with <c>Devices</c> and each membership's <c>Device</c> loaded.</param>
    /// <param name="openSubjects">Subjects with an open incident.</param>
    public static AlertScan Scan(IEnumerable<Folder> folders, AlertPolicy policy, DateTime now, DateTime watchingSince, IReadOnlySet<string> openSubjects)
    {
        var findings = new List<AlertFinding>();
        var watched = new HashSet<string>();
        foreach (var folder in folders)
        {
            var grace = policy.GraceFor(folder);
            foreach (var member in folder.Devices)
            {
                if (member.Paused || member.Device is null || member.Device.RevokedAt is not null) continue;
                var subject = SubjectOf(AlertKind.Overdue, folder.Id, member.DeviceId);
                watched.Add(subject);
                if (Check(folder, member, policy, grace, now, watchingSince, openSubjects.Contains(subject)) is { } finding)
                    findings.Add(finding);
            }

            if (folder.Mode != SyncMode.Backup) continue;
            watched.Add(SubjectOf(AlertKind.CheckFailed, folder.Id, null));
            watched.Add(SubjectOf(AlertKind.PruneFailed, folder.Id, null));
            if (folder.LastCheckOk == false)
                findings.Add(new AlertFinding(AlertKind.CheckFailed, folder.Id, null, folder.Name, null,
                    $"The integrity check{By(folder.LastCheckBy)} {When(folder.LastCheckAt)} found problems: {folder.LastCheckMessage ?? "no details"}"));
            if (folder.LastPruneOk == false)
                findings.Add(new AlertFinding(AlertKind.PruneFailed, folder.Id, null, folder.Name, null,
                    $"Freeing space{By(folder.LastPruneBy)} {When(folder.LastPruneAt)} failed: {folder.LastPruneMessage ?? "no details"}"));
        }
        return new AlertScan(findings, watched);
    }

    /// <summary>The problem with one device's membership, if any.</summary>
    public static AlertFinding? Check(Folder folder, FolderDevice member, AlertPolicy policy, TimeSpan grace, DateTime now, DateTime watchingSince, bool alreadyOpen)
    {
        var deviceName = member.Device?.Name ?? "a device";
        if (member.LastSyncOk == false)
        {
            var since = member.FailingSince ?? member.LastSyncAt ?? now;
            if (member.ConsecutiveFailures >= Math.Max(1, policy.FailureThreshold) || now - since >= grace)
                return new AlertFinding(AlertKind.Failing, folder.Id, member.DeviceId, folder.Name, deviceName,
                    $"{Runs(member.ConsecutiveFailures)} failed since {Format(since)}: {member.LastSyncMessage ?? "no details"}");
        }

        var lastOk = member.LastSuccessAt ?? (member.LastSyncOk == true ? member.LastSyncAt : null);
        var reference = lastOk is { } success && success > member.AttachedAt ? success : member.AttachedAt;
        if (!alreadyOpen && watchingSince > reference) reference = watchingSince;
        if (now - reference < grace) return null;
        var detail = lastOk is { } ok
            ? $"No successful run since {Format(ok)} (expected at least every {Age(grace)})."
            : $"No successful run since it was attached {Format(member.AttachedAt)} (expected within {Age(grace)}).";
        if (lastOk is { } last && member.AttachedAt > last)
            detail = $"No successful run since it was resumed {Format(member.AttachedAt)} (expected within {Age(grace)}).";
        return new AlertFinding(AlertKind.Overdue, folder.Id, member.DeviceId, folder.Name, deviceName, detail);
    }

    private static string By(string? by) => by is null ? "" : $" by {by}";
    private static string When(DateTime? at) => at is { } t ? $"on {Format(t)}" : "";
    private static string Runs(int n) => n == 1 ? "1 run" : $"{n} runs in a row";

    public static string Format(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm 'UTC'");

    /// <summary>"3 days", "5 h", "12 min", "40 s".</summary>
    public static string Age(TimeSpan span) =>
        span.TotalDays >= 2 ? $"{(int)span.TotalDays} days"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min"
        : $"{Math.Max(0, (int)span.TotalSeconds)} s";
}

/// <summary>What <see cref="IncidentTracker.Apply"/> changed.</summary>
public sealed record IncidentChanges(List<AlertIncident> Opened, List<AlertIncident> Updated, List<AlertIncident> Resolved);

/// <summary>
/// Incident state transitions: open once per subject, update while open (an Overdue device that starts failing
/// becomes Failing without a second alert), resolve when the problem is gone. Pure, so tests drive it with a fake clock.
/// </summary>
public static class IncidentTracker
{
    public static IncidentChanges Apply(int userId, IReadOnlyCollection<AlertIncident> open, AlertScan scan, DateTime now)
    {
        var changes = new IncidentChanges([], [], []);
        var bySubject = open.Where(i => i.ResolvedAt is null).ToDictionary(AlertRules.SubjectOf);
        var found = new HashSet<string>();
        foreach (var finding in scan.Findings)
        {
            if (!found.Add(finding.Subject)) continue;
            if (bySubject.TryGetValue(finding.Subject, out var incident))
            {
                if (incident.Kind != finding.Kind || incident.Detail != finding.Detail || incident.FolderName != finding.FolderName
                    || incident.DeviceName != finding.DeviceName)
                {
                    incident.Kind = finding.Kind;
                    incident.Detail = finding.Detail;
                    incident.FolderName = finding.FolderName;
                    incident.DeviceName = finding.DeviceName;
                    incident.UpdatedAt = now;
                    changes.Updated.Add(incident);
                }
                continue;
            }
            changes.Opened.Add(new AlertIncident
            {
                UserId = userId, FolderId = finding.FolderId, DeviceId = finding.DeviceId, Kind = finding.Kind,
                FolderName = finding.FolderName, DeviceName = finding.DeviceName, Detail = Trim(finding.Detail),
                OpenedAt = now, UpdatedAt = now,
            });
        }

        foreach (var (subject, incident) in bySubject)
        {
            if (found.Contains(subject)) continue;
            incident.ResolvedAt = now;
            incident.UpdatedAt = now;
            // Gone rather than fixed, or never announced (it lasted less than one scan): nothing to tell.
            if (!scan.Watched.Contains(subject) || incident.NotifiedAt is null)
            {
                incident.NotifiedAt ??= now;
                incident.ResolvedNotifiedAt = now;
            }
            changes.Resolved.Add(incident);
        }
        foreach (var incident in changes.Updated) incident.Detail = Trim(incident.Detail);
        return changes;
    }

    private static string Trim(string detail) => detail.Length > 1000 ? detail[..1000] : detail;
}

/// <summary>One message to a person: what is new, what is back to normal, and everything still open.</summary>
public sealed record AlertNotice(List<AlertIncident> Opened, List<AlertIncident> Resolved, List<AlertIncident> StillOpen, bool Reminder)
{
    public bool IsEmpty => Opened.Count == 0 && Resolved.Count == 0 && !Reminder;
}

/// <summary>
/// Decides what to send a person after a scan, so there are no alert storms: one message covers every folder; each
/// problem is announced once and its recovery once; open problems are repeated only every
/// <see cref="AlertPolicy.Reminder"/> (counted from the last message); and after a delivery that reached no channel,
/// the next try waits <c>retryAfter</c>.
/// </summary>
public static class AlertDispatch
{
    public static AlertNotice? Compose(IReadOnlyCollection<AlertIncident> incidents, AlertSettings state, AlertPolicy policy, DateTime now, TimeSpan retryAfter)
    {
        var open = incidents.Where(i => i.ResolvedAt is null).OrderBy(i => i.OpenedAt).ThenBy(i => i.Id).ToList();
        var opened = open.Where(i => i.NotifiedAt is null).ToList();
        var resolved = incidents.Where(i => i.ResolvedAt is not null && i.NotifiedAt is not null && i.ResolvedNotifiedAt is null)
            .OrderBy(i => i.ResolvedAt).ToList();
        var reminder = opened.Count == 0 && resolved.Count == 0 && open.Count > 0 && policy.Reminder > TimeSpan.Zero
                       && (state.LastSentAt is not { } last || now - last >= policy.Reminder);
        if (opened.Count == 0 && resolved.Count == 0 && !reminder) return null;
        if (state.LastFailedAttemptAt is { } failed && now - failed < retryAfter) return null;
        return new AlertNotice(opened, resolved, open, reminder);
    }

    /// <summary>Records that <paramref name="notice"/> was delivered (or that there was nowhere to deliver it).</summary>
    public static void MarkSent(AlertNotice notice, AlertSettings state, DateTime now)
    {
        foreach (var i in notice.Opened) i.NotifiedAt = now;
        foreach (var i in notice.Resolved) i.ResolvedNotifiedAt = now;
        state.LastSentAt = now;
        state.LastFailedAttemptAt = null;
    }

    public static void MarkFailed(AlertSettings state, DateTime now) => state.LastFailedAttemptAt = now;
}
