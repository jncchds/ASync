using System.Security.Claims;

namespace ASync.Api.Auth;

public static class Roles
{
    public const string Admin = "Admin";
    public const string User = "User";
    /// <summary>An enrolled device acting for its owner.</summary>
    public const string Device = "Device";
    /// <summary>For <c>[Authorize(Roles = Roles.Users)]</c>: a person signed in to the web UI, not a device.</summary>
    public const string Users = Admin + "," + User;
}

public static class AuthClaims
{
    public const string DeviceId = "device_id";
}

public static class ClaimsPrincipalExtensions
{
    public static int UserId(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? throw new InvalidOperationException("The request is not signed in."));

    /// <summary>The calling device, or null when the caller is a person.</summary>
    public static Guid? DeviceId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(AuthClaims.DeviceId), out var id) ? id : null;
}
