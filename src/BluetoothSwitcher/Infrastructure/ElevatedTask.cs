using System.Diagnostics;

namespace BluetoothSwitcher.Infrastructure;

/// <summary>
/// Legacy: versions before 1.1 ran the app elevated without a UAC prompt through a Task Scheduler task.
/// The Windows service replaced it, so the task is only removed now (by the elevated instance it starts,
/// which needs no UAC prompt for that); a logon trigger becomes the usual Run-key autostart.
/// </summary>
internal static class ElevatedTask
{
    public const string TaskName = "BluetoothSwitcher";

    /// <summary>Command-line argument the task passes, so a non-elevated start through it does not loop.</summary>
    public const string LaunchArgument = "--from-task";

    private static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public static bool Exists() => QueryXml(out _);

    /// <summary>Starts the task (no UAC prompt). Returns false if it could not be started.</summary>
    public static bool Run()
    {
        bool ok = RunHidden($"/Run /TN \"{TaskName}\"", out _) == 0;
        Log.Info($"Legacy elevated task: run -> {ok}");
        return ok;
    }

    /// <summary>Deletes the task (must be elevated) and keeps "start with Windows" through the Run key.</summary>
    public static void MigrateToRunKey()
    {
        if (!QueryXml(out string xml))
        {
            return;
        }

        bool atLogon = xml.Contains("<LogonTrigger", StringComparison.OrdinalIgnoreCase);
        int exitCode = RunHidden($"/Delete /TN \"{TaskName}\" /F", out _);
        Log.Info($"Legacy elevated task removed -> {exitCode}; it started the app at logon: {atLogon}");
        if (exitCode == 0 && atLogon)
        {
            try
            {
                AutoStart.Set(true);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to turn on autostart", ex);
            }
        }
    }

    private static bool QueryXml(out string xml)
    {
        int exitCode = RunHidden($"/Query /TN \"{TaskName}\" /XML", out string output);
        // The XML may come back as UTF-16 bytes read as a narrow encoding: drop the zero chars before searching.
        xml = output.Replace("\0", "");
        return exitCode == 0;
    }

    private static int RunHidden(string arguments, out string output)
    {
        output = "";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Schtasks, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return -1;
            }

            var errors = process.StandardError.ReadToEndAsync();
            output = process.StandardOutput.ReadToEnd();
            _ = errors.Result;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error($"schtasks {arguments} failed", ex);
            return -1;
        }
    }
}
