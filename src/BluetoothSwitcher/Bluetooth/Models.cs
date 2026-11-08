using System.Text.Json.Serialization;

namespace BluetoothSwitcher.Bluetooth;

/// <summary>Устройство, выбранное пользователем в настройках.</summary>
public sealed record SavedDevice
{
    public ulong Address { get; init; }
    public string Name { get; init; } = "";
    public bool IsLowEnergy { get; init; }

    /// <summary>
    /// Сервисы, которые переустанавливаются при переподключении (только Classic).
    /// null — автоматический выбор (HID для клавиатуры, Hands-Free/Headset для аудио).
    /// </summary>
    public List<Guid>? Services { get; init; }

    [JsonIgnore]
    public string Key => MakeKey(Address, IsLowEnergy);

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? BluetoothAddress.Format(Address) : Name;

    public static string MakeKey(ulong address, bool isLowEnergy) =>
        $"{(isLowEnergy ? "LE" : "BR")}:{address:X12}";
}

/// <summary>Сопряжённое с этим компьютером устройство.</summary>
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
