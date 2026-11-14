using System.Text;
using System.Text.Json;
using ASync.Contracts;
using ASync.Core.Alerts;
using ASync.Core.Models;

namespace ASync.Api.Services.Alerts;

/// <summary>One message, ready for every channel: a title and plain text (ntfy, e-mail) and the webhook's JSON.</summary>
public sealed record AlertMessage(string Title, string Text, AlertPayload Payload, bool Urgent, string? ClickUrl);

/// <summary>Turns an <see cref="AlertNotice"/> into an <see cref="AlertMessage"/>. Pure, so the formats are unit-tested.</summary>
public static class AlertMessages
{
    public const int PayloadVersion = 1;

    public static AlertMessage Build(AlertNotice notice, string user, string? server, DateTime now)
    {
        var stillOpen = notice.StillOpen.Where(i => !notice.Opened.Contains(i)).ToList();
        var title = Title(notice);
        var text = new StringBuilder();
        if (notice.Opened.Count > 0) Section(text, notice.Opened.Count == 1 ? "New problem:" : "New problems:", notice.Opened, Problem);
        if (notice.Resolved.Count > 0) Section(text, "Back to normal:", notice.Resolved, Recovery);
        if (stillOpen.Count > 0) Section(text, notice.Reminder ? "Still not fixed:" : "Still open:", stillOpen, Problem);
        var alertsUrl = server is null ? null : $"{server.TrimEnd('/')}/alerts";
        if (alertsUrl is not null) text.Append("Details: ").Append(alertsUrl).Append('\n');
        var payload = new AlertPayload(PayloadVersion, "alert", title, text.ToString().TrimEnd(), server, user, now, notice.Reminder,
            [.. notice.Opened.Select(ToPayload)], [.. notice.Resolved.Select(ToPayload)], [.. notice.StillOpen.Select(ToPayload)]);
        return new AlertMessage(title, payload.Text, payload, notice.Opened.Count > 0, alertsUrl);
    }

    public static AlertMessage Test(string user, string? server, DateTime now)
    {
        const string title = "ASync: test alert";
        var text = $"This is a test message from ASync for {user}. Alerts about overdue or failing devices arrive here.";
        return new AlertMessage(title, text, new AlertPayload(PayloadVersion, "test", title, text, server, user, now, false, [], [], []), false,
            server is null ? null : $"{server.TrimEnd('/')}/alerts");
    }

    public static string Title(AlertNotice notice)
    {
        if (notice.Opened.Count > 0)
            return $"ASync: {Count(notice.Opened.Count, "new problem")}" + (notice.Resolved.Count > 0 ? $", {notice.Resolved.Count} back to normal" : "");
        if (notice.Resolved.Count > 0)
            return notice.StillOpen.Count == 0 ? "ASync: back to normal" : $"ASync: {notice.Resolved.Count} back to normal, {notice.StillOpen.Count} still open";
        return $"ASync: {Count(notice.StillOpen.Count, "problem")} still open";
    }

    /// <summary>"overdue", "failing", "checkFailed", "pruneFailed": the payload's kind names.</summary>
    public static string KindName(AlertKind kind) => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    public static string Subject(AlertIncident i) =>
        i.DeviceName is null ? $"\"{i.FolderName}\"" : $"\"{i.FolderName}\" on {i.DeviceName}";

    public static string Problem(AlertIncident i) => i.Kind switch
    {
        AlertKind.Overdue => $"{Subject(i)} is overdue. {i.Detail}",
        AlertKind.Failing => $"{Subject(i)} is failing. {i.Detail}",
        AlertKind.CheckFailed => $"{Subject(i)}: integrity check failed. {i.Detail}",
        _ => $"{Subject(i)}: freeing space failed. {i.Detail}",
    };

    public static string Recovery(AlertIncident i) => i.Kind switch
    {
        AlertKind.Overdue or AlertKind.Failing => $"{Subject(i)} syncs again.",
        AlertKind.CheckFailed => $"{Subject(i)}: the integrity check passes again.",
        _ => $"{Subject(i)}: freeing space works again.",
    };

    public static AlertPayloadIncident ToPayload(AlertIncident i) =>
        new(i.Id, KindName(i.Kind), i.FolderId, i.FolderName, i.DeviceId, i.DeviceName, i.Detail, i.OpenedAt, i.ResolvedAt);

    private static void Section(StringBuilder text, string heading, List<AlertIncident> items, Func<AlertIncident, string> line)
    {
        text.Append(heading).Append('\n');
        foreach (var i in items) text.Append("• ").Append(line(i)).Append('\n');
        text.Append('\n');
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
