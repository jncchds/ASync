using System.Runtime.CompilerServices;
using ASync.Agent.Control;
using ASync.Contracts;

namespace ASync.App.Services;

/// <summary>
/// An agent that runs in the app's own process (Android, iOS: there is no separate agent service), behind the same
/// <see cref="IAgentConnector"/> the desktop uses for the control channel, so the view models do not change. Requests
/// go straight to the agent's <see cref="IControlHandler"/> (<c>AgentRuntime</c>) and events come from its
/// <see cref="ControlEventHub"/>. Behaves like <see cref="ControlAgentConnector"/>: the agent's errors arrive as
/// <see cref="ControlException"/>s with its message, a missing field is refused the way the control server refuses it,
/// and a stopped agent ends the event stream. Any number of sessions may be open at once (the UI, a foreground service,
/// a documents provider later): the agent is shared, the sessions own nothing.
/// </summary>
/// <param name="isRunning">Whether the agent is up (null: always); <see cref="ConnectAsync"/> answers null when it is not.</param>
public sealed class InProcessAgentConnector(IControlHandler agent, ControlEventHub events, Func<bool>? isRunning = null) : IAgentConnector
{
    public Task<IAgentSession?> ConnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IAgentSession?>(isRunning is null || isRunning() ? new Session(agent, events) : null);
    }

    private sealed class Session(IControlHandler agent, ControlEventHub events) : IAgentSession
    {
        private int _disposed;

        public HelloDto Hello { get; } = agent.Hello();

        private void CheckOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        /// <summary>Errors as the control client reports them: the agent's message in a <see cref="ControlException"/>.</summary>
        private async Task<T> Call<T>(Func<Task<T>> call, CancellationToken ct)
        {
            CheckOpen();
            ct.ThrowIfCancellationRequested();
            try
            {
                return await call();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ControlException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new ControlException(e.Message);
            }
        }

        private static string Need(string? value, string op, string name) =>
            !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new ControlException($"\"{op}\" needs a {name}.");

        public Task<AgentStatusDto> StatusAsync(CancellationToken ct) => Call(() => agent.StatusAsync(ct), ct);

        public async IAsyncEnumerable<ControlEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
        {
            CheckOpen();
            // Subscribed before the first status is read, so no change between the two is lost (as the server does it).
            using var subscription = events.Subscribe();
            yield return new ControlEvent { Kind = ControlEventKinds.Status, Status = await StatusAsync(ct) };
            var reader = subscription.Reader;
            while (true)
            {
                bool more;
                try { more = await reader.WaitToReadAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { yield break; }
                if (!more || Volatile.Read(ref _disposed) != 0) yield break;
                while (!ct.IsCancellationRequested && reader.TryRead(out var e)) yield return e;
                if (ct.IsCancellationRequested) yield break;
            }
        }

        public Task<List<FolderRunDto>> SyncAsync(int? folderId, CancellationToken ct) => Call(() => agent.SyncAsync(folderId, false, ct), ct);

        public Task<string> SetPausedAsync(int folderId, bool paused, CancellationToken ct) => Call(() => agent.SetPausedAsync(folderId, paused, ct), ct);

        public Task<string> SetPathAsync(int folderId, string path, CancellationToken ct) =>
            Call(() => agent.SetPathAsync(folderId, path is { Length: > 0 } ? path : throw new ControlException("\"set-path\" needs a path."), ct), ct);

        public Task<string> UnlockAsync(int folderId, string secret, CancellationToken ct) =>
            Call(() => agent.UnlockAsync(folderId, secret is { Length: > 0 } ? secret : throw new ControlException("\"unlock\" needs a secret."), ct), ct);

        public Task<AgentStatusDto> EnrollAsync(string server, string code, string name, DevicePlatform platform, CancellationToken ct) =>
            Call(() => agent.EnrollAsync(Need(server, "enroll", "server"), Need(code, "enroll", "code"), Need(name, "enroll", "name"), platform, ct), ct);

        public Task<PendingEnrollmentDto> StartDeviceCodeAsync(string server, string name, DevicePlatform platform, CancellationToken ct) =>
            Call(() => agent.StartDeviceCodeAsync(Need(server, "device-code", "server"), Need(name, "device-code", "name"), platform, ct), ct);

        public Task<string> ForgetAsync(CancellationToken ct) => Call(() => agent.ForgetAsync(ct), ct);
        public Task<string> SetConditionsAsync(int folderId, RunConditionsDto conditions, CancellationToken ct) =>
            Call(() => agent.SetConditionsAsync(folderId, conditions ?? throw new ControlException("\"set-conditions\" needs conditions."), ct), ct);
        public Task<string> CancelAsync(int? folderId, CancellationToken ct) => Call(() => agent.CancelAsync(folderId, ct), ct);

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }
}
