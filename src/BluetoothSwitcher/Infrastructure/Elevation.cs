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
    /// Запускает новый экземпляр приложения с повышенными правами.
    /// Возвращает false, если пользователь отменил запрос UAC.
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
