using System.Text.Json;
using BluetoothSwitcher.Bluetooth;

namespace BluetoothSwitcher.Infrastructure;

public sealed class AppSettings
{
    /// <summary>Устройства, которые показываются в меню трея.</summary>
    public List<SavedDevice> Devices { get; set; } = [];

    /// <summary>Сколько секунд ждать подключения после переустановки сервиса.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 20;

    public bool ShowNotifications { get; set; } = true;

    public AppSettings Clone() => new()
    {
        Devices = Devices.Select(d => d with { Services = d.Services?.ToList() }).ToList(),
        ConnectTimeoutSeconds = ConnectTimeoutSeconds,
        ShowNotifications = ShowNotifications,
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
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось прочитать настройки", ex);
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
