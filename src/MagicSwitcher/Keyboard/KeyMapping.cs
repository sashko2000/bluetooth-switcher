using System.Text.Json.Serialization;

namespace MagicSwitcher.Keyboard;

/// <summary>Keys of an Apple keyboard that can be remapped, as Windows sees them (Fn is handled by Apple's driver, not visible).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppleKey
{
    CapsLock,
    LeftControl,
    LeftOption,
    LeftCommand,
    RightCommand,
    RightOption,
    LeftShift,
    RightShift,
}

/// <summary>What a remapped key sends instead.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KeyTarget
{
    Disabled,
    LeftControl,
    RightControl,
    LeftAlt,
    RightAlt,
    LeftWin,
    RightWin,
    LeftShift,
    RightShift,
    CapsLock,
    Escape,
    Delete,
    Backspace,
    Enter,
    Insert,
    PrintScreen,
    Menu,
}

/// <summary>One remapping: <see cref="From"/> on an Apple keyboard sends <see cref="To"/>.</summary>
public sealed record KeyMapping(AppleKey From, KeyTarget To);

/// <summary>A key as Windows input sees it: scan code (with the E0 prefix) and virtual-key code.</summary>
internal readonly record struct KeyCode(ushort Scan, bool Extended, ushort VirtualKey)
{
    /// <summary>Scan code plus 0x100 for E0 keys: the same in the low-level hook and in raw input.</summary>
    public int Id => Scan | (Extended ? 0x100 : 0);

    public bool IsWinOrAlt => VirtualKey is 0x5B or 0x5C or 0xA4 or 0xA5;

    public static KeyCode Of(AppleKey key) => key switch
    {
        AppleKey.CapsLock => new(0x3A, false, 0x14),
        AppleKey.LeftControl => new(0x1D, false, 0xA2),
        AppleKey.LeftOption => new(0x38, false, 0xA4),
        AppleKey.LeftCommand => new(0x5B, true, 0x5B),
        AppleKey.RightCommand => new(0x5C, true, 0x5C),
        AppleKey.RightOption => new(0x38, true, 0xA5),
        AppleKey.LeftShift => new(0x2A, false, 0xA0),
        AppleKey.RightShift => new(0x36, false, 0xA1),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    /// <summary>null for <see cref="KeyTarget.Disabled"/>.</summary>
    public static KeyCode? Of(KeyTarget target) => target switch
    {
        KeyTarget.Disabled => null,
        KeyTarget.LeftControl => new(0x1D, false, 0xA2),
        KeyTarget.RightControl => new(0x1D, true, 0xA3),
        KeyTarget.LeftAlt => new(0x38, false, 0xA4),
        KeyTarget.RightAlt => new(0x38, true, 0xA5),
        KeyTarget.LeftWin => new(0x5B, true, 0x5B),
        KeyTarget.RightWin => new(0x5C, true, 0x5C),
        KeyTarget.LeftShift => new(0x2A, false, 0xA0),
        KeyTarget.RightShift => new(0x36, false, 0xA1),
        KeyTarget.CapsLock => new(0x3A, false, 0x14),
        KeyTarget.Escape => new(0x01, false, 0x1B),
        KeyTarget.Delete => new(0x53, true, 0x2E),
        KeyTarget.Backspace => new(0x0E, false, 0x08),
        KeyTarget.Enter => new(0x1C, false, 0x0D),
        KeyTarget.Insert => new(0x52, true, 0x2D),
        KeyTarget.PrintScreen => new(0x37, true, 0x2C),
        KeyTarget.Menu => new(0x5D, true, 0x5D),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };
}

internal static class AppleKeyboards
{
    private static readonly string[] AppleVendorMarkers = ["vid&0001004c", "vid&0002004c", "vid_05ac", "vid&000205ac"];

    /// <summary>True for the raw input device path of an Apple keyboard (Bluetooth or USB).</summary>
    public static bool IsAppleDevice(string devicePath) =>
        AppleVendorMarkers.Any(m => devicePath.Contains(m, StringComparison.OrdinalIgnoreCase));
}
