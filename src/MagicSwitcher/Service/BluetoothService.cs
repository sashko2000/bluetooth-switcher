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
    private readonly SemaphoreSlim _fnGate = new(1, 1);
    private PairingGuard? _guard;
    private Task? _pipe;
    private System.Threading.Timer? _fnTimer;
    private ServiceConfig _config = new();

    public static string ConfigPath { get; } = Path.Combine(AppPaths.MachineDataDirectory, "service.json");

    public void Start()
    {
        Log.Info($"Service {AppInfo.Version} starting ({Environment.ProcessPath})");
        _config = LoadConfig();
        _guard = new PairingGuard { Devices = _config.Devices, AfterPairing = () => _ = ReapplyAppleFnAfterPairingAsync() };
        Log.Info($"Restoring: {Describe(_guard.Devices)}; Apple top row: {DescribeFn(_config.AppleMediaKeys)}");
        _pipe = Task.Run(() => ServicePipe.ServeAsync(HandleAsync, _stopping.Token));
        OnTrigger(ServiceTrigger.Startup);

        // The Apple driver's INF resets the top row behavior whenever Windows installs the keyboard again
        // (e.g. after it was paired again): check every 30 seconds and re-apply the user's choice.
        _fnTimer = new System.Threading.Timer(_ => _ = EnsureAppleFnModeAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
    }

    public void OnTrigger(ServiceTrigger trigger)
    {
        var guard = _guard;
        if (guard is null)
        {
            return;
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
        _fnTimer?.Dispose();
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
                if (request.AppleMediaKeys is { } media)
                {
                    _config.AppleMediaKeys = media;
                }

                SaveConfig(_config);
                guard.Devices = devices;
                Log.Info($"Configured: {Describe(devices)}; Apple top row: {DescribeFn(_config.AppleMediaKeys)}");
                _ = EnsureAppleFnModeAsync();
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
                Log.Info($"Request: Apple keyboard media keys by default = {request.Media}");
                bool restarted;
                await _fnGate.WaitAsync(ct);
                try
                {
                    restarted = await AppleKeyboardDriver.SetMediaKeysByDefaultAsync(request.Media, ct);
                    _config.AppleMediaKeys = request.Media; // remembered, so it can be re-applied after the driver resets it
                    SaveConfig(_config);
                }
                finally
                {
                    _fnGate.Release();
                }

                return ServiceResponse.Success(restarted ? 1 : 0);
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
    /// After pairing again Windows installs the keyboard anew and the driver's INF resets the top row a moment later:
    /// check a few times soon after, so the keyboard restart happens before the user starts typing.
    /// </summary>
    private async Task ReapplyAppleFnAfterPairingAsync()
    {
        try
        {
            foreach (int seconds in new[] { 3, 5, 10, 20 })
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), _stopping.Token);
                await EnsureAppleFnModeAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Re-applies the Apple keyboard top row behavior if the driver's registry value no longer matches it.</summary>
    private async Task EnsureAppleFnModeAsync()
    {
        if (_config.AppleMediaKeys is not { } wanted || !await _fnGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (AppleKeyboardDriver.MediaKeysByDefault is { } current && current != wanted)
            {
                Log.Info($"Apple keyboard top row was reset by the driver (media keys = {current}): applying media keys = {wanted}");
                bool restarted = await AppleKeyboardDriver.SetMediaKeysByDefaultAsync(wanted, _stopping.Token);
                Log.Info($"Apple keyboard top row re-applied, keyboard restarted: {restarted}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Failed to re-apply the Apple keyboard top row behavior", ex);
        }
        finally
        {
            _fnGate.Release();
        }
    }

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

    private static string DescribeFn(bool? media) => media switch
    {
        true => "media keys",
        false => "F1–F12",
        null => "not set",
    };

    private static string Describe(IReadOnlyList<GuardedDevice> devices) => devices.Count == 0
        ? "no devices"
        : string.Join(", ", devices.Select(d => $"'{d.Name}' ({BluetoothAddress.Format(d.Address)})"));
}

/// <summary>%ProgramData%\MagicSwitcher\service.json.</summary>
internal sealed class ServiceConfig
{
    public List<GuardedDevice> Devices { get; set; } = [];

    /// <summary>Apple keyboard top row: true = media keys by default, false = F1–F12; null = never chosen.</summary>
    public bool? AppleMediaKeys { get; set; }
}
