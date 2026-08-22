using System.Diagnostics;
using System.IO;
using System.Text;

namespace PathEcho.Services;

public static class AppLogger
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const int RetainedFiles = 4;
    private static readonly object Gate = new();
    private static bool _enabled;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PathEcho",
        "Logs");

    private static string CurrentLogPath => Path.Combine(LogDirectory, "pathecho-debug.log");

    public static void Configure(bool enabled)
    {
        lock (Gate)
        {
            _enabled = enabled;
            if (enabled)
            {
                WriteCore("INFO", "Debug logging enabled.", null);
            }
        }
    }

    public static void Debug(string message)
    {
        lock (Gate)
        {
            if (_enabled)
            {
                WriteCore("DEBUG", message, null);
            }
        }
    }

    public static void Error(string message, Exception exception)
    {
        lock (Gate)
        {
            if (_enabled)
            {
                WriteCore("ERROR", message, exception);
            }
        }
    }

    public static void OpenLogDirectory()
    {
        Directory.CreateDirectory(LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true,
            Arguments = $"\"{LogDirectory}\"",
        });
    }

    private static void WriteCore(string level, string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            RotateIfNeeded();
            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(level).Append("] ")
                .Append(message);
            if (exception is not null)
            {
                builder.AppendLine().Append(exception);
            }

            File.AppendAllText(CurrentLogPath, builder.AppendLine().ToString(), Encoding.UTF8);
        }
        catch
        {
            // Logging must never break the application workflow.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(CurrentLogPath) || new FileInfo(CurrentLogPath).Length < MaxLogBytes)
        {
            return;
        }

        var oldest = $"{CurrentLogPath}.{RetainedFiles}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = RetainedFiles - 1; index >= 1; index--)
        {
            var source = $"{CurrentLogPath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{CurrentLogPath}.{index + 1}");
            }
        }

        File.Move(CurrentLogPath, $"{CurrentLogPath}.1");
    }
}
