using System.Globalization;
using ASync.Agent.Control;
using ASync.Contracts;

namespace ASync.App.Services;

/// <summary>
/// The pairing link of the web UI's QR code: <c>async://pair?s=&lt;server&gt;&amp;c=&lt;code&gt;</c> (see AGENTS.md
/// "Device provisioning"). People may paste it into the code field.
/// </summary>
public static class PairingLink
{
    public static bool TryParse(string? text, out string server, out string code)
    {
        server = code = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("async://pair", StringComparison.OrdinalIgnoreCase)) return false;
        var query = trimmed.IndexOf('?') is var q and >= 0 ? trimmed[(q + 1)..] : "";
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var value = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            switch (part[..eq].ToLowerInvariant())
            {
                case "s": server = value.Trim(); break;
                case "c": code = value.Trim(); break;
            }
        }
        return server.Length > 0 && code.Length > 0;
    }
}

/// <summary>How the UI writes sizes, times and states (invariant digits, English text like the web UI).</summary>
public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>"just now", "5 min ago", "3 h ago", "2 days ago".</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var d = now - at;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours} h ago";
        var days = (int)d.TotalDays;
        return days == 1 ? "yesterday" : $"{days} days ago";
    }

    /// <summary>"now", "in 5 min", "in 3 h", "in 2 days".</summary>
    public static string In(DateTimeOffset at, DateTimeOffset now)
    {
        var d = at - now;
        if (d < TimeSpan.FromMinutes(1)) return "now";
        if (d < TimeSpan.FromHours(1)) return $"in {(int)Math.Ceiling(d.TotalMinutes)} min";
        if (d < TimeSpan.FromDays(1)) return $"in {(int)d.TotalHours} h";
        return $"in {(int)d.TotalDays} days";
    }

    public static string Mode(SyncMode mode, MirrorDirection? direction) => mode switch
    {
        SyncMode.TwoWay => "Two-way",
        SyncMode.Mirror => direction == MirrorDirection.Download ? "Mirror ↓ from server" : "Mirror ↑ to server",
        _ => "Backup",
    };

    public static string State(AgentState state) => state switch
    {
        AgentState.Connected => "Connected",
        AgentState.Connecting => "Connecting…",
        AgentState.Reconnecting => "Reconnecting…",
        AgentState.Offline => "Offline",
        AgentState.Revoked => "Removed from the account",
        AgentState.Stopping => "Stopping…",
        _ => "Not enrolled",
    };

    public static string Alert(AlertKind kind) => kind switch
    {
        AlertKind.Overdue => "Overdue",
        AlertKind.Failing => "Failing",
        AlertKind.CheckFailed => "Check failed",
        AlertKind.PruneFailed => "Prune failed",
        _ => kind.ToString(),
    };

    public static string Progress(ProgressDto p)
    {
        var parts = new List<string>
        {
            p.TotalFiles is int total ? $"{p.Files} of {total} files" : $"{p.Files} files",
            p.TotalBytes is long totalBytes ? $"{Bytes(p.Bytes)} of {Bytes(totalBytes)}" : Bytes(p.Bytes),
        };
        return string.Join(" · ", parts);
    }
}
