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

    public static string ConfigPath { get; } = Path.Combine(AppPaths.MachineDataDirectory, "service.json");

    public void Start()
    {
        Log.Info($"Service {AppInfo.Version} starting ({Environment.ProcessPath})");
        _guard = new PairingGuard { Devices = LoadDevices() };
        Log.Info($"Restoring: {Describe(_guard.Devices)}");
        _pipe = Task.Run(() => ServicePipe.ServeAsync(HandleAsync, _stopping.Token));
        OnTrigger(ServiceTrigger.Startup);
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
                SaveDevices(devices);
                guard.Devices = devices;
                Log.Info($"Configured: {Describe(devices)}");
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
                bool restarted = await AppleKeyboardDriver.SetMediaKeysByDefaultAsync(request.Media, ct);
                return ServiceResponse.Success(restarted ? 1 : 0);
            }

            default:
                return ServiceResponse.Failure($"Unknown command '{request.Command}'.");
        }
    }

    private static List<GuardedDevice> LoadDevices()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                return JsonSerializer.Deserialize<List<GuardedDevice>>(File.ReadAllText(ConfigPath)) ?? [];
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read the service configuration", ex);
        }

        return [];
    }

    private static void SaveDevices(List<GuardedDevice> devices)
    {
        Directory.CreateDirectory(AppPaths.MachineDataDirectory);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(devices, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    private static string Describe(IReadOnlyList<GuardedDevice> devices) => devices.Count == 0
        ? "no devices"
        : string.Join(", ", devices.Select(d => $"'{d.Name}' ({BluetoothAddress.Format(d.Address)})"));
}
