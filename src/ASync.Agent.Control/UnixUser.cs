using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ASync.Agent.Control;

[UnsupportedOSPlatform("windows")]
internal static class UnixUser
{
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    public static uint EffectiveUserId() => GetEuid();

    /// <summary>
    /// Whether this user owns <paramref name="path"/>: only the owner (or root) may change its mode, so setting it to
    /// 0700 both checks and tightens it.
    /// </summary>
    public static bool Owns(string path)
    {
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
