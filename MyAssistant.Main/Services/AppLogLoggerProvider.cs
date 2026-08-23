using Microsoft.Extensions.Logging;

namespace MyAssistant.Main.Services;

public sealed class AppLogLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new AppLogLogger(categoryName);
    public void Dispose() { }

    private sealed class AppLogLogger : ILogger
    {
        private readonly string _category;
        public AppLogLogger(string category) => _category = category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = $"[{_category}] {formatter(state, exception)}";
            if (exception != null || logLevel >= LogLevel.Error)
                AppLog.Error(message, exception);
            else
                AppLog.Warn(message);
        }
    }
}
