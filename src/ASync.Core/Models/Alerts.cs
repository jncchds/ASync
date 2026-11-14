using ASync.Contracts;

namespace ASync.Core.Models;

/// <summary>
/// One problem the server found (see <c>Alerts/AlertRules</c>): a device overdue or failing on a folder, or a Backup
/// folder whose check or prune failed. Open while <see cref="ResolvedAt"/> is null. At most one incident is open per
/// subject (folder + device for Overdue/Failing, which may turn into one another while open; folder + kind for the
/// others), so a problem is announced once, not on every scan.
/// </summary>
public class AlertIncident
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int FolderId { get; set; }
    /// <summary>Null for folder problems (check, prune).</summary>
    public Guid? DeviceId { get; set; }
    public AlertKind Kind { get; set; }
    /// <summary>Names when the incident was last updated, so messages read well after a rename or removal.</summary>
    public string FolderName { get; set; } = "";
    public string? DeviceName { get; set; }
    public string Detail { get; set; } = "";
    public DateTime OpenedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    /// <summary>When a message announced it (or it was settled without one, e.g. no channel). Null = still to announce.</summary>
    public DateTime? NotifiedAt { get; set; }
    /// <summary>When the "back to normal" message went out (or was skipped). Null on a resolved incident = still to send.</summary>
    public DateTime? ResolvedNotifiedAt { get; set; }

    public Folder? Folder { get; set; }
}

/// <summary>Where a person's alerts go. The settings, secrets included, are protected with Data Protection like storage settings.</summary>
public class AlertChannel
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public AlertChannelType Type { get; set; }
    public bool Enabled { get; set; } = true;
    public string ProtectedSettings { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastSentAt { get; set; }
    public bool? LastOk { get; set; }
    public string? LastError { get; set; }
}

/// <summary>A person's alert rules (null = the server's default) and when they were last messaged. One row per user, made on demand.</summary>
public class AlertSettings
{
    public int UserId { get; set; }
    public double? GraceFactor { get; set; }
    public double? MinimumGraceHours { get; set; }
    public int? FailureThreshold { get; set; }
    public int? ReminderHours { get; set; }
    /// <summary>The last alert message delivered (or settled): reminders count from here.</summary>
    public DateTime? LastSentAt { get; set; }
    /// <summary>The last delivery attempt that reached no channel: retries wait a while after it.</summary>
    public DateTime? LastFailedAttemptAt { get; set; }
}
