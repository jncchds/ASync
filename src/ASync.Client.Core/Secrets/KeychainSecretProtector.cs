using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace ASync.Client.Core.Secrets;

/// <summary>
/// Keeps secrets as generic passwords in the user's default (login) Keychain: service <c>ua.pp.chds.async</c>, account
/// <c>&lt;store hash&gt;/&lt;id&gt;</c>, so several stores (config directories) of one user do not collide. It calls
/// Security.framework directly with P/Invoke (the generic-password functions, deprecated by Apple but still shipped and
/// simple enough to bind without Objective-C or a native build step).
/// <para>Only compiled on Linux and in CI: see AGENTS.md "Device identity and secrets per platform".</para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class KeychainSecretProtector(string storeRoot) : ISecretProtector
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecItemNotFound = -25300;
    private static readonly byte[] Service = Encoding.UTF8.GetBytes("ua.pp.chds.async");

    private readonly string _prefix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(storeRoot))))[..16];

    public string Kind => "keychain";

    private string Account(string id) => $"{_prefix}/{id}";

    public string Protect(string id, byte[] secret)
    {
        var account = Account(id);
        var accountBytes = Encoding.UTF8.GetBytes(account);
        var found = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)accountBytes.Length, accountBytes,
            out _, out var data, out var item);
        if (found == 0)
        {
            try
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)secret.Length, secret), "update");
            }
            finally { CFRelease(item); }
        }
        else if (found == ErrSecItemNotFound)
        {
            Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)accountBytes.Length, accountBytes,
                (uint)secret.Length, secret, out var added), "add");
            if (added != IntPtr.Zero) CFRelease(added);
        }
        else
        {
            Check(found, "find");
        }
        return SecretProtectors.KeychainPrefix + account;
    }

    /// <param name="account">What follows <c>keychain:</c> in the file.</param>
    public byte[] Read(string account)
    {
        var accountBytes = Encoding.UTF8.GetBytes(account);
        Check(SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)accountBytes.Length, accountBytes,
            out var length, out var data, out var item), "read");
        try
        {
            var secret = new byte[length];
            Marshal.Copy(data, secret, 0, (int)length);
            return secret;
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            CFRelease(item);
        }
    }

    public void Forget(string id)
    {
        var accountBytes = Encoding.UTF8.GetBytes(Account(id));
        if (SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)accountBytes.Length, accountBytes,
                out _, out var data, out var item) != 0)
            return;
        SecKeychainItemFreeContent(IntPtr.Zero, data);
        try { SecKeychainItemDelete(item); }
        finally { CFRelease(item); }
    }

    private static void Check(int status, string what)
    {
        if (status != 0)
            throw new CryptographicException($"The macOS Keychain refused to {what} an ASync secret (OSStatus {status}). " +
                                             "Unlock the login Keychain, or set ASYNC_SECRET_STORE=file to keep secrets in a 0600 file.");
    }

    [LibraryImport(Security)]
    private static partial int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemDelete(IntPtr itemRef);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr cf);
}
