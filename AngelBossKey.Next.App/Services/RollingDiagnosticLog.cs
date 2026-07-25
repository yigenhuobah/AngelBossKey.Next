using AngelBossKey.Next.Core.Abstractions;
using System.IO;
using System.Text;

namespace AngelBossKey.Next.App.Services;

public sealed class RollingDiagnosticLog(string directory) : IDiagnosticLog
{
    private const long MaximumBytes = 1024 * 1024;
    private const int BackupCount = 3;
    private readonly object _sync = new();
    private readonly string _path = Path.Combine(directory, "angelbosskey.log");

    public void Info(string eventName, string details) => Write("INFO", eventName, details);
    public void Warning(string eventName, string details) => Write("WARN", eventName, details);
    public void LogError(string eventName, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write("ERROR", eventName, $"exception={exception.GetType().Name}");
    }

    private void Write(string level, string eventName, string details)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(directory);
                RotateIfNeeded();
                var safeDetails = details.Replace('\r', ' ').Replace('\n', ' ');
                File.AppendAllText(
                    _path,
                    $"{DateTimeOffset.Now:O} [{level}] {eventName} {safeDetails}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never affect the visibility safety path.
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < MaximumBytes)
        {
            return;
        }

        var oldest = $"{_path}.{BackupCount}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = BackupCount - 1; index >= 1; index--)
        {
            var source = $"{_path}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{_path}.{index + 1}");
            }
        }

        File.Move(_path, $"{_path}.1");
    }
}
