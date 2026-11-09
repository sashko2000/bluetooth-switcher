namespace MagicSwitcher.Infrastructure;

internal static class AppPaths
{
    /// <summary>Per-user data of the tray app: %AppData%\MagicSwitcher.</summary>
    public static string UserDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MagicSwitcher");

    /// <summary>Data of the Windows service (it cannot see the user's profile): %ProgramData%\MagicSwitcher.</summary>
    public static string MachineDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MagicSwitcher");

    /// <summary>Where this process keeps its settings and log.</summary>
    public static string DataDirectory { get; private set; } = UserDataDirectory;

    /// <summary>Called first thing in service mode.</summary>
    public static void UseMachineData() => DataDirectory = MachineDataDirectory;
}
