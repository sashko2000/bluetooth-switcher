using System.Diagnostics;
using MagicSwitcher.Infrastructure;
using MagicSwitcher.Localization;
using MagicSwitcher.Service;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace MagicSwitcher.Bluetooth;

public static class BluetoothManager
{
    private const uint CodMajorPeripheral = 0x05;
    private const uint CodKeyboardBit = 0x40;
    private const uint CodPointingBit = 0x80;
    private const ushort LeAppearanceKeyboard = 0x03C1;

    /// <summary>All devices paired with this computer (Classic and BLE).</summary>
    public static async Task<IReadOnlyList<PairedDeviceInfo>> GetPairedDevicesAsync()
    {
        var result = new List<PairedDeviceInfo>();

        var classic = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
        foreach (var di in classic)
        {
            try
            {
                using var device = await BluetoothDevice.FromIdAsync(di.Id);
                if (device is null)
                {
                    continue;
                }

                uint cod = device.ClassOfDevice.RawValue;
                bool isPeripheral = ((cod >> 8) & 0x1F) == CodMajorPeripheral;
                bool isKeyboard = isPeripheral && (cod & CodKeyboardBit) != 0;
                string kind = !isPeripheral ? DescribeMajorClass(device.ClassOfDevice.MajorClass)
                    : isKeyboard && (cod & CodPointingBit) != 0 ? Strings.KindKeyboardMouse
                    : isKeyboard ? Strings.KindKeyboard
                    : (cod & CodPointingBit) != 0 ? Strings.KindMouse
                    : Strings.KindPeripheral;

                result.Add(new PairedDeviceInfo(
                    device.BluetoothAddress,
                    string.IsNullOrWhiteSpace(device.Name) ? di.Name : device.Name,
                    IsLowEnergy: false,
                    IsConnected: device.ConnectionStatus == BluetoothConnectionStatus.Connected,
                    isKeyboard,
                    kind));
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to read Classic device {di.Name}", ex);
            }
        }

        var lowEnergy = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        foreach (var di in lowEnergy)
        {
            try
            {
                using var device = await BluetoothLEDevice.FromIdAsync(di.Id);
                if (device is null)
                {
                    continue;
                }

                bool isKeyboard = device.Appearance.RawValue == LeAppearanceKeyboard;
                result.Add(new PairedDeviceInfo(
                    device.BluetoothAddress,
                    string.IsNullOrWhiteSpace(device.Name) ? di.Name : device.Name,
                    IsLowEnergy: true,
                    IsConnected: device.ConnectionStatus == BluetoothConnectionStatus.Connected,
                    isKeyboard,
                    isKeyboard ? Strings.KindKeyboardBle : Strings.KindBleDevice));
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to read BLE device {di.Name}", ex);
            }
        }

        return result
            .DistinctBy(d => d.Key)
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string DescribeMajorClass(BluetoothMajorClass major) => major switch
    {
        BluetoothMajorClass.AudioVideo => Strings.KindAudio,
        BluetoothMajorClass.Computer => Strings.KindComputer,
        BluetoothMajorClass.Phone => Strings.KindPhone,
        BluetoothMajorClass.Imaging => Strings.KindImaging,
        BluetoothMajorClass.Wearable => Strings.KindWearable,
        BluetoothMajorClass.Health => Strings.KindHealth,
        BluetoothMajorClass.Toy => Strings.KindToy,
        BluetoothMajorClass.NetworkAccessPoint => Strings.KindAccessPoint,
        _ => Strings.KindOther,
    };

    /// <summary>
    /// If the device is not connected, connects it to this computer
    /// (including "pulling" it away from another host).
    /// If it is connected, performs a full reconnect (disconnect → connect),
    /// e.g. so that a speaker "remembers" its microphone again.
    /// </summary>
    public static Task<ReconnectResult> ReconnectAsync(SavedDevice device, TimeSpan timeout, CancellationToken ct = default) =>
        device.IsLowEnergy
            ? ReconnectLowEnergyAsync(device, timeout, ct)
            : ReconnectClassicAsync(device, timeout, ct);

    private static async Task<ReconnectResult> ReconnectClassicAsync(SavedDevice saved, TimeSpan timeout, CancellationToken ct)
    {
        using var device = await BluetoothDevice.FromBluetoothAddressAsync(saved.Address).AsTask(ct);
        if (device is null)
        {
            return new(ReconnectOutcome.DeviceNotFound, Strings.ResultNotFound(saved.DisplayName));
        }

        bool IsConnected() => device.ConnectionStatus == BluetoothConnectionStatus.Connected;

        bool wasConnected = IsConnected();

        if (!wasConnected)
        {
            // Step 1. Soft attempt: an uncached SDP query makes the adapter page the device.
            // For many keyboards this is enough, and Windows brings up the HID channel right away.
            Log.Info($"[{saved.DisplayName}] step 1: uncached SDP query");
            await PageDeviceAsync(device, saved, ct);

            // SDP itself briefly brings up a connection, so make sure the connection actually holds.
            if (await WaitForStableConnectionAsync(IsConnected, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), ct))
            {
                return Connected(saved);
            }
        }

        // Step 2. Reinstall services (same as "Disconnect → Connect" in Windows Settings).
        Log.Info($"[{saved.DisplayName}] {(wasConnected ? "full reconnect" : "step 2")}: reinstalling services");
        // Through the service when it is installed (no administrator rights needed here), otherwise directly or with UAC.
        int error = await PrivilegedActions.ReinstallServicesAsync(saved.Address, saved.Services, full: !wasConnected, ct);
        switch (error)
        {
            case NativeBluetooth.ErrorSuccess:
                break;
            case NativeBluetooth.ErrorAccessDenied:
                return new(ReconnectOutcome.AccessDenied, Strings.ResultAccessDenied);
            case NativeBluetooth.ErrorNoRadio:
                return new(ReconnectOutcome.Failed, Strings.ResultNoRadio);
            default:
                return new(ReconnectOutcome.Failed, Strings.ResultApiError(error));
        }

        // Give the stack time to apply the change so the old connection is not mistaken for a new one.
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        var firstWait = TimeSpan.FromSeconds(Math.Min(6, timeout.TotalSeconds));
        if (await WaitForStableConnectionAsync(IsConnected, firstWait, TimeSpan.FromSeconds(2), ct))
        {
            return wasConnected ? Reconnected(saved) : Connected(saved);
        }

        // Windows did not connect on its own (happens with audio devices), so nudge it once more.
        Log.Info($"[{saved.DisplayName}] repeating SDP query after reinstall");
        await PageDeviceAsync(device, saved, ct);

        if (await WaitForStableConnectionAsync(IsConnected, timeout - firstWait, TimeSpan.FromSeconds(2), ct))
        {
            return wasConnected ? Reconnected(saved) : Connected(saved);
        }

        return NotConnected(saved);
    }

