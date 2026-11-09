namespace BluetoothSwitcher.Infrastructure;

internal static class AppPaths
{
    /// <summary>Per-user data of the tray app: %AppData%\BluetoothSwitcher.</summary>
    public static string UserDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BluetoothSwitcher");

    /// <summary>Data of the Windows service (it cannot see the user's profile): %ProgramData%\BluetoothSwitcher.</summary>
    public static string MachineDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BluetoothSwitcher");

    /// <summary>Where this process keeps its settings and log.</summary>
    public static string DataDirectory { get; private set; } = UserDataDirectory;

    /// <summary>Called first thing in service mode.</summary>
    public static void UseMachineData() => DataDirectory = MachineDataDirectory;
}
