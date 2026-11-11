namespace ASync.Api.Services;

/// <summary>
/// The address devices and browsers use to reach this server (put into QR codes and approval links).
/// Set <c>PublicUrl</c> (env <c>PublicUrl</c>) behind a reverse proxy; otherwise the request's own host is used.
/// </summary>
public class PublicUrl(IConfiguration configuration)
{
    public string For(HttpRequest request) =>
        configuration["PublicUrl"] is { Length: > 0 } configured
            ? configured.TrimEnd('/')
            : $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
}
