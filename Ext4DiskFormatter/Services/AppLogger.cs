using System.Text;

namespace Ext4DiskFormatter.Services;

internal static class AppLogger
{
    private static readonly object SyncRoot = new();
    private static string? _logFilePath;

    public static string Initialize()
    {
        lock (SyncRoot)
        {
            if (!string.IsNullOrWhiteSpace(_logFilePath))
                return _logFilePath;

            string logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ext4DiskFormatter",
                "Logs");

            Directory.CreateDirectory(logDirectory);
            _logFilePath = Path.Combine(logDirectory, $"Ext4DiskFormatter-{DateTime.Now:yyyyMMdd}.log");
            WriteLine("INFO", "Logger initialized.");
            return _logFilePath;
        }
    }

    public static string GetLogFilePath() => _logFilePath ?? Initialize();

    public static void Info(string message) => WriteLine("INFO", message);

    public static void Warning(string message) => WriteLine("WARN", message);

    public static void Error(string message, Exception? exception = null)
    {
        var builder = new StringBuilder(message);
        if (exception is not null)
        {
            builder.AppendLine();
            builder.Append(exception);
        }

        WriteLine("ERROR", builder.ToString());
    }

    private static void WriteLine(string level, string message)
    {
        string path = _logFilePath ?? Initialize();
        string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";

        lock (SyncRoot)
        {
            File.AppendAllText(path, entry, Encoding.UTF8);
        }
    }
}