using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace MagicSwitcher.Infrastructure;

internal static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Runs a tool with administrator rights: directly if the app is already elevated, otherwise via a UAC prompt.
    /// Throws <see cref="OperationCanceledException"/> if the prompt is declined.
    /// </summary>
    public static async Task<int> RunAsAdminAsync(string fileName, string arguments, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        if (IsElevated)
        {
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
        }
        else
        {
            info.UseShellExecute = true;
            info.Verb = "runas";
        }

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC prompt declined
        {
            throw new OperationCanceledException(ex.Message, ex);
        }

        using (process)
        {
            if (process is null)
            {
                throw new InvalidOperationException($"Could not start {Path.GetFileName(fileName)}.");
            }

            if (info.RedirectStandardOutput)
            {
                string output = await process.StandardOutput.ReadToEndAsync(ct);
                Log.Info($"{Path.GetFileName(fileName)} {arguments}{Environment.NewLine}{output.Trim()}");
            }

            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
    }
}
