using BluetoothSwitcher.Infrastructure;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BluetoothSwitcher.Bluetooth;

/// <summary>
/// Reads the battery level of a Bluetooth device, if the device reports it.
/// </summary>
public static class BatteryReader
{
    /// <summary>
    /// DEVPKEY_Bluetooth_Battery: the value shown in Windows Settings → Bluetooth & devices.
    /// Windows fills it for Hands-Free devices (speakers, headsets) and for many BLE devices.
    /// It lives on one of the PnP device nodes that share the Bluetooth device's container.
    /// </summary>
    private const string BatteryPropertyKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    private const string AepContainerIdKey = "System.Devices.Aep.ContainerId";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Battery level 0–100, or null if the device does not report it.</summary>
    public static async Task<int?> TryGetLevelAsync(SavedDevice device)
    {
        try
        {
            string? aepId = await GetAepIdAsync(device);
            if (aepId is null)
            {
                return null;
            }

            Guid? containerId = await GetContainerIdAsync(aepId);

            // 1. The value Windows itself shows (Hands-Free audio devices, many BLE devices).
            int? level = containerId is { } id1 ? await TryGetFromDevicePropertyAsync(id1) : null;

            // 2. BLE: standard GATT Battery Service.
            if (level is null && device.IsLowEnergy)
            {
                level = await TryGetFromGattAsync(device.Address);
            }

            // 3. HID reports (e.g. Apple Magic Keyboard), only while connected: reading wakes the device up.
            if (level is null && containerId is { } id2)
            {
                level = await HidBatteryReader.TryGetLevelAsync(id2, device.DisplayName);
            }

            return level is >= 0 and <= 100 ? level : null;
        }
        catch (Exception ex)
        {
            Log.Info($"Battery level for '{device.DisplayName}' is unavailable: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The PnP container that groups all device nodes (HID collections, audio endpoints…) of a Bluetooth device.</summary>
    internal static async Task<Guid?> TryGetContainerIdAsync(SavedDevice device)
    {
        string? aepId = await GetAepIdAsync(device);
        return aepId is null ? null : await GetContainerIdAsync(aepId);
    }

    private static async Task<string?> GetAepIdAsync(SavedDevice device)
    {
        if (device.IsLowEnergy)
        {
            using var le = await BluetoothLEDevice.FromBluetoothAddressAsync(device.Address).AsTask().WaitAsync(Timeout);
            return le?.DeviceId;
        }

        using var classic = await BluetoothDevice.FromBluetoothAddressAsync(device.Address).AsTask().WaitAsync(Timeout);
        return classic?.DeviceId;
    }

    private static async Task<Guid?> GetContainerIdAsync(string aepId)
    {
        var aep = await DeviceInformation
            .CreateFromIdAsync(aepId, [AepContainerIdKey], DeviceInformationKind.AssociationEndpoint)
            .AsTask().WaitAsync(Timeout);

        return aep.Properties.TryGetValue(AepContainerIdKey, out var value) && value is Guid containerId ? containerId : null;
    }

    private static async Task<int?> TryGetFromDevicePropertyAsync(Guid containerId)
    {
        var nodes = await DeviceInformation
            .FindAllAsync($"System.Devices.ContainerId:=\"{{{containerId}}}\"", [BatteryPropertyKey], DeviceInformationKind.Device)
            .AsTask().WaitAsync(Timeout);

        foreach (var node in nodes)
        {
            if (node.Properties.TryGetValue(BatteryPropertyKey, out var battery) && battery is not null)
            {
                return Convert.ToInt32(battery);
            }
        }

        return null;
    }

    /// <summary>Standard GATT Battery Service (0x180F) → Battery Level (0x2A19).</summary>
    private static async Task<int?> TryGetFromGattAsync(ulong address)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask().WaitAsync(Timeout);
        if (device is null || device.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            return null; // do not wake up or connect a device just to read its battery
        }

        var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached)
            .AsTask().WaitAsync(Timeout);
        if (services.Status != GattCommunicationStatus.Success)
        {
            return null;
        }

        try
        {
            foreach (var service in services.Services)
            {
                var characteristics = await service
                    .GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached)
                    .AsTask().WaitAsync(Timeout);
                if (characteristics.Status != GattCommunicationStatus.Success)
                {
                    continue;
                }

                foreach (var characteristic in characteristics.Characteristics)
                {
                    var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(Timeout);
                    if (read.Status == GattCommunicationStatus.Success && read.Value.Length > 0)
                    {
                        return DataReader.FromBuffer(read.Value).ReadByte();
                    }
                }
            }
        }
        finally
        {
            foreach (var service in services.Services)
            {
                service.Dispose();
            }
        }

        return null;
    }
}