    private static async Task PageDeviceAsync(BluetoothDevice device, SavedDevice saved, CancellationToken ct)
    {
        try
        {
            await device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(8), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info($"[{saved.DisplayName}] SDP: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<ReconnectResult> ReconnectLowEnergyAsync(SavedDevice saved, TimeSpan timeout, CancellationToken ct)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(saved.Address).AsTask(ct);
        if (device is null)
        {
            return new(ReconnectOutcome.DeviceNotFound, Strings.ResultNotFound(saved.DisplayName));
        }

        bool IsConnected() => device.ConnectionStatus == BluetoothConnectionStatus.Connected;

        if (IsConnected())
        {
            // The public Windows API offers no way to force-disconnect a BLE connection.
            return new(ReconnectOutcome.AlreadyConnected, Strings.ResultBleAlreadyConnected(saved.DisplayName));
        }

        // A GattSession with MaintainConnection makes Windows actively connect to the device
        // as soon as it becomes available (starts advertising to this host).
        using var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(ct);
        session.MaintainConnection = true;
        Log.Info($"[{saved.DisplayName}] BLE: MaintainConnection = true");

        try
        {
            await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct).WaitAsync(timeout, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info($"[{saved.DisplayName}] GATT: {ex.GetType().Name}: {ex.Message}");
        }

        if (await WaitForStableConnectionAsync(IsConnected, timeout, TimeSpan.FromSeconds(2), ct))
        {
            // Let the Windows HID driver take over the connection before releasing the session.
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            return Connected(saved);
        }

        return NotConnected(saved);
    }

    private static ReconnectResult Connected(SavedDevice saved) =>
        new(ReconnectOutcome.Connected, Strings.ResultConnected(saved.DisplayName));

    private static ReconnectResult Reconnected(SavedDevice saved) =>
        new(ReconnectOutcome.Connected, Strings.ResultReconnected(saved.DisplayName));

    private static ReconnectResult NotConnected(SavedDevice saved) =>
        new(ReconnectOutcome.NotConnected, Strings.ResultNotConnected(saved.DisplayName));

    /// <summary>
    /// Waits until the connection has held continuously for <paramref name="stableFor"/>.
    /// Returns false if that did not happen within <paramref name="timeout"/>.
    /// </summary>
    private static async Task<bool> WaitForStableConnectionAsync(
        Func<bool> isConnected, TimeSpan timeout, TimeSpan stableFor, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        TimeSpan? connectedSince = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (isConnected())
            {
                connectedSince ??= sw.Elapsed;
                if (sw.Elapsed - connectedSince.Value >= stableFor)
                {
                    return true;
                }
            }
            else
            {
                connectedSince = null;
                if (sw.Elapsed >= timeout)
                {
                    return false;
                }
            }

            await Task.Delay(250, ct);
        }
    }
}
