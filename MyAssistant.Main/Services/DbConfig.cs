using System.IO;
using System.Text.Json;

namespace MyAssistant.Main;

public static class DbConfig
{
    private static string? _connStr;
    private static DateTime _lastLoad = DateTime.MinValue;
    private static readonly object _lock = new();

    public static string ConnectionString
    {
        get
        {
            lock (_lock)
            {
                if (_connStr == null || (DateTime.UtcNow - _lastLoad).TotalMinutes > 5)
                    _connStr = BuildConnectionString();
                return _connStr;
            }
        }
    }

    public static void InvalidateCache()
    {
        lock (_lock) { _connStr = null; }
    }

    private static string BuildConnectionString()
    {
        var settings = LoadSettings();

        var server = GetEnv("MYCODER_DB_HOST") ?? settings.Host ?? "localhost";
        var port = GetEnv("MYCODER_DB_PORT") ?? settings.Port?.ToString() ?? "3306";
        var database = GetEnv("MYCODER_DB_NAME") ?? settings.Name ?? "mycoderdb";
        var user = GetEnv("MYCODER_DB_USER") ?? settings.User ?? "";
        var password = GetEnv("MYCODER_DB_PASS") ?? settings.Password ?? "";
        var sslMode = GetEnv("MYCODER_DB_SSL") ?? settings.SslMode ?? "None";
        var allowPubKey = GetEnv("MYCODER_DB_ALLOW_PUBKEY") ?? settings.AllowPublicKeyRetrieval?.ToString() ?? "true";

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "Database credentials not configured. Set in appsettings.json or MYCODER_DB_USER/MYCODER_DB_PASS environment variables.");
        }

        _lastLoad = DateTime.UtcNow;
        return $"Server={server};Port={port};Database={database};User={user};Password={password};SslMode={sslMode};AllowPublicKeyRetrieval={allowPubKey};ConnectionTimeout=5;";
    }

    private static string? GetEnv(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : ReadUserRegistryEnv(name);

    // Processes launched before these user-scope variables were set (or from a
    // shell with a stale environment) do not inherit them via the process block.
    // Fall back to reading them straight from HKCU\Environment.
    private static string? ReadUserRegistryEnv(string name)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment");
            return key?.GetValue(name) as string is { Length: > 0 } v ? v : null;
        }
        catch { return null; }
    }

    private static DbSettings LoadSettings()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
                return new DbSettings();

            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Database", out var db))
            {
                return new DbSettings
                {
                    Host = db.TryGetProperty("Host", out var h) ? h.GetString() : null,
                    Port = db.TryGetProperty("Port", out var p) ? p.GetInt32() : null,
                    Name = db.TryGetProperty("Name", out var n) ? n.GetString() : null,
                    User = db.TryGetProperty("User", out var u) ? u.GetString() : null,
                    Password = db.TryGetProperty("Password", out var pw) ? pw.GetString() : null,
                    SslMode = db.TryGetProperty("SslMode", out var s) ? s.GetString() : null,
                    AllowPublicKeyRetrieval = db.TryGetProperty("AllowPublicKeyRetrieval", out var pk) ? pk.GetBoolean() : null
                };
            }
        }
        catch { }
        return new DbSettings();
    }
}

public class DbSettings
{
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Name { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? SslMode { get; set; }
    public bool? AllowPublicKeyRetrieval { get; set; }
}
