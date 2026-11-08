using System.Text.Json;
using BluetoothSwitcher.Bluetooth;

namespace BluetoothSwitcher.Infrastructure;

public sealed class AppSettings
{
    /// <summary>Devices shown in the tray menu.</summary>
    public List<SavedDevice> Devices { get; set; } = [];

    /// <summary>How many seconds to wait for a connection after reinstalling services.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 20;

    public bool ShowNotifications { get; set; } = true;

    /// <summary>Warn when a connected device's battery drops to this level, in percent; 0 turns the warning off.</summary>
    public int LowBatteryThreshold { get; set; } = 15;

    /// <summary>UI language code ("en", "ru", "uk"); empty means the Windows display language.</summary>
    public string Language { get; set; } = "";

    public AppSettings Clone() => new()
    {
        Devices = Devices.Select(d => d with { Services = d.Services?.ToList() }).ToList(),
        ConnectTimeoutSeconds = ConnectTimeoutSeconds,
        ShowNotifications = ShowNotifications,
        LowBatteryThreshold = LowBatteryThreshold,
        Language = Language,
    };
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(AppPaths.DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (settings is not null)
                {
                    settings.ConnectTimeoutSeconds = Math.Clamp(settings.ConnectTimeoutSeconds, 5, 120);
                    settings.LowBatteryThreshold = Math.Clamp(settings.LowBatteryThreshold, 0, 50);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read settings", ex);
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
