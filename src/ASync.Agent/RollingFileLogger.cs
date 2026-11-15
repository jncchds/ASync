using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ASync.Agent;

/// <summary>
/// Logs to a file that rolls over at <c>maxBytes</c>, keeping <c>keep</c> old files (<c>agent.log.1</c> … ): for
/// services without a system log of their own (the macOS LaunchAgent, the Windows logon task). One line per entry,
/// written through a background queue so logging never waits for the disk.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _keep;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;
    private FileStream? _file;

    public RollingFileLoggerProvider(string path, long maxBytes = 10 * 1024 * 1024, int keep = 5)
    {
        _path = Path.GetFullPath(path);
        _maxBytes = maxBytes;
        _keep = keep;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _writer = new Thread(Write) { IsBackground = true, Name = "log writer" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Enqueue(string line) => _queue.TryAdd(line); // a full queue drops lines rather than block the agent

    private void Write()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                _file ??= Open();
                var bytes = Encoding.UTF8.GetBytes(line);
                if (_file.Length + bytes.Length > _maxBytes && _file.Length > 0)
                {
                    _file.Dispose();
                    Roll();
                    _file = Open();
                }
                _file.Write(bytes);
                if (_queue.Count == 0) _file.Flush();
            }
            catch (IOException) { _file?.Dispose(); _file = null; }
            catch (UnauthorizedAccessException) { _file?.Dispose(); _file = null; }
        }
        _file?.Dispose();
    }

    private FileStream Open()
    {
        var file = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return file;
    }

    private void Roll()
    {
        for (var i = _keep - 1; i >= 1; i--)
        {
            var from = $"{_path}.{i}";
            if (File.Exists(from)) File.Move(from, $"{_path}.{i + 1}", overwrite: true);
        }
        if (_keep > 0) File.Move(_path, $"{_path}.1", overwrite: true);
        else File.Delete(_path);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }

    private sealed class FileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Trace => "trce", LogLevel.Debug => "dbug", LogLevel.Information => "info",
                LogLevel.Warning => "warn", LogLevel.Error => "fail", _ => "crit",
            };
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level}: {category}: {formatter(state, exception)}{(exception is null ? "" : Environment.NewLine + exception)}{Environment.NewLine}");
            provider.Enqueue(line);
        }
    }
}
