using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NilaConnector.Desktop.Logging;

internal sealed class JsonLineFileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly object _gate = new();
    private bool _disposed;

    public ILogger CreateLogger(string categoryName) => new JsonLineFileLogger(this, categoryName);

    public void Dispose() => _disposed = true;

    private void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        if (_disposed)
        {
            return;
        }

        var entry = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            level = level.ToString(),
            category,
            eventId = eventId.Id,
            message,
            exception = exception?.ToString(),
        });

        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"nila-connector-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            File.AppendAllText(path, entry + Environment.NewLine);
        }
    }

    private sealed class JsonLineFileLogger(JsonLineFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
            }
        }
    }
}
