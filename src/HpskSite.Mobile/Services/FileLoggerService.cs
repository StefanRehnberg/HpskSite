using Microsoft.Extensions.Logging;

namespace HpskSite.Mobile.Services;

/// <summary>
/// Simple file logger for debugging
/// </summary>
public class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly string _logFilePath;
    private static readonly object _lock = new();
    private const long MaxLogSizeBytes = 500 * 1024; // 500KB

    public FileLogger(string categoryName, string logFilePath)
    {
        _categoryName = categoryName;
        _logFilePath = logFilePath;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{logLevel}] [{_categoryName}] {message}";

        if (exception != null)
        {
            logEntry += Environment.NewLine + exception.ToString();
        }

        lock (_lock)
        {
            try
            {
                RotateLogIfNeeded();
                File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
            }
            catch
            {
                // Ignore logging errors
            }
        }
    }

    private void RotateLogIfNeeded()
    {
        try
        {
            if (!File.Exists(_logFilePath))
                return;

            var fileInfo = new FileInfo(_logFilePath);
            if (fileInfo.Length > MaxLogSizeBytes)
            {
                // Keep last half of file
                var lines = File.ReadAllLines(_logFilePath);
                File.WriteAllLines(_logFilePath, lines.Skip(lines.Length / 2));
            }
        }
        catch
        {
            // Ignore rotation errors
        }
    }
}

public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logFilePath;

    public FileLoggerProvider(string logFilePath)
    {
        _logFilePath = logFilePath;

        // Write header on startup
        var header = $"{Environment.NewLine}=== App Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}";
        try
        {
            File.AppendAllText(_logFilePath, header);
        }
        catch { }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, _logFilePath);
    }

    public void Dispose() { }
}

/// <summary>
/// Extension methods for adding file logging
/// </summary>
public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddFileLogger(this ILoggingBuilder builder, string logFilePath)
    {
        builder.AddProvider(new FileLoggerProvider(logFilePath));
        return builder;
    }
}
