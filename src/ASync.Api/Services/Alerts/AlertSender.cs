using System.Net.Http.Headers;
using System.Text.Json;
using ASync.Contracts;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ASync.Api.Services.Alerts;

/// <summary>
/// The server's SMTP settings (<c>Smtp__*</c>), used by every person's e-mail channel. Unset <c>Smtp__Host</c> = no e-mail.
/// <c>Smtp__Security</c>: <c>StartTls</c> (default; <c>SslOnConnect</c> when the port is 465) or <c>None</c>.
/// </summary>
public sealed record SmtpOptions(string? Host, int Port, string? Username, string? Password, string? From, string Security)
{
    public bool Configured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);

    public static SmtpOptions FromConfiguration(IConfiguration c)
    {
        var port = c.GetValue("Smtp:Port", 587);
        return new SmtpOptions(c["Smtp:Host"], port, c["Smtp:Username"], c["Smtp:Password"], c["Smtp:From"],
            c["Smtp:Security"] is { Length: > 0 } s ? s : port == 465 ? "SslOnConnect" : "StartTls");
    }
}

/// <summary>Sends one <see cref="AlertMessage"/> to one channel.</summary>
public interface IAlertSender
{
    /// <summary>Null when it was delivered, otherwise why not (never throws for delivery problems).</summary>
    Task<string?> SendAsync(AlertChannelSettings settings, AlertMessage message, CancellationToken ct);
}

/// <summary>
/// Webhook (JSON POST of <see cref="AlertPayload"/>), ntfy (its JSON publishing API) and e-mail (SMTP).
/// The addresses are user-supplied, so the HTTP client (<see cref="HttpClientName"/>) only speaks http(s), follows no
/// redirects and gives up after <see cref="Timeout"/>. It can still reach hosts on the server's own network, like the
/// storage back-ends can: a documented known gap (README "Known limits").
/// </summary>
public class AlertSender(IHttpClientFactory httpClients, SmtpOptions smtp, ILogger<AlertSender> logger) : IAlertSender
{
    public const string HttpClientName = "alerts";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<string?> SendAsync(AlertChannelSettings settings, AlertMessage message, CancellationToken ct)
    {
        try
        {
            switch (settings)
            {
                case WebhookChannelSettings webhook:
                    await PostAsync(webhook.Url, Json(message.Payload), webhook.Authorization, ct);
                    return null;
                case NtfyChannelSettings ntfy:
                    await PostAsync(ntfy.ServerUrl.TrimEnd('/') + "/", Json(NtfyBody(ntfy, message)),
                        ntfy.Token is { Length: > 0 } token ? $"Bearer {token}" : null, ct);
                    return null;
                case EmailChannelSettings email:
                    await MailAsync(email.Address, message, ct);
                    return null;
                default:
                    return "Unknown channel type.";
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
                                     or MailKit.ProtocolException or MailKit.CommandException or AuthenticationException or SslHandshakeException
                                     or System.Net.Sockets.SocketException or ParseException or FormatException)
        {
            var reason = e is TaskCanceledException && !ct.IsCancellationRequested ? $"No answer within {Timeout.TotalSeconds:0} s." : e.Message;
            logger.LogInformation("Sending an alert via {Type} failed: {Reason}", settings.Type, reason);
            return reason.Length > 1000 ? reason[..1000] : reason;
        }
    }

    /// <summary>ntfy's JSON publishing body (POST to the server's root): topic, title, message, priority, tags, click.</summary>
    public static object NtfyBody(NtfyChannelSettings ntfy, AlertMessage message) => new
    {
        topic = ntfy.Topic,
        title = message.Title,
        message = message.Text,
        priority = message.Urgent ? 4 : 3,
        tags = new[] { message.Payload.Event == "test" ? "test_tube" : message.Urgent ? "warning" : message.Payload.Reminder ? "hourglass" : "white_check_mark" },
        click = message.ClickUrl,
    };

    /// <summary>A buffered JSON body (with Content-Length: some receivers refuse chunked requests).</summary>
    private static ByteArrayContent Json<T>(T value)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private async Task PostAsync(string url, HttpContent content, string? authorization, CancellationToken ct)
    {
        if (AlertService.ValidateUrl(url, "address") is { } invalid) throw new InvalidOperationException(invalid);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (authorization is { Length: > 0 }) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ASync", null));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    private async Task MailAsync(string address, AlertMessage message, CancellationToken ct)
    {
        if (!smtp.Configured) throw new InvalidOperationException("This server has no SMTP settings (Smtp__Host, Smtp__From), so it cannot send e-mail.");
        var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(smtp.From!));
        mail.To.Add(MailboxAddress.Parse(address));
        mail.Subject = message.Title;
        mail.Body = new TextPart("plain") { Text = message.Text };
        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout * 2);
        var security = smtp.Security.ToLowerInvariant() switch
        {
            "none" => SecureSocketOptions.None,
            "sslonconnect" or "ssl" or "tls" => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(smtp.Host!, smtp.Port, security, timeout.Token);
        if (!string.IsNullOrEmpty(smtp.Username)) await client.AuthenticateAsync(smtp.Username, smtp.Password ?? "", timeout.Token);
        await client.SendAsync(mail, timeout.Token);
        await client.DisconnectAsync(true, timeout.Token);
    }
}
