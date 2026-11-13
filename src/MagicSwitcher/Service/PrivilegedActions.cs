using System.Globalization;
using MagicSwitcher.Bluetooth;
using MagicSwitcher.Infrastructure;
using MagicSwitcher.Keyboard;

namespace MagicSwitcher.Service;

/// <summary>
/// Actions that need administrator rights, as seen from the tray app (which runs without them):
/// through the service when it is running, otherwise with a UAC prompt.
/// </summary>
internal static class PrivilegedActions
{
    /// <summary>Elevated one-shot helper: <c>--reinstall-services &lt;address&gt; &lt;full 0|1&gt; [guid,guid…]</c>.</summary>
    public const string ReinstallArgument = "--reinstall-services";

    /// <summary>Version of the running service, or null if it is not running.</summary>
    public static async Task<string?> GetServiceVersionAsync()
    {
        if (ServiceInstaller.GetState() != ServiceState.Running)
        {
            return null;
        }

        try
        {
            var response = await ServicePipe.SendAsync(new ServiceRequest { Command = ServiceCommands.Ping }, TimeSpan.FromSeconds(5),
                connectTimeout: TimeSpan.FromSeconds(1));
            return response?.Version;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Tells the service which devices to restore. Returns false if the service is not running.</summary>
    public static async Task<bool> ConfigureServiceAsync(IEnumerable<SavedDevice> devices, bool? appleMediaKeys, List<KeyMapping>? keyMap)
    {
        if (ServiceInstaller.GetState() != ServiceState.Running)
        {
            return false;
        }

        var request = new ServiceRequest
        {
            Command = ServiceCommands.Configure,
            Devices = devices
                .Where(d => d.RestoreAtStartup && !d.IsLowEnergy)
                .Select(d => new GuardedDevice { Address = d.Address, Name = d.DisplayName, Services = ServicesAfterPairing(d) })
                .ToList(),
            AppleMediaKeys = appleMediaKeys,
            KeyMap = keyMap,
        };

        try
        {
            var response = await ServicePipe.SendAsync(request, TimeSpan.FromSeconds(10), connectTimeout: TimeSpan.FromSeconds(1));
            return response?.Ok == true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            Log.Error("Failed to configure the service", ex);
            return false;
        }
    }

    /// <summary>The services selected for the device; null (automatic) means HID.</summary>
    private static List<Guid>? ServicesAfterPairing(SavedDevice device) =>
        device.Services is { Count: > 0 } services ? services : null;

    /// <summary>
    /// Asks the service to bring the device here now, pairing again without the Windows dialog if needed.
    /// Returns null if the service is not running.
    /// </summary>
    public static async Task<ServiceResponse?> PullAsync(SavedDevice device, TimeSpan duration, IProgress<string>? progress, CancellationToken ct = default)
    {
        if (ServiceInstaller.GetState() != ServiceState.Running)
        {
            return null;
        }

        // Force: the tray only asks when the device does not work here, even if Windows still shows it as connected
        // (a keyboard taken over by another host can look connected until Windows notices).
        var request = new ServiceRequest { Command = ServiceCommands.Pull, Address = device.Address, Seconds = (int)duration.TotalSeconds, Force = true };
        return await ServicePipe.SendAsync(request, duration + TimeSpan.FromSeconds(30), progress, TimeSpan.FromSeconds(1), ct);
    }

    /// <summary>Reinstalls services of a Classic device. Returns a Win32 error code.</summary>
    public static async Task<int> ReinstallServicesAsync(ulong address, IReadOnlyCollection<Guid>? services, bool full, CancellationToken ct)
    {
        if (ServiceInstaller.GetState() == ServiceState.Running)
        {
            try
            {
                var request = new ServiceRequest
                {
                    Command = ServiceCommands.Reinstall,
                    Address = address,
                    Services = services?.ToList(),
                    Full = full,
                };
                var response = await ServicePipe.SendAsync(request, TimeSpan.FromMinutes(2), connectTimeout: TimeSpan.FromSeconds(2), ct: ct);
                if (response is not null)
                {
                    return response.Code;
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                Log.Error("The service failed to reinstall services; trying here", ex);
            }
        }

        // No service: try without rights first (often allowed), then once with a UAC prompt.
        int error = await Task.Run(() => NativeBluetooth.ReinstallServices(address, TimeSpan.FromSeconds(1), services, full), ct);
        if (error != NativeBluetooth.ErrorAccessDenied || Elevation.IsElevated || Environment.ProcessPath is not { } exe)
        {
            return error;
        }

        string guids = services is { Count: > 0 } ? " " + string.Join(',', services) : "";
        try
        {
            return await Elevation.RunAsAdminAsync(exe, $"{ReinstallArgument} {address:X12} {(full ? 1 : 0)}{guids}", ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return NativeBluetooth.ErrorAccessDenied; // UAC prompt declined
        }
    }

    /// <summary>Runs in the elevated helper process started by <see cref="ReinstallServicesAsync"/>.</summary>
    public static int RunReinstallElevated(string[] args)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, ReinstallArgument, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 2 >= args.Length
            || !ulong.TryParse(args[i + 1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address))
        {
            return 87; // ERROR_INVALID_PARAMETER
        }

        bool full = args[i + 2] == "1";
        var services = i + 3 < args.Length
            ? args[i + 3].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList()
            : null;
        return NativeBluetooth.ReinstallServices(address, TimeSpan.FromSeconds(1), services, full);
    }

    public static async Task<AppleKeyboardDriver.InstallResult> InstallAppleDriverAsync(IProgress<AppleKeyboardDriver.InstallStage> progress)
    {
        var stages = new Progress<string>(text =>
        {
            if (Enum.TryParse<AppleKeyboardDriver.InstallStage>(text, out var stage))
            {
                progress.Report(stage);
            }
        });

        var response = await SendOrNullAsync(new ServiceRequest { Command = ServiceCommands.AppleInstall }, TimeSpan.FromMinutes(10), stages);
        if (response is null)
        {
            return await AppleKeyboardDriver.InstallAsync(progress); // with a UAC prompt
        }

        return response.Ok
            ? (AppleKeyboardDriver.InstallResult)response.Code
            : throw new InvalidOperationException(response.Message);
    }

    /// <summary>Returns true if a keyboard was restarted (the new behavior is already active).</summary>
    public static async Task<bool> SetAppleMediaKeysByDefaultAsync(bool media)
    {
        // The top row mode (Fn Lock) belongs to the key remapper: the service's helper, or the one in the tray app.
        var response = await SendOrNullAsync(new ServiceRequest { Command = ServiceCommands.AppleFn, Media = media }, TimeSpan.FromSeconds(30));
        if (response is null)
        {
            AppleTopRow.Write(AppleTopRow.UserPath, fKeys: !media); // the tray restarts its remapper when settings close
            return true;
        }

        return response.Ok ? true : throw new InvalidOperationException(response.Message);
    }

    /// <summary>Windows audio can be restarted without a UAC prompt (through the service, or this process is elevated).</summary>
    public static bool CanRestartWindowsAudioSilently =>
        ServiceInstaller.GetState() == ServiceState.Running || Elevation.IsElevated;

    /// <summary>Restarts the Windows audio services: through the service, or with a UAC prompt. Returns a Win32 error code.</summary>
    public static async Task<int> RestartWindowsAudioAsync()
    {
        try
        {
            var response = await SendOrNullAsync(new ServiceRequest { Command = ServiceCommands.RestartAudio }, TimeSpan.FromMinutes(2));
            if (response is not null)
            {
                return response.Code;
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            Log.Error("The service failed to restart Windows audio; trying with UAC", ex);
        }

        if (Elevation.IsElevated)
        {
            return await Task.Run(WindowsAudio.Restart);
        }

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        return await Elevation.RunAsAdminAsync(exe, WindowsAudio.RestartArgument); // throws OperationCanceledException if declined
    }

    private static async Task<ServiceResponse?> SendOrNullAsync(ServiceRequest request, TimeSpan timeout, IProgress<string>? progress = null) =>
        ServiceInstaller.GetState() == ServiceState.Running
            ? await ServicePipe.SendAsync(request, timeout, progress, TimeSpan.FromSeconds(2))
            : null;
}
