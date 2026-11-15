using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ASync.Agent.Control;

/// <summary>One side of a control connection: JSON lines over a stream (socket or pipe), at most <see cref="ControlProtocol.MaxLineBytes"/> each.</summary>
public sealed class ControlConnection(Stream stream) : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    /// <summary>The next message, or null when the other side closed the connection.</summary>
    public async Task<T?> ReadAsync<T>(JsonTypeInfo<T> type, CancellationToken ct) where T : class
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                var line = _buffer.AsMemory(_start, newline - _start);
                _start = newline + 1;
                if (line.Span.Trim((byte)'\r').IsEmpty) continue;
                return JsonSerializer.Deserialize(line.Span, type) ?? throw new JsonException("An empty message.");
            }
            if (_end - _start >= ControlProtocol.MaxLineBytes) throw new InvalidDataException("A control message is too long.");
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            if (_end == _buffer.Length) Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, ControlProtocol.MaxLineBytes + 1));
            var read = await stream.ReadAsync(_buffer.AsMemory(_end), ct);
            if (read == 0) return null;
            _end += read;
        }
    }

    public async Task WriteAsync<T>(T message, JsonTypeInfo<T> type, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, type);
        await _writeGate.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(bytes, ct);
            await stream.WriteAsync("\n"u8.ToArray(), ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync();
        _writeGate.Dispose();
    }
}

/// <summary>Opens and accepts the OS-level connections (no authentication of its own: see AGENTS.md "Control channel").</summary>
public static class ControlTransport
{
    /// <summary>Connects to an agent; null when none listens there (no socket or pipe, or a stale socket file).</summary>
    public static async Task<Stream?> TryConnectAsync(ControlEndpoint endpoint, TimeSpan timeout, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!File.Exists(endpoint.ToString())) return null;
            // CurrentUserOnly: the client also checks that the pipe's server runs as this user (no impostor pipe). The
            // system service's pipe belongs to SYSTEM and is opened by administrators without that check.
            var system = endpoint.Address == ControlEndpoint.SystemPipeName;
            var pipe = new NamedPipeClientStream(".", endpoint.Address, PipeDirection.InOut,
                PipeOptions.Asynchronous | (system ? PipeOptions.None : PipeOptions.CurrentUserOnly));
            try
            {
                await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct);
                return pipe;
            }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync();
                return null;
            }
        }

        if (!File.Exists(endpoint.Address)) return null;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), limit.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            socket.Dispose();
            return null; // ECONNREFUSED: a socket file left by an agent that is gone
        }
    }

    /// <summary>A Unix socket only this user can connect to: in a 0700 directory, itself 0600.</summary>
    [UnsupportedOSPlatform("windows")]
    internal static Socket ListenUnix(ControlEndpoint endpoint)
    {
        endpoint.PrepareDirectory();
        // The caller holds the agent lock of the config directory, so a socket file here is left over from a crash.
        if (File.Exists(endpoint.Address)) File.Delete(endpoint.Address);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(endpoint.Address));
            File.SetUnixFileMode(endpoint.Address, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            socket.Listen(16);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One instance of the agent's pipe. A per-user agent's pipe is for its user only (<see cref="PipeOptions.CurrentUserOnly"/>);
    /// a system service's for SYSTEM and the Administrators group. <paramref name="first"/> refuses to start when another
    /// process already owns the name.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static NamedPipeServerStream CreatePipe(ControlEndpoint endpoint, bool first, bool systemService)
    {
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        if (!systemService)
            return new NamedPipeServerStream(endpoint.Address, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, options | PipeOptions.CurrentUserOnly);
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(endpoint.Address, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options, 0, 0, security);
    }
}
