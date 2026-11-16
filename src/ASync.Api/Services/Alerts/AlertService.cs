using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ASync.Api.Services.Push;
using ASync.Contracts;
using ASync.Core.Alerts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using Microsoft.AspNetCore.DataProtection;

namespace ASync.Api.Services.Alerts;

/// <summary>The server's alert defaults (<c>Alerts__*</c>); people override them on the Alerts page.</summary>
public sealed record AlertOptions(
    bool Enabled, TimeSpan Poll, double GraceFactor, TimeSpan MinimumGrace, int FailureThreshold, int ReminderHours, TimeSpan RetryAfter,
    int KeepResolvedDays)
{
    public static AlertOptions FromConfiguration(IConfiguration c) => new(
        c.GetValue("Alerts:Enabled", true),
        TimeSpan.FromSeconds(Math.Max(1, c.GetValue("Alerts:PollSeconds", 300))),
        Math.Max(0, c.GetValue("Alerts:GraceFactor", 3.0)),
        TimeSpan.FromMinutes(Math.Max(0, c.GetValue("Alerts:MinimumGraceMinutes", 240.0))),
        Math.Max(1, c.GetValue("Alerts:FailureThreshold", 3)),
        Math.Max(0, c.GetValue("Alerts:ReminderHours", 24)),
        TimeSpan.FromMinutes(Math.Max(0, c.GetValue("Alerts:RetryMinutes", 15.0))),
        Math.Max(1, c.GetValue("Alerts:KeepResolvedDays", 30)));

    public AlertDefaultsDto ToDto() => new(GraceFactor, MinimumGrace.TotalHours, FailureThreshold, ReminderHours);
}

/// <summary>When this server process started: time before it does not count towards new Overdue incidents.</summary>
public sealed record ServerStarted(DateTime At);

