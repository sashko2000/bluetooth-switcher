using System.Text.Json;
using MagicSwitcher.Bluetooth;

namespace MagicSwitcher.Infrastructure;

public sealed class AppSettings
{
    /// <summary>Devices shown in the tray menu.</summary>
    public List<SavedDevice> Devices { get; set; } = [];

    /// <summary>How many seconds to wait for a connection after reinstalling services.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 20;

    public bool ShowNotifications { get; set; } = true;

    /// <summary>Warn when a connected device's battery drops to this level, in percent; 0 turns the warning off.</summary>
    public int LowBatteryThreshold { get; set; } = 15;

    /// <summary>Notify when a connected Bluetooth speaker or headset has lost its microphone.</summary>
    public bool WatchMicrophone { get; set; } = true;

    /// <summary>Restore a lost microphone without asking (restarts Windows audio, which interrupts sound for a few seconds).</summary>
    public bool RestoreMicrophoneAutomatically { get; set; }

    /// <summary>UI language code ("en", "ru", "uk"); empty means the Windows display language.</summary>
    public string Language { get; set; } = "";

    /// <summary>Apple keyboard top row chosen in settings (true = media keys); null if never changed. The service re-applies it.</summary>
    public bool? AppleMediaKeysByDefault { get; set; }

    /// <summary>Keys remapped on Apple keyboards only.</summary>
    public List<MagicSwitcher.Keyboard.KeyMapping> AppleKeyMap { get; set; } = [];

    public AppSettings Clone() => new()
    {
        Devices = Devices.Select(d => d with { Services = d.Services?.ToList() }).ToList(),
        ConnectTimeoutSeconds = ConnectTimeoutSeconds,
        ShowNotifications = ShowNotifications,
        LowBatteryThreshold = LowBatteryThreshold,
        WatchMicrophone = WatchMicrophone,
        RestoreMicrophoneAutomatically = RestoreMicrophoneAutomatically,
        Language = Language,
        AppleMediaKeysByDefault = AppleMediaKeysByDefault,
        AppleKeyMap = AppleKeyMap.ToList(),
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
