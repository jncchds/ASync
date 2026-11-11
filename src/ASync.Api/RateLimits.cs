namespace ASync.Api;

public static class RateLimits
{
    /// <summary>
    /// Anonymous sign-in and device endpoints: 30 requests a minute per client IP. Behind a reverse proxy set
    /// <c>TrustForwardedHeaders=true</c> so the limit applies per real client rather than to the proxy as a whole.
    /// </summary>
    public const string Auth = "auth";
    public const int AuthPermitsPerMinute = 30;
}
