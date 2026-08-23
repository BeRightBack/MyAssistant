using System.IO;
using System.Runtime.CompilerServices;

namespace MyAssistant.Main;

public static class AppLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyAssistant", "logs", $"app_{DateTime.Now:yyyyMMdd}.log");

    private static readonly object _lock = new();

    static AppLog()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
    }

    public static void Warn(string message, [CallerMemberName] string? caller = null,
        [CallerFilePath] string? file = null, [CallerLineNumber] int line = 0)
    {
        Write("WARN", message, caller, file, line);
    }

    public static void Error(string message, Exception? ex = null, [CallerMemberName] string? caller = null,
        [CallerFilePath] string? file = null, [CallerLineNumber] int line = 0)
    {
        var msg = ex != null ? $"{message}: {ex.GetType().Name}: {ex.Message}" : message;
        Write("ERROR", msg, caller, file, line);
    }

    public static void Info(string message, [CallerMemberName] string? caller = null,
        [CallerFilePath] string? file = null, [CallerLineNumber] int line = 0)
    {
        Write("INFO", message, caller, file, line);
    }

    private static void Write(string level, string message, string? caller, string? file, int line)
    {
        try
        {
            var shortFile = string.IsNullOrEmpty(file) ? "?" : Path.GetFileName(file);
            var entry = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {shortFile}:{line} ({caller}) {message}";
            lock (_lock)
            {
                File.AppendAllText(LogPath, entry + Environment.NewLine);
            }
        }
        catch
        {
            // Logging itself should never throw
        }
    }
}
