namespace ASync.Api;

public static class RateLimits
{
    /// <summary>
    /// Anonymous sign-in and device endpoints: 30 requests a minute per client IP. Behind a reverse proxy set
    /// <c>TrustForwardedHeaders=true</c> so the limit applies per real client rather than to the proxy as a whole.
    /// </summary>
    public const string Auth = "auth";
    public const int AuthPermitsPerMinute = 30;

    /// <summary>
    /// Device sign-in (challenge + signed token): 240 requests a minute per client IP. Guessing is pointless here
    /// (the device must sign with its private key), and many agents often share one address (containers on a NAS,
    /// devices behind one router), each signing in at start and every 15 minutes.
    /// </summary>
    public const string DeviceSignIn = "device-sign-in";
    public const int DeviceSignInPermitsPerMinute = 240;
}
