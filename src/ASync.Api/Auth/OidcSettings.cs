namespace ASync.Api.Auth;

/// <summary>
/// Optional single sign-on through an OpenID Connect provider (Authentik, Keycloak, Entra ID, Google…).
/// On when <c>Oidc__Authority</c> and <c>Oidc__ClientId</c> are set. The provider must allow the redirect URI
/// <c>{public URL}/api/auth/oidc/callback</c>.
/// </summary>
public record OidcSettings(
    string? Authority, string? ClientId, string? ClientSecret, string DisplayName, bool AutoCreateUsers, bool RequireHttpsMetadata)
{
    public const string Scheme = "oidc";
    /// <summary>Short-lived cookie that carries the provider's answer from the callback to <c>/api/auth/oidc/complete</c>.</summary>
    public const string ExternalScheme = "External";
    public const string CallbackPath = "/api/auth/oidc/callback";

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);

    public static OidcSettings FromConfiguration(IConfiguration c) => new(
        c["Oidc:Authority"],
        c["Oidc:ClientId"],
        c["Oidc:ClientSecret"],
        c["Oidc:DisplayName"] is { Length: > 0 } name ? name : "Single sign-on",
        c.GetValue("Oidc:AutoCreateUsers", true),
        c.GetValue("Oidc:RequireHttpsMetadata", true));
}
