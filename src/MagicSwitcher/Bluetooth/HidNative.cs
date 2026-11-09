using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Enumeration;

namespace MagicSwitcher.Bluetooth;

/// <summary>Shared Win32 HID interop (hid.dll) and helpers for opening a device's HID collections.</summary>
internal static class HidNative
{
    private static readonly Guid HidInterfaceClass = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    /// <summary>Device paths of all enabled HID top-level collections that belong to a Bluetooth device.</summary>
    internal static async Task<List<string>> FindCollectionPathsAsync(Guid containerId)
    {
        string aqs = $"System.Devices.InterfaceClassGuid:=\"{{{HidInterfaceClass}}}\" AND " +
                     $"System.Devices.ContainerId:=\"{{{containerId}}}\" AND " +
                     "System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";

        var interfaces = await DeviceInformation.FindAllAsync(aqs, [], DeviceInformationKind.DeviceInterface)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return interfaces.Select(i => i.Id).ToList();
    }

    internal static SafeFileHandle Open(string path, out bool readWrite)
    {
        // Read/write access is needed by some devices for HidD_Get*; system-owned collections only allow none.
        var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        readWrite = !handle.IsInvalid;
        if (readWrite)
        {
            return handle;
        }

        handle.Dispose();
        return CreateFile(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
    }

    internal static string ShortPath(string path)
    {
        // \\?\HID#{00001124-...}_VID&0001004C_PID&0267&Col02#... -> VID&0001004C_PID&0267&Col02
        var parts = path.Split('#');
        if (parts.Length < 2)
        {
            return path;
        }

        int underscore = parts[1].IndexOf('_');
        return underscore >= 0 ? parts[1][(underscore + 1)..] : parts[1];
    }

    #region P/Invoke

    internal const int HidpStatusSuccess = 0x00110000;
    internal const int HidpInput = 0;
    internal const int HidpFeature = 2;

    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x1;
    internal const uint FileShareWrite = 0x2;
    internal const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct HIDP_CAPS
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort UsagePage;
        [FieldOffset(4)] public ushort InputReportByteLength;
        [FieldOffset(6)] public ushort OutputReportByteLength;
        [FieldOffset(8)] public ushort FeatureReportByteLength;
        // Reserved[17] at 10..43, NumberLinkCollectionNodes at 44, NumberInputButtonCaps at 46.
        [FieldOffset(48)] public ushort NumberInputValueCaps;
        [FieldOffset(60)] public ushort NumberFeatureValueCaps;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    internal struct HIDP_VALUE_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(56)] public ushort UsageMin; // Range.UsageMin or NotRange.Usage
        [FieldOffset(58)] public ushort UsageMax; // Range.UsageMax
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    internal static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetInputReport(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetValueCaps(int reportType, [Out] HIDP_VALUE_CAPS[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint usageValue, IntPtr preparsedData, byte[] report, uint reportLength);

    #endregion
}
