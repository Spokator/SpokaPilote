using System.Collections.Concurrent;

namespace SpokaPilote.Core.Infrastructure;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message);

/// <summary>Journal simple, thread-safe : fichier quotidien + abonnés (console, UI).</summary>
public static class Log
{
    private static readonly object FileLock = new();
    private static readonly ConcurrentBag<Action<LogEntry>> Sinks = new();
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static void Subscribe(Action<LogEntry> sink) => Sinks.Add(sink);

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message, Exception? ex = null)
        => Write(LogLevel.Error, ex is null ? message : $"{message} : {ex.Message}");

    public static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        try
        {
            lock (FileLock)
            {
                var file = Path.Combine(AppPaths.Logs, $"{AppIdentity.Current.LogFilePrefix}-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss} [{level.ToString().ToUpperInvariant()}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Le journal ne doit jamais faire planter l'application (droits insuffisants, disque plein...).
        }

        if (level < MinimumLevel) return;
        foreach (var sink in Sinks)
        {
            try { sink(entry); } catch { /* abonné défaillant ignoré */ }
        }
    }
}
