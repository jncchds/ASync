namespace ASync.Agent;

/// <summary>
/// One agent per config directory (per device identity): <c>agent.lock</c> in it, held open with
/// <see cref="FileShare.None"/> for as long as the agent runs. .NET turns that into an exclusive <c>flock</c> on Linux and
/// macOS and a sharing lock on Windows, and the OS drops it when the process dies, so a crash never leaves a stale lock.
/// A second <c>async-cli run</c> or service, and in-process sync commands, check it and refuse.
/// </summary>
public sealed class AgentLock : IDisposable
{
    public const string FileName = "agent.lock";

    private readonly FileStream _file;

    private AgentLock(FileStream file) => _file = file;

    /// <summary>The lock, or null when another agent holds it.</summary>
    public static AgentLock? TryAcquire(string configDirectory)
    {
        Directory.CreateDirectory(configDirectory);
        try
        {
            return new AgentLock(new FileStream(Path.Combine(configDirectory, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Whether an agent runs for this config directory right now.</summary>
    public static bool IsHeld(string configDirectory)
    {
        if (!Directory.Exists(configDirectory)) return false;
        try
        {
            using var probe = new FileStream(Path.Combine(configDirectory, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false; // not ours to check (a read-only config): nothing to guard
        }
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>Another agent already runs for this config directory.</summary>
public sealed class AgentAlreadyRunningException(string configDirectory)
    : Exception($"An ASync agent is already running for {configDirectory} (as a service or `async-cli run`). " +
                "`async-cli status` shows it; stop it before starting another one.");
