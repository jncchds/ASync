using System.Text.Json;
using System.Text.Json.Nodes;
using ASync.Api.Services.Alerts;
using ASync.Contracts;
using ASync.Core.Alerts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASync.Tests;

/// <summary>Overdue and failure detection, incident state, message composition and formats, and one monitor pass end to end (with fakes).</summary>
public class AlertTests
{
    private static readonly DateTime T0 = new(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LongAgo = T0.AddDays(-30);
    private static readonly AlertPolicy Policy = new(GraceFactor: 3, MinimumGrace: TimeSpan.FromHours(4), FailureThreshold: 3, Reminder: TimeSpan.FromHours(24));
    private static readonly HashSet<string> NoneOpen = [];

    private static (Folder Folder, FolderDevice Member) NewFolder(int id = 1, int intervalMinutes = 60, SyncMode mode = SyncMode.TwoWay, string device = "laptop")
    {
        var folder = new Folder { Id = id, UserId = 1, Name = $"folder{id}", Mode = mode, IntervalMinutes = intervalMinutes };
        var member = new FolderDevice
        {
            Id = id, FolderId = id, Folder = folder, DeviceId = Guid.CreateVersion7(), AttachedAt = LongAgo,
            Device = new Device { Name = device, UserId = 1 },
        };
        member.Device.Id = member.DeviceId;
        folder.Devices.Add(member);
        return (folder, member);
    }

    private static void Succeeded(FolderDevice m, DateTime at)
    {
        (m.LastSyncAt, m.LastSyncOk, m.LastSuccessAt, m.ConsecutiveFailures, m.FailingSince) = (at, true, at, 0, null);
    }

    private static void Failed(FolderDevice m, DateTime at, string message = "disk full")
    {
        (m.LastSyncAt, m.LastSyncOk, m.LastSyncMessage) = (at, false, message);
        m.ConsecutiveFailures++;
        m.FailingSince ??= at;
    }

    private static List<AlertFinding> Scan(DateTime now, params Folder[] folders) =>
        AlertRules.Scan(folders, Policy, now, LongAgo, NoneOpen).Findings;

    // ── Detection rules ───────────────────────────────────────────────────────

    [Fact]
    public void A_device_is_overdue_once_the_grace_has_passed_since_its_last_success()
    {
        var (folder, member) = NewFolder(intervalMinutes: 60); // 3 × 60 min < 4 h minimum → 4 h
        Succeeded(member, T0);
        Assert.Empty(Scan(T0.AddHours(4).AddSeconds(-1), folder));
        var finding = Assert.Single(Scan(T0.AddHours(4), folder));
        Assert.Equal(AlertKind.Overdue, finding.Kind);
        Assert.Equal(member.DeviceId, finding.DeviceId);
        Assert.Contains("2026-11-14 08:00 UTC", finding.Detail);
    }

    [Fact]
    public void The_grace_scales_with_the_folder_interval()
    {
        var (folder, member) = NewFolder(intervalMinutes: 24 * 60); // daily → 72 h
        Succeeded(member, T0);
        Assert.Equal(TimeSpan.FromHours(72), Policy.GraceFor(folder));
        Assert.Empty(Scan(T0.AddHours(71), folder));
        Assert.Single(Scan(T0.AddHours(72), folder));
    }

    [Fact]
    public void A_device_that_never_synced_counts_from_when_it_was_attached()
    {
        var (folder, member) = NewFolder();
        member.AttachedAt = T0;
        Assert.Empty(Scan(T0.AddHours(3), folder));
        var finding = Assert.Single(Scan(T0.AddHours(5), folder));
        Assert.Contains("since it was attached", finding.Detail);
    }

    [Fact]
    public void A_resumed_device_counts_from_resuming_not_from_its_old_last_success()
    {
        var (folder, member) = NewFolder();
        Succeeded(member, T0.AddDays(-10));
        member.AttachedAt = T0; // resumed now
        Assert.Empty(Scan(T0.AddHours(1), folder));
        Assert.Contains("resumed", Assert.Single(Scan(T0.AddHours(4), folder)).Detail);
    }

    [Fact]
    public void Paused_and_revoked_devices_are_not_watched()
    {
        var (paused, p) = NewFolder(1);
        p.Paused = true;
        var (revoked, r) = NewFolder(2);
        r.Device!.RevokedAt = T0.AddDays(-1);
        var scan = AlertRules.Scan([paused, revoked], Policy, T0, LongAgo, NoneOpen);
        Assert.Empty(scan.Findings);
        Assert.DoesNotContain(AlertRules.SubjectOf(AlertKind.Overdue, 1, p.DeviceId), scan.Watched);
    }

    [Fact]
    public void Failing_after_the_threshold_of_failed_runs_in_a_row()
    {
        var (folder, member) = NewFolder();
        Succeeded(member, T0);
        Failed(member, T0.AddMinutes(10));
        Failed(member, T0.AddMinutes(20));
        Assert.Empty(Scan(T0.AddMinutes(25), folder));
        Failed(member, T0.AddMinutes(30));
        var finding = Assert.Single(Scan(T0.AddMinutes(31), folder));
        Assert.Equal(AlertKind.Failing, finding.Kind);
        Assert.Contains("3 runs in a row", finding.Detail);
        Assert.Contains("disk full", finding.Detail);
    }

    [Fact]
    public void Failing_when_one_failure_lasts_longer_than_the_grace()
    {
        var (folder, member) = NewFolder(intervalMinutes: 24 * 60);
        Succeeded(member, T0);
        Failed(member, T0.AddHours(1)); // a daily folder: one run a day, so the threshold of 3 takes 3 days
        Assert.Empty(Scan(T0.AddHours(70), folder));
        Assert.Equal(AlertKind.Failing, Assert.Single(Scan(T0.AddHours(73), folder)).Kind);
    }

    [Fact]
    public void Failed_checks_and_prunes_of_Backup_folders_are_problems_too()
    {
        var (folder, member) = NewFolder(mode: SyncMode.Backup);
        Succeeded(member, T0);
        (folder.LastCheckAt, folder.LastCheckOk, folder.LastCheckMessage, folder.LastCheckBy) = (T0, false, "2 problems", "server");
        (folder.LastPruneAt, folder.LastPruneOk, folder.LastPruneMessage) = (T0, false, "storage offline");
        var findings = Scan(T0.AddMinutes(1), folder);
        Assert.Equal([AlertKind.CheckFailed, AlertKind.PruneFailed], findings.Select(f => f.Kind));
        Assert.All(findings, f => Assert.Null(f.DeviceId));
        Assert.Contains("2 problems", findings[0].Detail);
        folder.LastCheckOk = true;
        Assert.Equal(AlertKind.PruneFailed, Assert.Single(Scan(T0.AddMinutes(2), folder)).Kind);
    }

    [Fact]
    public void Server_downtime_does_not_open_new_overdue_incidents_but_open_ones_stay()
    {
        var (folder, member) = NewFolder();
        Succeeded(member, T0.AddDays(-2));
        var started = T0.AddMinutes(-5); // the server was down until 5 minutes ago
        Assert.Empty(AlertRules.Scan([folder], Policy, T0, started, NoneOpen).Findings);
        Assert.Single(AlertRules.Scan([folder], Policy, T0.AddHours(4), started, NoneOpen).Findings);
        var open = new HashSet<string> { AlertRules.SubjectOf(AlertKind.Overdue, folder.Id, member.DeviceId) };
        Assert.Single(AlertRules.Scan([folder], Policy, T0, started, open).Findings);
    }

    // ── Incident state ────────────────────────────────────────────────────────

    private static AlertScan ScanFor(DateTime now, List<AlertIncident> incidents, params Folder[] folders) =>
        AlertRules.Scan(folders, Policy, now, LongAgo, incidents.Where(i => i.ResolvedAt is null).Select(AlertRules.SubjectOf).ToHashSet());

    private static IncidentChanges Step(List<AlertIncident> incidents, DateTime now, params Folder[] folders)
    {
        var changes = IncidentTracker.Apply(1, incidents, ScanFor(now, incidents, folders), now);
        foreach (var i in changes.Opened) i.Id = incidents.Count + 1;
        incidents.AddRange(changes.Opened);
        return changes;
    }

    [Fact]
    public void An_incident_opens_once_and_turns_from_overdue_into_failing_without_a_second_incident()
    {
        var (folder, member) = NewFolder();
        Succeeded(member, T0);
        var incidents = new List<AlertIncident>();
        Assert.Single(Step(incidents, T0.AddHours(5), folder).Opened);
        Assert.Empty(Step(incidents, T0.AddHours(6), folder).Opened);
        Failed(member, T0.AddHours(6));
        Failed(member, T0.AddHours(6));
        Failed(member, T0.AddHours(6));
        var changes = Step(incidents, T0.AddHours(7), folder);
        Assert.Empty(changes.Opened);
        Assert.Equal(AlertKind.Failing, Assert.Single(changes.Updated).Kind);
        Assert.Single(incidents);
    }

    [Fact]
    public void A_fixed_problem_resolves_and_waits_for_its_back_to_normal_message()
    {
        var (folder, member) = NewFolder();
        Succeeded(member, T0);
        var incidents = new List<AlertIncident>();
        var opened = Assert.Single(Step(incidents, T0.AddHours(5), folder).Opened);
        opened.NotifiedAt = T0.AddHours(5);
        Succeeded(member, T0.AddHours(6));
        var resolved = Assert.Single(Step(incidents, T0.AddHours(6), folder).Resolved);
        Assert.Same(opened, resolved);
        Assert.Equal(T0.AddHours(6), resolved.ResolvedAt);
        Assert.Null(resolved.ResolvedNotifiedAt); // the recovery is still to announce
        Assert.Empty(Step(incidents, T0.AddHours(7), folder).Opened);
    }

    [Fact]
    public void A_problem_that_goes_away_unannounced_or_whose_device_is_removed_closes_quietly()
    {
        var (folder, member) = NewFolder();
        Succeeded(member, T0);
        var incidents = new List<AlertIncident>();
        Step(incidents, T0.AddHours(5), folder);
        Succeeded(member, T0.AddHours(5).AddMinutes(1));
        var blip = Assert.Single(Step(incidents, T0.AddHours(5).AddMinutes(2), folder).Resolved);
        Assert.NotNull(blip.ResolvedNotifiedAt); // never announced: nothing to say

        var again = Assert.Single(Step(incidents, T0.AddHours(10), folder).Opened);
        again.NotifiedAt = T0.AddHours(10);
        member.Paused = true;
        var gone = Assert.Single(Step(incidents, T0.AddHours(11), folder).Resolved);
        Assert.NotNull(gone.ResolvedNotifiedAt); // paused, not fixed: no "back to normal"
    }

    // ── Composing messages: once per incident, reminders, retries ─────────────

    private static AlertIncident Incident(int id, AlertKind kind, string folder, DateTime openedAt, string? device = "laptop") => new()
    {
        Id = id, UserId = 1, FolderId = id, DeviceId = device is null ? null : Guid.CreateVersion7(), Kind = kind, FolderName = folder,
        DeviceName = device, Detail = "details", OpenedAt = openedAt, UpdatedAt = openedAt,
    };

    [Fact]
    public void One_message_covers_several_folders_and_each_problem_is_sent_once()
    {
        var incidents = new List<AlertIncident> { Incident(1, AlertKind.Overdue, "photos", T0), Incident(2, AlertKind.CheckFailed, "docs", T0, null) };
        var state = new AlertSettings { UserId = 1 };
        var notice = AlertDispatch.Compose(incidents, state, Policy, T0, TimeSpan.FromMinutes(15))!;
        Assert.Equal(2, notice.Opened.Count);
        Assert.False(notice.Reminder);
        AlertDispatch.MarkSent(notice, state, T0);
        Assert.Null(AlertDispatch.Compose(incidents, state, Policy, T0.AddHours(1), TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void Open_problems_are_repeated_after_the_reminder_period_only()
    {
        var incidents = new List<AlertIncident> { Incident(1, AlertKind.Overdue, "photos", T0) };
        var state = new AlertSettings { UserId = 1 };
        AlertDispatch.MarkSent(AlertDispatch.Compose(incidents, state, Policy, T0, TimeSpan.Zero)!, state, T0);
        Assert.Null(AlertDispatch.Compose(incidents, state, Policy, T0.AddHours(23), TimeSpan.Zero));
        var reminder = AlertDispatch.Compose(incidents, state, Policy, T0.AddHours(24), TimeSpan.Zero)!;
        Assert.True(reminder.Reminder);
        Assert.Empty(reminder.Opened);
        Assert.Single(reminder.StillOpen);
        Assert.Null(AlertDispatch.Compose(incidents, state, Policy with { Reminder = TimeSpan.Zero }, T0.AddDays(5), TimeSpan.Zero));
    }

    [Fact]
    public void After_a_delivery_that_reached_no_channel_the_next_try_waits()
    {
        var incidents = new List<AlertIncident> { Incident(1, AlertKind.Failing, "photos", T0) };
        var state = new AlertSettings { UserId = 1 };
        Assert.NotNull(AlertDispatch.Compose(incidents, state, Policy, T0, TimeSpan.FromMinutes(15)));
        AlertDispatch.MarkFailed(state, T0);
        Assert.Null(AlertDispatch.Compose(incidents, state, Policy, T0.AddMinutes(10), TimeSpan.FromMinutes(15)));
        Assert.NotNull(AlertDispatch.Compose(incidents, state, Policy, T0.AddMinutes(15), TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void A_recovery_is_announced_once()
    {
        var incident = Incident(1, AlertKind.Overdue, "photos", T0);
        incident.NotifiedAt = T0;
        incident.ResolvedAt = T0.AddHours(1);
        var state = new AlertSettings { UserId = 1, LastSentAt = T0 };
        var notice = AlertDispatch.Compose([incident], state, Policy, T0.AddHours(1), TimeSpan.Zero)!;
        Assert.Same(incident, Assert.Single(notice.Resolved));
        Assert.Empty(notice.StillOpen);
        AlertDispatch.MarkSent(notice, state, T0.AddHours(1));
        Assert.Null(AlertDispatch.Compose([incident], state, Policy, T0.AddDays(3), TimeSpan.Zero));
    }

    // ── Formats ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_webhook_payload_has_the_documented_shape()
    {
        var overdue = Incident(7, AlertKind.Overdue, "photos", T0);
        var fixedOne = Incident(8, AlertKind.CheckFailed, "docs", T0.AddHours(-3), null);
        fixedOne.ResolvedAt = T0;
        var message = AlertMessages.Build(new AlertNotice([overdue], [fixedOne], [overdue], false), "alice", "https://sync.example.com/", T0);
        var json = JsonNode.Parse(JsonSerializer.Serialize(message.Payload, JsonSerializerOptions.Web))!;
        Assert.Equal(1, json["version"]!.GetValue<int>());
        Assert.Equal("alert", json["event"]!.GetValue<string>());
        Assert.Equal("alice", json["user"]!.GetValue<string>());
        Assert.Equal("https://sync.example.com/", json["server"]!.GetValue<string>());
        Assert.False(json["reminder"]!.GetValue<bool>());
        var opened = json["opened"]!.AsArray().Single()!;
        Assert.Equal("overdue", opened["kind"]!.GetValue<string>());
        Assert.Equal("photos", opened["folderName"]!.GetValue<string>());
        Assert.Equal("laptop", opened["deviceName"]!.GetValue<string>());
        Assert.Equal(overdue.DeviceId.ToString(), opened["deviceId"]!.GetValue<string>());
        Assert.Equal(7, opened["id"]!.GetValue<int>());
        var resolved = json["resolved"]!.AsArray().Single()!;
        Assert.Equal("checkFailed", resolved["kind"]!.GetValue<string>());
        Assert.Null(resolved["deviceId"]);
        Assert.NotNull(resolved["resolvedAt"]);
        Assert.Single(json["open"]!.AsArray());
        Assert.Equal("ASync: 1 new problem, 1 back to normal", json["title"]!.GetValue<string>());
        Assert.True(message.Urgent);
        Assert.Equal("https://sync.example.com/alerts", message.ClickUrl);
    }

    [Fact]
    public void The_text_lists_new_recovered_and_still_open_problems()
    {
        var a = Incident(1, AlertKind.Overdue, "photos", T0);
        var b = Incident(2, AlertKind.Failing, "music", T0, "nas");
        var c = Incident(3, AlertKind.Overdue, "docs", T0, "phone");
        c.ResolvedAt = T0;
        var text = AlertMessages.Build(new AlertNotice([b], [c], [a, b], false), "alice", null, T0).Text;
        Assert.Contains("New problem:\n• \"music\" on nas is failing. details", text);
        Assert.Contains("Back to normal:\n• \"docs\" on phone syncs again.", text);
        Assert.Contains("Still open:\n• \"photos\" on laptop is overdue.", text);
        Assert.DoesNotContain("Details:", text); // no PublicUrl, no link
        Assert.Equal("ASync: 2 problems still open", AlertMessages.Title(new AlertNotice([], [], [a, b], true)));
        Assert.Equal("ASync: back to normal", AlertMessages.Title(new AlertNotice([], [c], [], false)));
    }

    [Fact]
    public void The_ntfy_body_uses_its_JSON_publishing_fields()
    {
        var notice = new AlertNotice([Incident(1, AlertKind.Overdue, "photos", T0)], [], [], false);
        var body = JsonNode.Parse(JsonSerializer.Serialize(AlertSender.NtfyBody(new NtfyChannelSettings("https://ntfy.sh", "my-alerts"),
            AlertMessages.Build(notice, "alice", null, T0)), JsonSerializerOptions.Web))!;
        Assert.Equal("my-alerts", body["topic"]!.GetValue<string>());
        Assert.Equal(4, body["priority"]!.GetValue<int>());
        Assert.Equal("warning", body["tags"]![0]!.GetValue<string>());
        Assert.StartsWith("ASync: 1 new problem", body["title"]!.GetValue<string>());
        var test = JsonNode.Parse(JsonSerializer.Serialize(AlertSender.NtfyBody(new NtfyChannelSettings("https://ntfy.sh", "t"),
            AlertMessages.Test("alice", null, T0)), JsonSerializerOptions.Web))!;
        Assert.Equal(3, test["priority"]!.GetValue<int>());
    }

    // ── Channels: validation and secrets ──────────────────────────────────────

    private sealed class Harness
    {
        public readonly ManualClock Clock = new(new DateTimeOffset(T0));
        public readonly FakeFolderRepository Folders = new();
        public readonly FakeUserRepository Users = new();
        public readonly FakeAlertRepository Alerts = new();
        public readonly RecordingSender Sender = new();
        public readonly RecordingNotifier Notifier = new();
        public readonly AlertService Service;
        public readonly AlertMonitor Monitor;

        public Harness(bool smtp = false)
        {
            Users.Add("alice");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PublicUrl"] = "https://sync.example.com",
                ["Smtp:Host"] = smtp ? "mail.example.com" : null,
                ["Smtp:From"] = smtp ? "async@example.com" : null,
            }).Build();
            var options = AlertOptions.FromConfiguration(config);
            Service = new AlertService(Alerts, Users, Sender, new EphemeralDataProtectionProvider(), options, SmtpOptions.FromConfiguration(config),
                PushSettings.FromConfiguration(config), config, Clock);
            Monitor = new AlertMonitor(Folders, Users, Alerts, Service, Sender, options, new ServerStarted(LongAgo), Notifier, Clock,
                NullLogger<AlertMonitor>.Instance);
        }

        public DateTime Now => Clock.GetUtcNow().UtcDateTime;
    }

    [Theory]
    [InlineData("ftp://example.com/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("example.com/hook")]
    [InlineData("")]
    public async Task Webhook_and_ntfy_addresses_must_be_http_or_https(string url)
    {
        var h = new Harness();
        Assert.False((await h.Service.CreateChannelAsync(1, new SaveAlertChannelRequest("hook", new WebhookChannelSettings(url)))).Success);
        Assert.False((await h.Service.CreateChannelAsync(1, new SaveAlertChannelRequest("ntfy", new NtfyChannelSettings(url, "topic")))).Success);
    }

    [Fact]
    public async Task Email_needs_the_servers_SMTP_settings_and_a_valid_address()
    {
        var without = await new Harness().Service.CreateChannelAsync(1, new SaveAlertChannelRequest("mail", new EmailChannelSettings("alice@example.com")));
        Assert.Contains("SMTP", without.Error);
        var h = new Harness(smtp: true);
        Assert.False((await h.Service.CreateChannelAsync(1, new SaveAlertChannelRequest("mail", new EmailChannelSettings("not an address")))).Success);
        Assert.True((await h.Service.CreateChannelAsync(1, new SaveAlertChannelRequest("mail", new EmailChannelSettings("alice@example.com")))).Success);
    }

    [Fact]
    public async Task Channel_secrets_are_encrypted_write_only_and_kept_on_edit()
    {
        var h = new Harness();
        var created = (await h.Service.CreateChannelAsync(1, new SaveAlertChannelRequest("phone",
            new NtfyChannelSettings("https://ntfy.example.com/", "alerts", "tk_secret")))).Value!;
        Assert.DoesNotContain("tk_secret", created.ProtectedSettings);
        var dto = h.Service.ToDto(created);
        Assert.Null(((NtfyChannelSettings)dto.Settings).Token);
        Assert.Equal(["Token"], dto.SecretsSet);
        Assert.Equal("https://ntfy.example.com/alerts", dto.Description);

        await h.Service.UpdateChannelAsync(created, new SaveAlertChannelRequest("phone", new NtfyChannelSettings("https://ntfy.example.com", "renamed")));
        var stored = (NtfyChannelSettings)h.Service.Settings(created);
        Assert.Equal(("renamed", "tk_secret"), (stored.Topic, stored.Token));

        var result = (await h.Service.TestAsync(1, new NtfyChannelSettings("https://ntfy.example.com", "renamed"), created, default)).Value!;
        Assert.True(result.Ok);
        var (settings, message) = Assert.Single(h.Sender.Sent);
        Assert.Equal("tk_secret", ((NtfyChannelSettings)settings).Token); // the test used the stored token
        Assert.Equal("test", message.Payload.Event);
        Assert.True(created.LastOk);
    }

    [Fact]
    public async Task User_rules_override_the_server_defaults_within_limits()
    {
        var h = new Harness();
        Assert.False((await h.Service.SaveSettingsAsync(1, new SaveAlertSettingsRequest(GraceFactor: 0.5))).Success);
        Assert.False((await h.Service.SaveSettingsAsync(1, new SaveAlertSettingsRequest(ReminderHours: -1))).Success);
        var saved = (await h.Service.SaveSettingsAsync(1, new SaveAlertSettingsRequest(GraceFactor: 2, MinimumGraceHours: 1, ReminderHours: 0))).Value;
        var policy = h.Service.PolicyFor(saved);
        Assert.Equal((2.0, TimeSpan.FromHours(1), 3, TimeSpan.Zero), (policy.GraceFactor, policy.MinimumGrace, policy.FailureThreshold, policy.Reminder));
        Assert.Equal(new AlertDefaultsDto(3, 4, 3, 24), h.Service.ToDto(saved).Defaults);
        Assert.Equal("None", h.Service.ToDto(saved).PushMode);
    }

    [Fact]
    public void Push_mode_defaults_to_None_and_other_modes_fall_back_with_a_warning()
    {
        Assert.Equal(new PushSettings(PushMode.None, null), PushSettings.FromConfiguration(new ConfigurationBuilder().Build()));
        var relay = PushSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Push:Mode"] = "Relay" }).Build());
        Assert.Equal(PushMode.None, relay.Mode);
        Assert.Contains("not available yet", relay.Warning);
    }

    // ── A monitor pass, end to end with fakes ─────────────────────────────────

    [Fact]
    public async Task The_monitor_sends_one_alert_per_incident_then_a_recovery()
    {
        var h = new Harness();
        await h.Service.CreateChannelAsync(1, new SaveAlertChannelRequest("hook", new WebhookChannelSettings("https://hooks.example.com/x", "Bearer s3cret")));
        var (photos, laptop) = NewFolder(1);
        var (music, nas) = NewFolder(2, device: "nas");
        h.Folders.Folders.AddRange([photos, music]);
        Succeeded(laptop, h.Now);
        Succeeded(nas, h.Now);

        await h.Monitor.RunAsync(default);
        Assert.Empty(h.Sender.Sent);

        h.Clock.Advance(TimeSpan.FromHours(5));
        await h.Monitor.RunAsync(default);
        var (settings, alert) = Assert.Single(h.Sender.Sent);
        Assert.Equal("Bearer s3cret", ((WebhookChannelSettings)settings).Authorization);
        Assert.Equal(["folder1", "folder2"], alert.Payload.Opened.Select(i => i.FolderName)); // one message, both folders
        Assert.Contains(h.Notifier.ToUsers, e => e.Name == SyncEvents.AlertsChanged);

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.Monitor.RunAsync(default);
        Assert.Single(h.Sender.Sent); // no storm

        Succeeded(laptop, h.Now);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.Monitor.RunAsync(default);
        Assert.Equal(2, h.Sender.Sent.Count);
        var recovery = h.Sender.Sent[1].Message.Payload;
        Assert.Equal("folder1", Assert.Single(recovery.Resolved).FolderName);
        Assert.Equal("folder2", Assert.Single(recovery.Open).FolderName);
        Assert.Empty(recovery.Opened);

        h.Clock.Advance(TimeSpan.FromHours(24));
        Succeeded(laptop, h.Now); // still fine: only the nas is reminded of
        await h.Monitor.RunAsync(default);
        Assert.True(h.Sender.Sent[^1].Message.Payload.Reminder);
        Assert.Equal(3, h.Sender.Sent.Count);
    }

    [Fact]
    public async Task Without_channels_incidents_are_still_recorded_and_a_failed_delivery_is_retried()
    {
        var h = new Harness();
        var (folder, member) = NewFolder();
        h.Folders.Folders.Add(folder);
        Succeeded(member, h.Now);
        h.Clock.Advance(TimeSpan.FromHours(5));
        await h.Monitor.RunAsync(default);
        var incident = Assert.Single(h.Alerts.Incidents);
        Assert.NotNull(incident.NotifiedAt); // settled: nowhere to send it

        Failed(member, h.Now);
        member.ConsecutiveFailures = 3;
        var (folder2, member2) = NewFolder(2);
        h.Folders.Folders.Add(folder2);
        Succeeded(member2, h.Now.AddHours(-5));
        var channel = (await h.Service.CreateChannelAsync(1, new SaveAlertChannelRequest("hook", new WebhookChannelSettings("https://hooks.example.com/x")))).Value!;
        h.Sender.Fail = "The server answered 500";
        await h.Monitor.RunAsync(default);
        Assert.Single(h.Sender.Sent);
        Assert.False(channel.LastOk);
        Assert.Equal("The server answered 500", channel.LastError);

        h.Sender.Fail = null;
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.Monitor.RunAsync(default);
        Assert.Single(h.Sender.Sent); // waits 15 minutes after a failed delivery
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        await h.Monitor.RunAsync(default);
        Assert.Equal(2, h.Sender.Sent.Count);
        Assert.Equal("folder2", Assert.Single(h.Sender.Sent[1].Message.Payload.Opened).FolderName);
        Assert.Equal(AlertKind.Failing, h.Alerts.Incidents.Single(i => i.FolderId == 1).Kind);
        Assert.True(channel.LastOk);
    }
}

internal sealed class RecordingSender : IAlertSender
{
    public readonly List<(AlertChannelSettings Settings, AlertMessage Message)> Sent = [];
    public string? Fail;

    public Task<string?> SendAsync(AlertChannelSettings settings, AlertMessage message, CancellationToken ct)
    {
        Sent.Add((settings, message));
        return Task.FromResult(Fail);
    }
}

internal sealed class FakeAlertRepository : IAlertRepository
{
    public readonly List<AlertIncident> Incidents = [];
    public readonly List<AlertChannel> Channels = [];
    public readonly List<AlertSettings> Settings = [];

    public Task<AlertSettings?> GetSettingsAsync(int userId) => Task.FromResult(Settings.FirstOrDefault(s => s.UserId == userId));

    public async Task<AlertSettings> GetOrAddSettingsAsync(int userId)
    {
        if (await GetSettingsAsync(userId) is { } s) return s;
        var added = new AlertSettings { UserId = userId };
        Settings.Add(added);
        return added;
    }

    public Task<List<AlertChannel>> GetChannelsAsync(int userId) => Task.FromResult(Channels.Where(c => c.UserId == userId).ToList());
    public Task<AlertChannel?> GetChannelAsync(int id) => Task.FromResult(Channels.FirstOrDefault(c => c.Id == id));
    public Task<AlertChannel> AddChannelAsync(AlertChannel channel) { channel.Id = Channels.Count + 1; Channels.Add(channel); return Task.FromResult(channel); }
    public Task DeleteChannelAsync(AlertChannel channel) { Channels.Remove(channel); return Task.CompletedTask; }

    public Task<List<AlertIncident>> GetActiveAsync() =>
        Task.FromResult(Incidents.Where(i => i.ResolvedAt == null || (i.NotifiedAt != null && i.ResolvedNotifiedAt == null)).ToList());

    public Task<List<AlertIncident>> GetForUserAsync(int userId, DateTime resolvedSince) =>
        Task.FromResult(Incidents.Where(i => i.UserId == userId && (i.ResolvedAt == null || i.ResolvedAt >= resolvedSince)).ToList());

    public void AddIncidents(IEnumerable<AlertIncident> incidents)
    {
        foreach (var i in incidents)
        {
            i.Id = Incidents.Count + 1;
            Incidents.Add(i);
        }
    }

    public Task<int> DeleteResolvedAsync(DateTime olderThan) =>
        Task.FromResult(Incidents.RemoveAll(i => i.ResolvedAt < olderThan && i.ResolvedNotifiedAt != null));

    public Task SaveAsync() => Task.CompletedTask;
}
