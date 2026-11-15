using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace ASync.Agent.Control;

/// <summary>
/// Where the agent of one config directory listens: a Unix domain socket (Linux, macOS) or a named pipe (Windows).
/// <para>Each config directory (each device identity) has its own endpoint, so several agents of one user (the
/// end-to-end test runs two) do not meet. The name is a hash of the user and the directory's full path:</para>
/// <list type="bullet">
/// <item>Linux: <c>$XDG_RUNTIME_DIR/async/agent-&lt;hash&gt;.sock</c> (the runtime directory is the user's own, 0700).</item>
/// <item>macOS: <c>$TMPDIR/async/agent-&lt;hash&gt;.sock</c> (TMPDIR is per user, 0700).</item>
/// <item>Elsewhere, or without either variable (a container, a system service): <c>/tmp/async-&lt;uid&gt;/</c>, created
///   0700 and refused when someone else owns it.</item>
/// <item>Windows: the pipe <c>\\.\pipe\ASync.Agent.&lt;hash&gt;</c>.</item>
/// </list>
/// <c>ASYNC_CONTROL_SOCKET</c> overrides the socket path or pipe name (the system unit sets it). The agent also writes the
/// endpoint into <c>agent.endpoint</c> in the config directory, which clients read first: then a client finds an agent
/// started with another environment.
/// </summary>
public sealed record ControlEndpoint(string Address)
{
    public const string EndpointFileName = "agent.endpoint";
    public const string EnvironmentVariable = "ASYNC_CONTROL_SOCKET";

    /// <summary>The pipe of the Windows system service (SYSTEM and administrators only).</summary>
    public const string SystemPipeName = "ASync.Agent.System";

    /// <summary>A named pipe (Windows) rather than a socket path.</summary>
    public bool IsPipe => OperatingSystem.IsWindows();

    public override string ToString() => IsPipe ? $@"\\.\pipe\{Address}" : Address;

    /// <summary>The endpoint the agent of <paramref name="configDirectory"/> listens on (see the class summary).</summary>
    public static ControlEndpoint ForConfigDirectory(string configDirectory)
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } overridden) return new ControlEndpoint(overridden);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDirectory));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.UserName}\n{root}")))[..16];
        if (OperatingSystem.IsWindows()) return new ControlEndpoint($"ASync.Agent.{hash}");
        return new ControlEndpoint(Path.Combine(RuntimeDirectory(), $"agent-{hash}.sock"));
    }

    /// <summary>What clients try: the agent's own <c>agent.endpoint</c> file if there is one, else <see cref="ForConfigDirectory"/>.</summary>
    public static ControlEndpoint Discover(string configDirectory)
    {
        var file = Path.Combine(Path.GetFullPath(configDirectory), EndpointFileName);
        try
        {
            if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } written) return new ControlEndpoint(written);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return ForConfigDirectory(configDirectory);
    }

    /// <summary>The per-user directory for sockets.</summary>
    [UnsupportedOSPlatform("windows")]
    private static string RuntimeDirectory()
    {
        string dir;
        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } xdg && Directory.Exists(xdg))
            dir = Path.Combine(xdg, "async");
        else if (OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } tmp && Directory.Exists(tmp))
            dir = Path.Combine(tmp, "async");
        else
            dir = Path.Combine("/tmp", $"async-{UnixUser.EffectiveUserId()}");
        return dir;
    }

    /// <summary>Creates the socket's directory (0700) and checks that nobody else can use it.</summary>
    [UnsupportedOSPlatform("windows")]
    internal void PrepareDirectory()
    {
        var dir = Path.GetDirectoryName(Address)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (!UnixUser.Owns(dir))
            throw new UnauthorizedAccessException($"{dir} belongs to another user or is writable by others: the control channel is not started there " +
                                                  $"(set {EnvironmentVariable} to a path in a private directory).");
    }
}
