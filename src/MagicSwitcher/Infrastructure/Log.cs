namespace MagicSwitcher.Infrastructure;

/// <summary>Simple file log: log.txt in %AppData%\MagicSwitcher (tray) or service-log.txt in %ProgramData%\MagicSwitcher (service).</summary>
internal static class Log
{
    private static readonly Lock Sync = new();
    private const long MaxSize = 1024 * 1024;

    public static string FilePath => AppPaths.DataDirectory == AppPaths.MachineDataDirectory ? ServiceLogPath : Path.Combine(AppPaths.DataDirectory, "log.txt");

    /// <summary>The service log (readable by users, so the tray can open it).</summary>
    public static string ServiceLogPath { get; } = Path.Combine(AppPaths.MachineDataDirectory, "service-log.txt");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                string path = FilePath;
                var file = new FileInfo(path);
                if (file.Exists && file.Length > MaxSize)
                {
                    File.Move(path, path + ".old", overwrite: true);
                }

                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the application.
        }
    }
}
