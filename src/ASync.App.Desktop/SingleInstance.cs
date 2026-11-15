using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ASync.Agent.Control;

namespace ASync.App.Desktop;

/// <summary>
/// One app per config directory (per device identity): <c>app.lock</c> in the config directory, held open with
/// <c>FileShare.None</c> while the app runs (the OS drops it with the process, like the agent's lock). A second start
/// asks the first one to show its window over a Unix socket next to the agent's (Linux, macOS) or a named pipe that only
/// this user can open (Windows), then exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string ShowCommand = "show";
    private readonly FileStream _lock;
    private readonly CancellationTokenSource _stop = new();
    private Socket? _socket;
    private string? _socketPath;

    private SingleInstance(FileStream held) => _lock = held;

    /// <summary>Raised (on a background thread) when another start asked for the window.</summary>
    public event Action? ShowRequested;

    public static SingleInstance? TryAcquire(string configDirectory)
    {
        try
        {
            var held = new FileStream(Path.Combine(configDirectory, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstance(held);
        }
        catch (IOException)
        {
            return null; // another app holds it
        }
    }

    private static string Hash(string configDirectory) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Environment.UserName}\n{Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDirectory))}")))[..16];

    private static string PipeName(string configDirectory) => $"ASync.App.{Hash(configDirectory)}";

    /// <summary>The socket sits in the agent's private (0700) runtime directory.</summary>
    private static string SocketPath(string configDirectory)
    {
        var agentSocket = ControlEndpoint.ForConfigDirectory(configDirectory).Address;
        return Path.Combine(Path.GetDirectoryName(agentSocket)!, $"app-{Hash(configDirectory)}.sock");
    }

    /// <summary>Starts listening for "show" from later starts.</summary>
    public void Listen()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = Task.Run(ListenPipeAsync);
            return;
        }
        try
        {
            var path = SocketPath(Path.GetDirectoryName(_lock.Name)!);
            var dir = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (File.Exists(path)) File.Delete(path); // left by a crashed app: we hold the lock, so it is ours
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            socket.Listen(4);
            _socket = socket;
            _socketPath = path;
            _ = Task.Run(ListenSocketAsync);
        }
        catch (Exception e) when (e is IOException or SocketException or UnauthorizedAccessException)
        {
            // Without it a second start just exits; the window is still reachable from the tray.
        }
    }

    private async Task ListenSocketAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _socket!.AcceptAsync(_stop.Token);
                using var reader = new StreamReader(new NetworkStream(client));
                if (await reader.ReadLineAsync(_stop.Token) == ShowCommand) ShowRequested?.Invoke();
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return; }
            catch (Exception e) when (e is IOException or SocketException) { }
        }
    }

    private async Task ListenPipeAsync()
    {
        var name = PipeName(Path.GetDirectoryName(_lock.Name)!);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(pipe);
                if (await reader.ReadLineAsync(_stop.Token) == ShowCommand) ShowRequested?.Invoke();
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return; }
            catch (IOException) { await Task.Delay(500); }
        }
    }

    /// <summary>Asks the running app to show its window (best effort).</summary>
    public static void SignalShow(string configDirectory)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var pipe = new NamedPipeClientStream(".", PipeName(configDirectory), PipeDirection.Out, PipeOptions.CurrentUserOnly);
                pipe.Connect(2000);
                using var writer = new StreamWriter(pipe);
                writer.WriteLine(ShowCommand);
                return;
            }
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath(configDirectory)));
            using var stream = new NetworkStream(socket);
            using var w = new StreamWriter(stream);
            w.WriteLine(ShowCommand);
        }
        catch (Exception e) when (e is IOException or SocketException or TimeoutException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _socket?.Dispose();
        if (_socketPath is not null)
            try { File.Delete(_socketPath); } catch (IOException) { }
        _lock.Dispose();
    }
}
