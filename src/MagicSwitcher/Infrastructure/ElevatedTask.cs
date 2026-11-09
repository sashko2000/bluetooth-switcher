using System.Diagnostics;

namespace MagicSwitcher.Infrastructure;

/// <summary>
/// Legacy: Bluetooth Switcher 1.0 ran the app elevated without a UAC prompt through a Task Scheduler task.
/// The Windows service replaced it, so the task is only removed now; a logon trigger becomes the usual Run-key autostart.
/// </summary>
internal static class ElevatedTask
{
    public const string TaskName = "BluetoothSwitcher"; // the app was called Bluetooth Switcher then

    private static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public static bool Exists() => QueryXml(out _);

    /// <summary>Deletes the task (UAC prompt if not elevated) and keeps "start with Windows" through the Run key.</summary>
    public static void Remove()
    {
        if (!QueryXml(out string xml))
        {
            return;
        }

        bool atLogon = xml.Contains("<LogonTrigger", StringComparison.OrdinalIgnoreCase);
        int exitCode;
        try
        {
            string arguments = $"/Delete /TN \"{TaskName}\" /F";
            exitCode = Elevation.IsElevated
                ? RunHidden(arguments, out _)
                : Elevation.RunAsAdminAsync(Schtasks, arguments).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Log.Info("Legacy elevated task: removal declined, will ask again next time");
            return;
        }

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
