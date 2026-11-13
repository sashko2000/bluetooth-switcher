using System.Text.Json;
using MagicSwitcher.Bluetooth;
using MagicSwitcher.Infrastructure;
using MagicSwitcher.Keyboard;

namespace MagicSwitcher.Service;

/// <summary>
/// The Windows service (runs as LocalSystem from %ProgramFiles%\MagicSwitcher): restores the selected
/// devices at startup/sign-in (<see cref="PairingGuard"/>) and performs actions that need administrator
/// rights for the tray app, which runs without them.
/// </summary>
internal sealed class BluetoothService
{
    private const int MaxDevices = 16;

    private readonly CancellationTokenSource _stopping = new();
    private PairingGuard? _guard;
    private Task? _pipe;
    private System.Threading.Timer? _helperTimer;
    private ServiceConfig _config = new();
    private readonly RemapHelper _remapHelper = new();

    public static string ConfigPath { get; } = Path.Combine(AppPaths.MachineDataDirectory, "service.json");

    public void Start()
    {
        Log.Info($"Service {AppInfo.Version} starting ({Environment.ProcessPath})");
        _config = LoadConfig();
        _guard = new PairingGuard { Devices = _config.Devices };
        MigrateTopRow();
        Log.Info($"Restoring: {Describe(_guard.Devices)}; Apple top row: {DescribeTopRow()}");
        _pipe = Task.Run(() => ServicePipe.ServeAsync(HandleAsync, _stopping.Token));
        OnTrigger(ServiceTrigger.Startup);

        // The key remapper helper: start it if a user is signed in already (service restarted), and every 30 seconds
        // start it again if it died or the console session changed.
        UpdateRemapHelper(restart: false);
        _helperTimer = new System.Threading.Timer(_ => UpdateRemapHelper(restart: false), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
    }

    public void OnTrigger(ServiceTrigger trigger)
    {
        var guard = _guard;
        if (guard is null)
        {
            return;
        }

        if (trigger is ServiceTrigger.Logon or ServiceTrigger.Unlock)
        {
            _ = Task.Run(() => UpdateRemapHelper(restart: false));
        }

        if (trigger == ServiceTrigger.Lock)
        {
            return; // nothing to restore
        }

        _ = Task.Run(() =>
        {
            try
            {
                guard.Trigger(trigger);
            }
            catch (Exception ex)
            {
                Log.Error($"Trigger {trigger} failed", ex);
            }
        });
    }

    public void Stop()
    {
        _helperTimer?.Dispose();
        _remapHelper.Stop();

        _stopping.Cancel();
        _guard?.Dispose();
        try
        {
            _pipe?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }
    }

    private async Task<ServiceResponse> HandleAsync(ServiceRequest request, Action<string> progress, CancellationToken ct)
    {
        var guard = _guard!;
        switch (request.Command)
        {
            case ServiceCommands.Ping:
                return new ServiceResponse { Ok = true, Version = AppInfo.Version };

            case ServiceCommands.Configure:
            {
                var devices = (request.Devices ?? [])
                    .Where(d => d.Address is > 0 and <= 0xFFFFFFFFFFFF)
                    .DistinctBy(d => d.Address)
                    .Take(MaxDevices)
                    .ToList();
                _config.Devices = devices;
                bool keyMapChanged = false;
                if (request.KeyMap is { } keyMap)
                {
                    var newMap = keyMap.DistinctBy(m => m.From).Take(16).ToList();
                    keyMapChanged = !newMap.SequenceEqual(_config.KeyMap);
                    _config.KeyMap = newMap;
                }

                SaveConfig(_config);
                guard.Devices = devices;
                Log.Info($"Configured: {Describe(devices)}; Apple top row: {DescribeTopRow()}; " +
                         $"remapped keys: {(_config.KeyMap.Count == 0 ? "none" : string.Join(", ", _config.KeyMap.Select(m => $"{m.From}->{m.To}")))}");
                UpdateRemapHelper(restart: keyMapChanged);
                return ServiceResponse.Success();
            }

            case ServiceCommands.Reinstall:
            {
                if (request.Address is 0 or > 0xFFFFFFFFFFFF)
                {
                    return ServiceResponse.Failure("Invalid address.");
                }

                Log.Info($"Request: reinstall services of {BluetoothAddress.Format(request.Address)} (full = {request.Full})");
                int error = await Task.Run(() => NativeBluetooth.ReinstallServices(request.Address, TimeSpan.FromSeconds(1), request.Services, request.Full), ct);
                return new ServiceResponse { Ok = error == 0, Code = error };
            }

            case ServiceCommands.Pull:
            {
                // Pairing is confirmed automatically only for devices the user selected for restoring.
                var device = guard.Devices.FirstOrDefault(d => d.Address == request.Address);
                if (device is null)
                {
                    return ServiceResponse.Failure("The device is not selected for restoring.", 1168); // ERROR_NOT_FOUND
                }

                var duration = TimeSpan.FromSeconds(Math.Clamp(request.Seconds, 15, 300));
                bool working = await guard.PullAsync(device, duration, request.Force, progress).WaitAsync(ct);
                return new ServiceResponse { Ok = working, Code = working ? 0 : NativeBluetooth.ErrorTimeout };
            }

            case ServiceCommands.AppleInstall:
            {
                Log.Info("Request: install the Apple keyboard driver");
                var stages = new Progress<AppleKeyboardDriver.InstallStage>(stage => progress(stage.ToString()));
                var result = await AppleKeyboardDriver.InstallAsync(stages, ct);
                return ServiceResponse.Success((int)result);
            }

            case ServiceCommands.AppleFn:
            {
                // The top row mode (Fn Lock) is the remapper's: write its state and restart the helper to pick it up.
                Log.Info($"Request: Apple keyboard top row = {(request.Media ? "media keys" : "F-keys")}");
                AppleTopRow.Write(AppleTopRow.MachinePath, fKeys: !request.Media);
                UpdateRemapHelper(restart: true);
                return ServiceResponse.Success(1);
            }

            case ServiceCommands.RestartAudio:
            {
                Log.Info("Request: restart Windows audio");
                int error = await Task.Run(WindowsAudio.Restart, ct);
                return new ServiceResponse { Ok = error == 0, Code = error };
            }

            default:
                return ServiceResponse.Failure($"Unknown command '{request.Command}'.");
        }
    }

    /// <summary>
    /// The helper runs for the key mappings and, when Apple's keyboard driver is installed, for the top row (Fn Lock
    /// and its Fn+Esc toggle).
    /// </summary>
    private void UpdateRemapHelper(bool restart)
    {
        try
        {
            _remapHelper.Update(_config.KeyMap.Count > 0 || AppleKeyboardDriver.IsInstalled, restart);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update the key remapper", ex);
        }
    }

    /// <summary>
    /// Versions up to 1.3 switched the driver itself between media keys and F-keys (OSXFnBehavior) and restarted the
    /// keyboard for it, which could leave it without input after sleep. Now the driver always stays in its default mode
    /// and the remapper does the swap: carry the old choice over once and put the driver back to its default
    /// (no restart: it reads the value the next time the keyboard starts).
    /// </summary>
    private void MigrateTopRow()
    {
        try
        {
            if (!AppleTopRow.Exists(AppleTopRow.MachinePath))
            {
                AppleTopRow.Write(AppleTopRow.MachinePath, fKeys: _config.AppleMediaKeys == false);
            }

            if (AppleKeyboardDriver.MediaKeysByDefault == false)
            {
                AppleKeyboardDriver.WriteMediaKeysByDefault(true);
                Log.Info("Apple keyboard driver set back to its default top row mode (applies when the keyboard starts next)");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to migrate the Apple top row setting", ex);
        }
    }

    private static string DescribeTopRow() => AppleTopRow.ReadFKeys(AppleTopRow.MachinePath) ? "F-keys" : "media keys";

    /// <summary>Key mappings from the service configuration (for the remapper helper process).</summary>
    public static List<KeyMapping> LoadKeyMap() => LoadConfig().KeyMap;

    private static ServiceConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                // Version 1.1 stored just the list of devices.
                return json.TrimStart().StartsWith('[')
                    ? new ServiceConfig { Devices = JsonSerializer.Deserialize<List<GuardedDevice>>(json) ?? [] }
                    : JsonSerializer.Deserialize<ServiceConfig>(json) ?? new ServiceConfig();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read the service configuration", ex);
        }

        return new ServiceConfig();
    }

    private static void SaveConfig(ServiceConfig config)
    {
        Directory.CreateDirectory(AppPaths.MachineDataDirectory);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    private static string Describe(IReadOnlyList<GuardedDevice> devices) => devices.Count == 0
        ? "no devices"
        : string.Join(", ", devices.Select(d => $"'{d.Name}' ({BluetoothAddress.Format(d.Address)})"));
}

/// <summary>%ProgramData%\MagicSwitcher\service.json.</summary>
internal sealed class ServiceConfig
{
    public List<GuardedDevice> Devices { get; set; } = [];

    /// <summary>Legacy (up to 1.3): the top row mode set in the driver. Migrated to <see cref="AppleTopRow"/> once.</summary>
    public bool? AppleMediaKeys { get; set; }

    /// <summary>Keys remapped on Apple keyboards (see KeyRemapper / RemapHelper).</summary>
    public List<KeyMapping> KeyMap { get; set; } = [];
}
