using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace Speed.Core;

public sealed record LogEntry(DateTime Time, string Message, bool IsWarning)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>The activity list in the window, mirrored to a daily log file.</summary>
public static class AppLog
{
    public static ObservableCollection<LogEntry> Entries { get; } = new();

    private static readonly object FileLock = new();

    public static void Info(string message) => Add(message, false);
    public static void Warn(string message) => Add(message, true);

    private static void Add(string message, bool warning)
    {
        var entry = new LogEntry(DateTime.Now, message, warning);
        WriteToFile(entry);
        Dispatcher.UIThread.Post(() =>
        {
            Entries.Add(entry);
            while (Entries.Count > 500) Entries.RemoveAt(0);
        });
    }

    private static void WriteToFile(LogEntry entry)
    {
        try
        {
            var folder = Path.Combine(AppSettings.Folder, "Logs");
            Directory.CreateDirectory(folder);
            var line = $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff}  {(entry.IsWarning ? "WARN" : "INFO")}  {entry.Message}{Environment.NewLine}";
            lock (FileLock)
                File.AppendAllText(Path.Combine(folder, $"{entry.Time:yyyy-MM-dd}.log"), line);
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
