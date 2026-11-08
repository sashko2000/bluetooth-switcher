using System.Runtime.InteropServices;
using BluetoothSwitcher.Infrastructure;
using static BluetoothSwitcher.Bluetooth.HidNative;

namespace BluetoothSwitcher.Bluetooth;

/// <summary>
/// Reads the battery level straight from the device's HID reports. Covers devices that Windows itself
/// does not show a battery for, e.g. Apple Magic Keyboard / Magic Mouse / Magic Trackpad over Bluetooth.
/// <para>
/// 1. Generic: find a value with a standard battery usage (Generic Device Controls / Battery Strength,
///    or Battery System / Absolute State Of Charge) in any top-level collection of the device and request
///    that report (the same approach the Linux hid-input driver uses).
/// 2. Apple fallback: request report 0x90 directly; byte 2 is the charge in percent
///    (layout used by Apple's Bluetooth HID devices, see Linux hid-apple / hid-magicmouse).
/// </para>
/// Windows opens keyboard and mouse collections exclusively, but the battery usually sits in a separate
/// (vendor) collection that a normal application can open.
/// </summary>
internal static class HidBatteryReader
{
    private static readonly (ushort Page, ushort Usage)[] BatteryUsages =
    [
        (0x06, 0x20), // Generic Device Controls: Battery Strength
        (0x85, 0x65), // Battery System: Absolute State Of Charge
        (0x85, 0x64), // Battery System: Relative State Of Charge
    ];

    private const byte AppleBatteryReportId = 0x90;
    private static readonly string[] AppleVendorMarkers = ["vid&0001004c", "vid&0002004c", "vid_05ac", "vid&000205ac"];

    // Detailed diagnostics go to the log once per device per run, so the log does not grow every 5 minutes.
    private static readonly HashSet<Guid> Diagnosed = [];
    private static readonly Lock DiagnosedSync = new();

    public static async Task<int?> TryGetLevelAsync(Guid containerId, string deviceName)
    {
        var paths = await FindCollectionPathsAsync(containerId);

        bool log;
        lock (DiagnosedSync)
        {
            log = Diagnosed.Add(containerId);
        }

        if (log)
        {
            Log.Info($"[{deviceName}] HID battery probe: {paths.Count} HID collection(s)");
        }

        // HID calls are blocking and may take a while over Bluetooth.
        return await Task.Run(() =>
        {
            foreach (var path in paths)
            {
                if (TryReadCollection(path, deviceName, log) is int level)
                {
                    return (int?)level;
                }
            }

            return null;
        }).WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static int? TryReadCollection(string path, string deviceName, bool log)
    {
        using var handle = Open(path, out bool readWrite);
        if (handle.IsInvalid)
        {
            if (log)
            {
                Log.Info($"[{deviceName}]   {ShortPath(path)}: cannot open (error {Marshal.GetLastPInvokeError()})");
            }

            return null;
        }

        if (!HidD_GetPreparsedData(handle, out var preparsed))
        {
            return null;
        }

        try
        {
            if (HidP_GetCaps(preparsed, out var caps) != HidpStatusSuccess)
            {
                return null;
            }

            if (log)
            {
                Log.Info($"[{deviceName}]   {ShortPath(path)}: usage {caps.UsagePage:X4}:{caps.Usage:X4}, " +
                         $"access {(readWrite ? "rw" : "none")}, report bytes in/feature {caps.InputReportByteLength}/{caps.FeatureReportByteLength}, " +
                         $"value caps in/feature {caps.NumberInputValueCaps}/{caps.NumberFeatureValueCaps}");
            }

            // 1. Standard battery usages: feature reports first (harmless), then input reports.
            foreach (var (type, count, length) in new[]
                     {
                         (HidpFeature, caps.NumberFeatureValueCaps, caps.FeatureReportByteLength),
                         (HidpInput, caps.NumberInputValueCaps, caps.InputReportByteLength),
                     })
            {
                if (count == 0 || length == 0)
                {
                    continue;
                }

                var valueCaps = new HIDP_VALUE_CAPS[count];
                ushort n = count;
                if (HidP_GetValueCaps(type, valueCaps, ref n, preparsed) != HidpStatusSuccess)
                {
                    continue;
                }

                foreach (var vc in valueCaps.Take(n))
                {
                    if (log)
                    {
                        Log.Info($"[{deviceName}]     {(type == HidpFeature ? "feature" : "input")} report {vc.ReportID:X2}: " +
                                 $"usage {vc.UsagePage:X4}:{(vc.IsRange != 0 ? $"{vc.UsageMin:X4}-{vc.UsageMax:X4}" : $"{vc.UsageMin:X4}")}, " +
                                 $"logical {vc.LogicalMin}..{vc.LogicalMax}");
                    }

                    foreach (var (page, usage) in BatteryUsages)
                    {
                        bool matches = vc.UsagePage == page &&
                                       (vc.IsRange != 0 ? usage >= vc.UsageMin && usage <= vc.UsageMax : vc.UsageMin == usage);
                        if (!matches)
                        {
                            continue;
                        }

                        var report = new byte[length];
                        report[0] = vc.ReportID;
                        bool ok = type == HidpFeature
                            ? HidD_GetFeature(handle, report, report.Length)
                            : HidD_GetInputReport(handle, report, report.Length);
                        if (!ok)
                        {
                            if (log)
                            {
                                Log.Info($"[{deviceName}]     get report {vc.ReportID:X2} failed (error {Marshal.GetLastPInvokeError()})");
                            }

                            continue;
                        }

                        if (HidP_GetUsageValue(type, page, vc.LinkCollection, usage, out uint raw, preparsed, report, (uint)report.Length) != HidpStatusSuccess)
                        {
                            continue;
                        }

                        int level = Scale(raw, vc.LogicalMin, vc.LogicalMax);
                        if (log)
                        {
                            Log.Info($"[{deviceName}]     battery {page:X2}:{usage:X2} raw {raw} -> {level}%");
                        }

                        if (level is >= 0 and <= 100)
                        {
                            return level;
                        }
                    }
                }
            }

            // 2. Apple fallback: feature report 0x90 = [0x90, flags, percent, ...].
            if (AppleVendorMarkers.Any(m => path.Contains(m, StringComparison.OrdinalIgnoreCase)) && caps.FeatureReportByteLength >= 3)
            {
                var report = new byte[caps.FeatureReportByteLength];
                report[0] = AppleBatteryReportId;
                if (HidD_GetFeature(handle, report, report.Length))
                {
                    if (log)
                    {
                        Log.Info($"[{deviceName}]     Apple report 90: {Convert.ToHexString(report)}");
                    }

                    if (report[0] == AppleBatteryReportId && report[2] <= 100)
                    {
                        return report[2];
                    }
                }
                else if (log)
                {
                    Log.Info($"[{deviceName}]     Apple report 90 failed (error {Marshal.GetLastPInvokeError()})");
                }
            }

            return null;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    private static int Scale(uint raw, int min, int max)
    {
        if (max <= min)
        {
            return (int)raw; // descriptor without a usable range: assume percent
        }

        return (int)Math.Round(((long)raw - min) * 100.0 / (max - min));
    }
}
