using System.Text.Json.Serialization;

namespace MagicSwitcher.Bluetooth;

/// <summary>A device selected by the user in settings.</summary>
public sealed record SavedDevice
{
    public ulong Address { get; init; }
    public string Name { get; init; } = "";
    public bool IsLowEnergy { get; init; }

    /// <summary>
    /// Services reinstalled on reconnect (Classic only).
    /// null means automatic selection (HID for keyboards, Hands-Free/Headset for audio).
    /// </summary>
    public List<Guid>? Services { get; init; }

    /// <summary>
    /// Restore at startup and sign-in (Classic keyboards): the service reconnects the device and, if another host
    /// took it over, pairs it again without the Windows dialog.
    /// </summary>
    public bool RestoreAtStartup { get; init; }

    [JsonIgnore]
    public string Key => MakeKey(Address, IsLowEnergy);

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? BluetoothAddress.Format(Address) : Name;

    public static string MakeKey(ulong address, bool isLowEnergy) =>
        $"{(isLowEnergy ? "LE" : "BR")}:{address:X12}";
}

/// <summary>A device paired with this computer.</summary>
public sealed record PairedDeviceInfo(
    ulong Address,
    string Name,
    bool IsLowEnergy,
    bool IsConnected,
    bool IsKeyboard,
    string Kind)
{
    public string Key => SavedDevice.MakeKey(Address, IsLowEnergy);

    public SavedDevice ToSaved() => new() { Address = Address, Name = Name, IsLowEnergy = IsLowEnergy };
}

public enum ReconnectOutcome
{
    Connected,
    AlreadyConnected,
    NotConnected,
    DeviceNotFound,
    AccessDenied,
    Failed,
}

public sealed record ReconnectResult(ReconnectOutcome Outcome, string Message)
{
    public bool IsSuccess => Outcome is ReconnectOutcome.Connected or ReconnectOutcome.AlreadyConnected;
}

public static class BluetoothAddress
{
    public static string Format(ulong address) =>
        string.Join(':', Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("X2")));
}
