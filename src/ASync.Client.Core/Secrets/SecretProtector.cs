using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace ASync.Client.Core.Secrets;

/// <summary>
/// How <see cref="FileClientStore"/> keeps the secrets in its files (the device's private key and the unlocked keys of
/// end-to-end folders). <see cref="Protect"/> returns the text written to the file; <see cref="Unprotect"/> reads
/// any format, so a store written on another platform or by an older version keeps working:
/// <list type="bullet">
/// <item>plain base64: the file itself is the protection (0600 on Linux and macOS). What every version before the
///   agent service (v0.1.4) wrote, and what Linux and the Docker image still write.</item>
/// <item><c>dpapi:</c>base64: encrypted with Windows DPAPI for the current user (<see cref="DpapiSecretProtector"/>).</item>
/// <item><c>keychain:</c>account: kept in the macOS login Keychain (<see cref="KeychainSecretProtector"/>); the file
///   only names the item.</item>
/// </list>
/// </summary>
public interface ISecretProtector
{
    /// <summary>The format this protector writes: <c>file</c>, <c>dpapi</c> or <c>keychain</c>.</summary>
    string Kind { get; }

    /// <param name="id">Stable name of the secret within its store (<c>device-key</c>, <c>folder-12</c>).</param>
    string Protect(string id, byte[] secret);

    /// <summary>Removes what <see cref="Protect"/> kept outside the file (a Keychain item); no-op otherwise.</summary>
    void Forget(string id);
}

public static class SecretProtectors
{
    public const string DpapiPrefix = "dpapi:";
    public const string KeychainPrefix = "keychain:";

    /// <summary>
    /// The protector for this machine: DPAPI on Windows, the Keychain on macOS, a 0600 file on Linux. The environment
    /// variable <c>ASYNC_SECRET_STORE=file</c> chooses the file everywhere (e.g. a headless Mac without a login
    /// Keychain).
    /// </summary>
    public static ISecretProtector ForThisMachine(string storeRoot)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("ASYNC_SECRET_STORE"), "file", StringComparison.OrdinalIgnoreCase))
            return PlainSecretProtector.Instance;
        if (OperatingSystem.IsWindows()) return new DpapiSecretProtector();
        if (OperatingSystem.IsMacOS()) return new KeychainSecretProtector(storeRoot);
        return PlainSecretProtector.Instance;
    }

    /// <summary>Which format a stored value is in.</summary>
    public static string KindOf(string stored) =>
        stored.StartsWith(DpapiPrefix, StringComparison.Ordinal) ? "dpapi"
        : stored.StartsWith(KeychainPrefix, StringComparison.Ordinal) ? "keychain"
        : "file";

    /// <summary>Reads a value in any of the formats (see <see cref="ISecretProtector"/>).</summary>
    public static byte[] Unprotect(string stored, string storeRoot)
    {
        switch (KindOf(stored))
        {
            case "dpapi":
                if (!OperatingSystem.IsWindows())
                    throw new CryptographicException("This secret was encrypted with Windows DPAPI and can only be read by the same Windows user.");
                return DpapiSecretProtector.Decrypt(stored[DpapiPrefix.Length..]);
            case "keychain":
                if (!OperatingSystem.IsMacOS())
                    throw new CryptographicException("This secret is kept in a macOS Keychain and can only be read on that Mac.");
                return new KeychainSecretProtector(storeRoot).Read(stored[KeychainPrefix.Length..]);
            default:
                return Convert.FromBase64String(stored.Trim());
        }
    }
}

/// <summary>Plain base64 in the store's 0600 file (Linux, Docker, and the fallback everywhere).</summary>
public sealed class PlainSecretProtector : ISecretProtector
{
    public static readonly PlainSecretProtector Instance = new();
    public string Kind => "file";
    public string Protect(string id, byte[] secret) => Convert.ToBase64String(secret);
    public void Forget(string id) { }
}

/// <summary>
/// Windows DPAPI with the current user's scope: only the same Windows account (on the same machine, or with a roaming
/// profile) can decrypt it, also when the file is copied elsewhere.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ASync client store v1");

    public string Kind => "dpapi";

    public string Protect(string id, byte[] secret) =>
        SecretProtectors.DpapiPrefix + Convert.ToBase64String(ProtectedData.Protect(secret, Entropy, DataProtectionScope.CurrentUser));

    public void Forget(string id) { }

    internal static byte[] Decrypt(string base64) =>
        ProtectedData.Unprotect(Convert.FromBase64String(base64), Entropy, DataProtectionScope.CurrentUser);
}