/// <summary>
/// A person's alert channels and rules. Channel settings, secrets included, are protected with Data Protection
/// (purpose <see cref="SettingsPurpose"/>), and secrets are never returned — as for storage back-ends.
/// </summary>
public class AlertService(
    IAlertRepository alerts, IUserRepository users, IAlertSender sender, IDataProtectionProvider dataProtection, AlertOptions options,
    SmtpOptions smtp, PushSettings push, IConfiguration configuration, TimeProvider clock)
{
    public const string SettingsPurpose = "ASync.AlertChannel.Settings.v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        AllowOutOfOrderMetadataProperties = true,
    };

    private IDataProtector Protector => dataProtection.CreateProtector(SettingsPurpose);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>The configured <c>PublicUrl</c>, for links in messages (background work has no request to take it from).</summary>
    public string? ServerUrl => configuration["PublicUrl"] is { Length: > 0 } url ? url.TrimEnd('/') : null;

    // ── Rules ─────────────────────────────────────────────────────────────────

    public AlertPolicy PolicyFor(AlertSettings? s) => new(
        s?.GraceFactor ?? options.GraceFactor,
        s?.MinimumGraceHours is double hours ? TimeSpan.FromHours(hours) : options.MinimumGrace,
        s?.FailureThreshold ?? options.FailureThreshold,
        TimeSpan.FromHours(s?.ReminderHours ?? options.ReminderHours));

    public AlertSettingsDto ToDto(AlertSettings? s) => new(
        s?.GraceFactor, s?.MinimumGraceHours, s?.FailureThreshold, s?.ReminderHours, options.ToDto(), smtp.Configured, push.Mode.ToString());

    public async Task<ServiceResult<AlertSettings>> SaveSettingsAsync(int userId, SaveAlertSettingsRequest req)
    {
        if (req.GraceFactor is < 1 or > 100) return ServiceResult<AlertSettings>.Fail("The grace factor must be between 1 and 100.");
        if (req.MinimumGraceHours is < 0.25 or > 720) return ServiceResult<AlertSettings>.Fail("The minimum grace must be between 0.25 and 720 hours.");
        if (req.FailureThreshold is < 1 or > 100) return ServiceResult<AlertSettings>.Fail("The number of failed runs must be between 1 and 100.");
        if (req.ReminderHours is < 0 or > 720) return ServiceResult<AlertSettings>.Fail("Reminders must be 0 (off) to 720 hours apart.");
        var settings = await alerts.GetOrAddSettingsAsync(userId);
        (settings.GraceFactor, settings.MinimumGraceHours, settings.FailureThreshold, settings.ReminderHours) =
            (req.GraceFactor, req.MinimumGraceHours, req.FailureThreshold, req.ReminderHours);
        await alerts.SaveAsync();
        return ServiceResult<AlertSettings>.Ok(settings);
    }

    // ── Channels ──────────────────────────────────────────────────────────────

    public AlertChannelSettings Settings(AlertChannel channel) =>
        JsonSerializer.Deserialize<AlertChannelSettings>(Protector.Unprotect(channel.ProtectedSettings), Json)
        ?? throw new InvalidOperationException($"Alert channel {channel.Id} has no settings.");

    private string Protect(AlertChannelSettings settings) => Protector.Protect(JsonSerializer.Serialize(settings, Json));

    public AlertChannelDto ToDto(AlertChannel c)
    {
        var settings = Settings(c);
        return new AlertChannelDto(c.Id, c.Name, c.Type, c.Enabled, settings.WithoutSecrets(), settings.SetSecrets(), settings.Describe(), c.CreatedAt,
            c.LastSentAt, c.LastOk, c.LastError);
    }

    public async Task<ServiceResult<AlertChannel>> CreateChannelAsync(int userId, SaveAlertChannelRequest req)
    {
        if (Validate(req.Name, req.Settings) is { } invalid) return ServiceResult<AlertChannel>.Fail(invalid);
        var channel = await alerts.AddChannelAsync(new AlertChannel
        {
            UserId = userId, Name = req.Name.Trim(), Type = req.Settings.Type, Enabled = req.Enabled,
            ProtectedSettings = Protect(Normalize(req.Settings)), CreatedAt = Now, UpdatedAt = Now,
        });
        return ServiceResult<AlertChannel>.Ok(channel);
    }

    public async Task<ServiceResult<AlertChannel>> UpdateChannelAsync(AlertChannel channel, SaveAlertChannelRequest req)
    {
        if (req.Settings is not null && req.Settings.Type != channel.Type)
            return ServiceResult<AlertChannel>.Fail("A channel's type cannot change. Add a new channel instead.");
        if (Validate(req.Name, req.Settings) is { } invalid) return ServiceResult<AlertChannel>.Fail(invalid);
        channel.Name = req.Name.Trim();
        channel.Enabled = req.Enabled;
        channel.ProtectedSettings = Protect(Normalize(req.Settings!.WithSecretsFrom(Settings(channel))));
        channel.UpdatedAt = Now;
        (channel.LastSentAt, channel.LastOk, channel.LastError) = (null, null, null);
        await alerts.SaveAsync();
        return ServiceResult<AlertChannel>.Ok(channel);
    }

    public Task DeleteChannelAsync(AlertChannel channel) => alerts.DeleteChannelAsync(channel);

    /// <summary>Sends a test message with <paramref name="settings"/> (secrets left empty come from <paramref name="existing"/>).</summary>
    public async Task<ServiceResult<AlertTestResultDto>> TestAsync(int userId, AlertChannelSettings settings, AlertChannel? existing, CancellationToken ct)
    {
        if (existing is not null) settings = settings.WithSecretsFrom(Settings(existing));
        if (Validate("test", settings) is { } invalid) return ServiceResult<AlertTestResultDto>.Fail(invalid);
        var user = await users.GetByIdAsync(userId);
        var error = await sender.SendAsync(Normalize(settings), AlertMessages.Test(user?.Username ?? "you", ServerUrl, Now), ct);
        if (existing is not null)
        {
            Record(existing, error);
            await alerts.SaveAsync();
        }
        return ServiceResult<AlertTestResultDto>.Ok(new AlertTestResultDto(error is null, error));
    }

    /// <summary>Stores a delivery's outcome on the channel (saved by the caller).</summary>
    public void Record(AlertChannel channel, string? error)
    {
        channel.LastSentAt = Now;
        channel.LastOk = error is null;
        channel.LastError = error;
    }

    private static readonly Regex NtfyTopic = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    private string? Validate(string? name, AlertChannelSettings? settings)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 100) return "A name of 1–100 characters is required.";
        return settings switch
        {
            null => "Settings are required.",
            WebhookChannelSettings w => ValidateUrl(w.Url, "webhook address") ?? (w.Authorization is { Length: > 2000 } ? "The Authorization header is too long." : null),
            NtfyChannelSettings n => ValidateUrl(n.ServerUrl, "ntfy server") ?? (!NtfyTopic.IsMatch(n.Topic?.Trim() ?? "")
                ? "The ntfy topic may only contain letters, digits, - and _ (up to 64)." : null),
            EmailChannelSettings e => !MimeKit.MailboxAddress.TryParse(e.Address?.Trim() ?? "", out var box) || !box.Address.Contains('@')
                ? "Enter one e-mail address." : !smtp.Configured ? "This server has no SMTP settings, so it cannot send e-mail. Ask its admin to set Smtp__Host and Smtp__From." : null,
            _ => "Unknown channel type.",
        };
    }

    /// <summary>Only absolute http(s) URLs: no file:, ftp: or other schemes (see <see cref="AlertSender"/> about SSRF).</summary>
    public static string? ValidateUrl(string? url, string what) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host)
            ? null
            : $"The {what} must be an http:// or https:// URL.";

    private static AlertChannelSettings Normalize(AlertChannelSettings s) => s switch
    {
        WebhookChannelSettings w => w with { Url = w.Url.Trim(), Authorization = string.IsNullOrWhiteSpace(w.Authorization) ? null : w.Authorization.Trim() },
        NtfyChannelSettings n => n with { ServerUrl = n.ServerUrl.Trim().TrimEnd('/'), Topic = n.Topic.Trim(), Token = string.IsNullOrWhiteSpace(n.Token) ? null : n.Token.Trim() },
        EmailChannelSettings e => e with { Address = e.Address.Trim() },
        _ => s,
    };

    // ── Incidents ─────────────────────────────────────────────────────────────

    public static AlertIncidentDto ToDto(AlertIncident i) =>
        new(i.Id, i.Kind, i.FolderId, i.FolderName, i.DeviceId, i.DeviceName, i.Detail, i.OpenedAt, i.ResolvedAt, i.NotifiedAt,
            i.StorageBackendId, i.StorageBackendName);
}
