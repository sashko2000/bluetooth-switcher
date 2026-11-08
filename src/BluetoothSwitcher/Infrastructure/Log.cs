namespace BluetoothSwitcher.Infrastructure;

/// <summary>Простой файловый лог в %AppData%\BluetoothSwitcher\log.txt.</summary>
internal static class Log
{
    private static readonly Lock Sync = new();
    private const long MaxSize = 1024 * 1024;

    public static string FilePath { get; } = Path.Combine(AppPaths.DataDirectory, "log.txt");

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
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxSize)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Логирование никогда не должно ронять приложение.
        }
    }
}
