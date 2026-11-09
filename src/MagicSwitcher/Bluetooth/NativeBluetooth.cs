using System.Runtime.InteropServices;
using MagicSwitcher.Infrastructure;

namespace MagicSwitcher.Bluetooth;

/// <summary>
/// Win32 Bluetooth API (bthprops.cpl). Used for a "hard" reconnect of a Classic (BR/EDR) device:
/// its services (HID, A2DP, Hands-Free, AVRCP…) are disabled and enabled again. This is the same as
/// "Disconnect → Connect" in Windows Settings: profile drivers are reinstalled and the stack
/// initiates the connection by itself.
/// </summary>
internal static partial class NativeBluetooth
{
    public const int ErrorSuccess = 0;
    public const int ErrorAccessDenied = 5;
    public const int ErrorNotFound = 1168;
    public const int ErrorNoRadio = 1167; // ERROR_DEVICE_NOT_CONNECTED

    private const uint BluetoothServiceDisable = 0x00;
    private const uint BluetoothServiceEnable = 0x01;
    private const int ErrorMoreData = 234;

    /// <summary>Human Interface Device Service Class UUID (0x1124).</summary>
    public static readonly Guid HidServiceClass = new("00001124-0000-1000-8000-00805F9B34FB");
    public static readonly Guid HandsFreeServiceClass = new("0000111E-0000-1000-8000-00805F9B34FB");
    public static readonly Guid HeadsetServiceClass = new("00001108-0000-1000-8000-00805F9B34FB");
    public static readonly Guid A2dpSinkServiceClass = new("0000110B-0000-1000-8000-00805F9B34FB");
    public static readonly Guid AvrcpServiceClass = new("0000110E-0000-1000-8000-00805F9B34FB");

    /// <summary>Standard profile names (protocol names, not localized).</summary>
    private static readonly Dictionary<Guid, string> KnownServices = new()
    {
        [HidServiceClass] = "HID",
        [A2dpSinkServiceClass] = "A2DP Sink",
        [new Guid("0000110A-0000-1000-8000-00805F9B34FB")] = "A2DP Source",
        [HandsFreeServiceClass] = "Hands-Free",
        [new Guid("0000111F-0000-1000-8000-00805F9B34FB")] = "Hands-Free AG",
        [HeadsetServiceClass] = "Headset",
        [new Guid("00001112-0000-1000-8000-00805F9B34FB")] = "Headset AG",
        [AvrcpServiceClass] = "AVRCP",
        [new Guid("0000110C-0000-1000-8000-00805F9B34FB")] = "AVRCP Target",
        [new Guid("00001101-0000-1000-8000-00805F9B34FB")] = "Serial Port",
    };

    /// <summary>
    /// Services that only describe the device (no profile driver). Named in the UI, but never reinstalled
    /// automatically: toggling them takes time and does not help the connection.
    /// </summary>
    private static readonly Dictionary<Guid, string> InformationalServices = new()
    {
        [new Guid("00001200-0000-1000-8000-00805F9B34FB")] = "PnP Information",
    };

    public static bool IsKnown(Guid service) => KnownServices.ContainsKey(service);

    public static bool IsInformational(Guid service) => InformationalServices.ContainsKey(service);

    public static string Describe(Guid service) =>
        KnownServices.TryGetValue(service, out var name) ? name
        : InformationalServices.TryGetValue(service, out name) ? name
        : service.ToString();

    /// <summary>
    /// Services reconnected in "Automatic" mode.
    /// Each BluetoothSetServiceState call takes about 3 seconds, so touch as few as possible:
    /// keyboard/mouse — HID only, speaker/headset — Hands-Free/Headset only (microphone).
    /// </summary>
    public static Guid[] DefaultServices(IReadOnlyCollection<Guid> installed)
    {
        if (installed.Contains(HidServiceClass))
        {
            return [HidServiceClass];
        }

        var microphone = installed.Where(g => g == HandsFreeServiceClass || g == HeadsetServiceClass).ToArray();
        if (microphone.Length > 0)
        {
            return microphone;
        }

        var known = installed.Where(IsKnown).ToArray();
        if (known.Length > 0)
        {
            return known;
        }

        var other = installed.Where(g => !IsInformational(g)).ToArray();
        return other.Length > 0 ? other : [HidServiceClass];
    }

