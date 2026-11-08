using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace BluetoothSwitcher.Infrastructure;

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
    /// Starts a new elevated instance of the application.
    /// Returns false if the user cancelled the UAC prompt.
    /// </summary>
    public static bool TryRestartElevated()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = Program.RestartArgument,
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }
}
