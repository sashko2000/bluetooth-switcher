using Microsoft.Win32;

namespace MagicSwitcher.Infrastructure;

/// <summary>
/// The app was called "Bluetooth Switcher" before 1.1. Its settings, autostart entry and service are moved over
/// to the new name: the tray app does the per-user part at start, the service installer removes the old service.
/// </summary>
internal static class LegacyMigration
{
    public const string OldName = "BluetoothSwitcher";
    public const string OldServiceName = "BluetoothSwitcher";

    public static string OldUserDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OldName);

    public static string OldMachineDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), OldName);

    public static string OldInstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Bluetooth Switcher");

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Copies the old settings (if there are no new ones yet) and moves the autostart entry.</summary>
    public static void MigrateUserData()
    {
        try
        {
            string oldSettings = Path.Combine(OldUserDataDirectory, "settings.json");
            if (!File.Exists(SettingsStore.FilePath) && File.Exists(oldSettings))
            {
                Directory.CreateDirectory(AppPaths.UserDataDirectory);
                File.Copy(oldSettings, SettingsStore.FilePath);
                Log.Info($"Settings copied from {OldUserDataDirectory}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to copy the old settings", ex);
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(OldName) is not null)
            {
                key.DeleteValue(OldName, throwOnMissingValue: false);
                AutoStart.Set(true);
                Log.Info("Autostart entry moved to the new name");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to move the autostart entry", ex);
        }
    }
}
