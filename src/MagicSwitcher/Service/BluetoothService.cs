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

    /// <summary>
    /// A user is signed in and the session is unlocked. Only then the Apple keyboard top row gets the user's choice;
    /// on the sign-in and lock screens the driver keeps its default (media keys), because in the F1–F12 mode the driver
    /// can lose the release of Delete made with Fn+Backspace, and after Ctrl+Option+Fn+Backspace no keyboard types there.
    /// </summary>
    private volatile bool _sessionActive;

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

        if (trigger is ServiceTrigger.Logon or ServiceTrigger.Unlock or ServiceTrigger.Lock)
        {
            _sessionActive = trigger != ServiceTrigger.Lock;
            _ = EnsureAppleFnModeAsync(wait: true);
            if (trigger == ServiceTrigger.Lock)
            {
                return; // nothing to restore
            }
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

        // Shutting down (or the service stops): the next sign-in screen should start the keyboard in the driver's default.
        if (_config.AppleMediaKeys == false && AppleKeyboardDriver.MediaKeysByDefault == false)
        {
            try
            {
                AppleKeyboardDriver.WriteMediaKeysByDefault(true);
                Log.Info("Apple keyboard top row: default (media keys) for the next sign-in screen");
            }
            catch (Exception ex)
            {
                Log.Error("Failed to reset the Apple keyboard top row", ex);
            }
        }

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
                _sessionActive = true; // the tray app runs: a user is signed in
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
                _sessionActive = true; // asked from the tray app: a user is signed in
                _config.AppleMediaKeys = request.Media; // remembered, so it can be re-applied after the driver resets it
                SaveConfig(_config);
                bool restarted = await EnsureAppleFnModeAsync(wait: true) == true;
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

    /// <summary>
    /// Makes the driver's top row setting match the current state: the user's choice while a user session is active,
    /// the driver's default (media keys) on the sign-in and lock screens. Also re-applies it after the driver's INF
    /// reset it (whenever Windows installs the keyboard again). Returns true if a keyboard was restarted,
    /// false if only the setting was written, null if nothing had to change.
    /// </summary>
    private async Task<bool?> EnsureAppleFnModeAsync(bool wait = false)
    {
        if (_config.AppleMediaKeys is not { } choice)
        {
            return null;
        }

        if (wait)
        {
            await _fnGate.WaitAsync(_stopping.Token);
        }
        else if (!await _fnGate.WaitAsync(0))
        {
            return null;
        }

        try
        {
            bool wanted = _sessionActive ? choice : true;
            if (AppleKeyboardDriver.MediaKeysByDefault is not { } current || current == wanted)
            {
                return null;
            }

            AppleKeyboardDriver.WriteMediaKeysByDefault(wanted);
            string reason = _sessionActive ? "user session" : "sign-in/lock screen";
            var guarded = _guard?.Devices ?? [];
            var connected = guarded.Where(d => NativeBluetooth.GetState(d.Address).Connected).ToList();
            if (guarded.Count > 0 && connected.Count == 0)
            {
                // No restart: the driver reads the value when the keyboard connects.
                Log.Info($"Apple keyboard top row: media keys = {wanted} ({reason}); the keyboard is not connected, applies when it connects");
                return false;
            }

            var result = await AppleKeyboardDriver.RestartKeyboardsAsync(_stopping.Token);
            Log.Info($"Apple keyboard top row: media keys = {wanted} ({reason}), keyboard restart: {result}");
            foreach (var device in connected)
            {
                await EnsureHidAfterRestartAsync(device);
            }

            return result != AppleKeyboardDriver.RestartResult.NoDevice;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Failed to apply the Apple keyboard top row behavior", ex);
            return null;
        }
        finally
        {
            _fnGate.Release();
        }
    }

    /// <summary>
    /// A restarted keyboard may come back connected but without HID collections (pnputil: "System reboot is needed"):
    /// it does not type then. Reinstalling HID (like "Reconnect" in the tray) brings it back.
    /// </summary>
    private async Task EnsureHidAfterRestartAsync(GuardedDevice device)
    {
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(1000, _stopping.Token);
            if (NativeBluetooth.GetState(device.Address).Connected && await Task.Run(() => PairingGuard.CountHid(device)) is > 0)
            {
                return;
            }
        }

        if (!NativeBluetooth.GetState(device.Address).Connected)
        {
            return; // disconnected: the guard or the next connection takes care of it
        }

        int error = await Task.Run(() => NativeBluetooth.EnableServices(device.Address, device.Services));
        Log.Info($"[{device.Name}] no HID after the keyboard restart: services reinstalled -> {error}");
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
