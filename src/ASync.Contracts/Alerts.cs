using System.Text.Json.Serialization;

namespace ASync.Contracts;

/// <summary>What an alert incident is about.</summary>
public enum AlertKind
{
    /// <summary>A device has not synced a folder successfully for longer than the grace period.</summary>
    Overdue,
    /// <summary>A device's runs of a folder keep failing.</summary>
    Failing,
    /// <summary>A Backup folder's last integrity check found problems.</summary>
    CheckFailed,
    /// <summary>A Backup folder's last prune failed.</summary>
    PruneFailed,
}

public enum AlertChannelType { Webhook, Ntfy, Email }

/// <summary>
/// Where a person's alerts go. Secrets (the webhook's Authorization header, the ntfy token) are write-only, as
/// for storage settings: responses carry them as null with <see cref="AlertChannelDto.SecretsSet"/> listing which
/// are stored, and an update that leaves a secret null keeps the stored one.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WebhookChannelSettings), nameof(AlertChannelType.Webhook))]
[JsonDerivedType(typeof(NtfyChannelSettings), nameof(AlertChannelType.Ntfy))]
[JsonDerivedType(typeof(EmailChannelSettings), nameof(AlertChannelType.Email))]
public abstract record AlertChannelSettings
{
    [JsonIgnore] public abstract AlertChannelType Type { get; }
    public abstract string[] SetSecrets();
    public abstract AlertChannelSettings WithoutSecrets();
    public abstract AlertChannelSettings WithSecretsFrom(AlertChannelSettings stored);
    public abstract string Describe();
}

/// <summary>A JSON POST of an <see cref="AlertPayload"/> to <see cref="Url"/>, with an optional <c>Authorization</c> header value.</summary>
public sealed record WebhookChannelSettings(string Url, string? Authorization = null) : AlertChannelSettings
{
    [JsonIgnore] public override AlertChannelType Type => AlertChannelType.Webhook;
    public override string[] SetSecrets() => Authorization is null ? [] : [nameof(Authorization)];
    public override AlertChannelSettings WithoutSecrets() => this with { Authorization = null };
    public override AlertChannelSettings WithSecretsFrom(AlertChannelSettings stored) =>
        stored is WebhookChannelSettings s ? this with { Authorization = Authorization ?? s.Authorization } : this;
    public override string Describe() => Url;
}

/// <summary>An ntfy topic (ntfy.sh or a self-hosted server). <see cref="Token"/>: an access token for protected topics.</summary>
public sealed record NtfyChannelSettings(string ServerUrl, string Topic, string? Token = null) : AlertChannelSettings
{
    [JsonIgnore] public override AlertChannelType Type => AlertChannelType.Ntfy;
    public override string[] SetSecrets() => Token is null ? [] : [nameof(Token)];
    public override AlertChannelSettings WithoutSecrets() => this with { Token = null };
    public override AlertChannelSettings WithSecretsFrom(AlertChannelSettings stored) =>
        stored is NtfyChannelSettings s ? this with { Token = Token ?? s.Token } : this;
    public override string Describe() => $"{ServerUrl.TrimEnd('/')}/{Topic}";
}

/// <summary>An e-mail address, sent through the server's SMTP settings (<c>Smtp__*</c>).</summary>
public sealed record EmailChannelSettings(string Address) : AlertChannelSettings
{
    [JsonIgnore] public override AlertChannelType Type => AlertChannelType.Email;
    public override string[] SetSecrets() => [];
    public override AlertChannelSettings WithoutSecrets() => this;
    public override AlertChannelSettings WithSecretsFrom(AlertChannelSettings stored) => this;
    public override string Describe() => Address;
}

// ── API ───────────────────────────────────────────────────────────────────────

public record AlertChannelDto(
    int Id, string Name, AlertChannelType Type, bool Enabled, AlertChannelSettings Settings, string[] SecretsSet, string Description,
    DateTime CreatedAt, DateTime? LastSentAt, bool? LastOk, string? LastError);

public record SaveAlertChannelRequest(string Name, AlertChannelSettings Settings, bool Enabled = true);

/// <summary>Sends a test message with these settings; with <paramref name="ChannelId"/>, empty secrets come from that channel.</summary>
public record AlertTestRequest(AlertChannelSettings Settings, int? ChannelId = null);

public record AlertTestResultDto(bool Ok, string? Error);

/// <summary>
/// A person's alert rules. Null = the server's default (in <see cref="AlertSettingsDto.Defaults"/>).
/// A device is overdue after max(<paramref name="MinimumGraceHours"/>, folder interval × <paramref name="GraceFactor"/>)
/// without a successful run; failing after <paramref name="FailureThreshold"/> failed runs in a row (or failing for
/// longer than the grace). <paramref name="ReminderHours"/>: repeat open problems this often (0 = never).
/// </summary>
public record SaveAlertSettingsRequest(double? GraceFactor = null, double? MinimumGraceHours = null, int? FailureThreshold = null, int? ReminderHours = null);

public record AlertDefaultsDto(double GraceFactor, double MinimumGraceHours, int FailureThreshold, int ReminderHours);

/// <summary><paramref name="EmailAvailable"/>: the server has SMTP settings. <paramref name="PushMode"/>: the server's <c>Push__Mode</c>.</summary>
public record AlertSettingsDto(
    double? GraceFactor, double? MinimumGraceHours, int? FailureThreshold, int? ReminderHours, AlertDefaultsDto Defaults,
    bool EmailAvailable, string PushMode);

public record AlertIncidentDto(
    int Id, AlertKind Kind, int FolderId, string FolderName, Guid? DeviceId, string? DeviceName, string Detail,
    DateTime OpenedAt, DateTime? ResolvedAt, DateTime? NotifiedAt);

// ── What webhooks receive ─────────────────────────────────────────────────────

/// <summary>
/// The JSON body of a webhook alert (documented in README "Alerts"). <paramref name="Event"/> is <c>alert</c> or
/// <c>test</c>. <paramref name="Opened"/>: problems found since the last message; <paramref name="Resolved"/>: back
/// to normal; <paramref name="Open"/>: everything still open, new ones included. <paramref name="Reminder"/>: sent
/// only to repeat what is still open.
/// </summary>
public record AlertPayload(
    int Version, string Event, string Title, string Text, string? Server, string User, DateTime SentAt, bool Reminder,
    AlertPayloadIncident[] Opened, AlertPayloadIncident[] Resolved, AlertPayloadIncident[] Open);

/// <summary><paramref name="Kind"/>: <c>overdue</c>, <c>failing</c>, <c>checkFailed</c> or <c>pruneFailed</c>.</summary>
public record AlertPayloadIncident(
    int Id, string Kind, int FolderId, string FolderName, Guid? DeviceId, string? DeviceName, string Detail, DateTime OpenedAt, DateTime? ResolvedAt);