    /// <summary>
    /// Full set used to connect a device that is not currently connected to this computer
    /// (e.g. it is in use by another one): all standard profiles, without vendor-specific services.
    /// </summary>
    public static Guid[] FullServices(IReadOnlyCollection<Guid> installed)
    {
        var known = installed.Where(IsKnown).ToArray();
        if (known.Length > 0)
        {
            return known;
        }

        var other = installed.Where(g => !IsInformational(g)).ToArray();
        return other.Length > 0 ? other : [HidServiceClass];
    }

    /// <summary>Installed (enabled) services of the device, or null if the device was not found.</summary>
    public static Guid[]? TryGetInstalledServices(ulong address)
    {
        var radio = OpenFirstRadio();
        if (radio == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var info = CreateDeviceInfo(address);
            return BluetoothGetDeviceInfo(radio, ref info) == ErrorSuccess ? GetInstalledServices(radio, ref info) : null;
        }
        finally
        {
            CloseHandle(radio);
        }
    }

    /// <summary>
    /// Disables and re-enables the device's services. Returns a Win32 error code.
    /// </summary>
    /// <param name="services">Services selected by the user; null means automatic selection (<see cref="DefaultServices"/>).</param>
    /// <param name="full">
    /// true when the device is not connected to this PC: all standard profiles
    /// (<see cref="FullServices"/>) plus the user-selected ones are reinstalled.
    /// </param>
    public static int ReinstallServices(ulong address, TimeSpan pause, IReadOnlyCollection<Guid>? services, bool full)
    {
        var radio = OpenFirstRadio();
        if (radio == IntPtr.Zero)
        {
            Log.Error("Bluetooth adapter not found");
            return ErrorNoRadio;
        }

        try
        {
            var info = CreateDeviceInfo(address);
            int error = BluetoothGetDeviceInfo(radio, ref info);
            if (error != ErrorSuccess)
            {
                Log.Error($"BluetoothGetDeviceInfo({BluetoothAddress.Format(address)}) = {error}");
                return error;
            }

            var installed = GetInstalledServices(radio, ref info);
            Guid[] targets = full
                ? FullServices(installed).Union(services ?? []).ToArray()
                : services is { Count: > 0 } ? services.ToArray() : DefaultServices(installed);

            Log.Info($"Installed: [{string.Join(", ", installed.Select(Describe))}]");
            Log.Info($"Reinstalling [{string.Join(", ", targets.Select(Describe))}] for '{info.szName}' ({BluetoothAddress.Format(address)})");

            foreach (var service in targets)
            {
                var guid = service;
                error = BluetoothSetServiceState(radio, ref info, ref guid, BluetoothServiceDisable);
                Log.Info($"  disable {Describe(guid)} -> {error}");
                if (error == ErrorAccessDenied)
                {
                    return error;
                }
            }

            Thread.Sleep(pause);

            int result = ErrorSuccess;
            foreach (var service in targets)
            {
                var guid = service;
                error = BluetoothSetServiceState(radio, ref info, ref guid, BluetoothServiceEnable);
                Log.Info($"  enable  {Describe(guid)} -> {error}");
                if (error != ErrorSuccess)
                {
                    result = error;
                }
            }

            return result;
        }
        finally
        {
            CloseHandle(radio);
        }
    }

    private static BLUETOOTH_DEVICE_INFO CreateDeviceInfo(ulong address) => new()
    {
        dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
        Address = address,
        szName = "",
    };

    private static Guid[] GetInstalledServices(IntPtr radio, ref BLUETOOTH_DEVICE_INFO info)
    {
        uint count = 0;
        int error = BluetoothEnumerateInstalledServices(radio, ref info, ref count, null);
        if ((error != ErrorSuccess && error != ErrorMoreData) || count == 0)
        {
            return [];
        }

        var services = new Guid[count];
        error = BluetoothEnumerateInstalledServices(radio, ref info, ref count, services);
        return error == ErrorSuccess ? services.Take((int)count).ToArray() : [];
    }

    private static IntPtr OpenFirstRadio()
    {
        var parameters = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        var find = BluetoothFindFirstRadio(ref parameters, out var radio);
        if (find == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        BluetoothFindRadioClose(find);
        return radio;
    }

    #region P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public uint dwSize;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)]
    private static extern int BluetoothGetDeviceInfo(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)]
    private static extern int BluetoothEnumerateInstalledServices(
        IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi, ref uint pcServiceInout, [Out] Guid[]? pGuidServices);

    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)]
    private static extern int BluetoothSetServiceState(
        IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi, ref Guid pGuidService, uint dwServiceFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    #endregion
}
