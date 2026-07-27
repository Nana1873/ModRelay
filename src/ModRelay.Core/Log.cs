using System.Text;

namespace ModRelay.Core;

/// <summary>
/// Minimal rolling file logger. One file per day, older files pruned.
/// Never throws: a broken log must not take the app down with it.
/// </summary>
public static class Log
{
    private const int KeepDays = 7;

    private static readonly object Gate = new();
    private static string? _directory;

    public static void Init(string directory)
    {
        lock (Gate)
        {
            _directory = directory;
            try
            {
                Directory.CreateDirectory(directory);
                Prune(directory);
            }
            catch
            {
                // Logging must never be fatal.
            }
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);
    public static void Info(string message) => Write(LogLevel.Info, message, null);
    public static void Warn(string message, Exception? ex = null) => Write(LogLevel.Warn, message, ex);
    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    public static string? CurrentFile =>
        _directory is null ? null : Path.Combine(_directory, $"{DateTime.Now:yyyy-MM-dd}.log");

    private static void Write(LogLevel level, string message, Exception? ex)
    {
        var file = CurrentFile;
        if (file is null)
            return;

        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("HH:mm:ss.fff"))
            .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
            .Append(message);

        if (ex is not null)
            line.AppendLine().Append(ex);

        lock (Gate)
        {
            try
            {
                File.AppendAllText(file, line.AppendLine().ToString(), Encoding.UTF8);
            }
            catch
            {
                // Disk full, file locked, permissions - none of it is worth crashing over.
            }
        }
    }

    private static void Prune(string directory)
    {
        var cutoff = DateTime.Now.AddDays(-KeepDays);
        foreach (var file in Directory.EnumerateFiles(directory, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
            catch
            {
                // Ignore; a stale log file is harmless.
            }
        }
    }

    private enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }
}
