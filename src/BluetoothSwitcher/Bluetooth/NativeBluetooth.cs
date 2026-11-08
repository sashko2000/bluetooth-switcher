using System.Runtime.InteropServices;
using BluetoothSwitcher.Infrastructure;

namespace BluetoothSwitcher.Bluetooth;

/// <summary>
/// Win32 Bluetooth API (bthprops.cpl). Используется для «жёсткого» переподключения
/// классического (BR/EDR) устройства: его сервисы (HID, A2DP, Hands-Free, AVRCP…) отключаются
/// и снова включаются. Это то же самое, что «Отключить → Подключить» в параметрах Windows:
/// драйверы профилей переустанавливаются, и стек сам инициирует подключение.
/// </summary>
internal static class NativeBluetooth
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

    private static readonly Dictionary<Guid, string> KnownServices = new()
    {
        [HidServiceClass] = "HID (клавиатура/мышь)",
        [new Guid("0000110B-0000-1000-8000-00805F9B34FB")] = "A2DP Sink (звук)",
        [new Guid("0000110A-0000-1000-8000-00805F9B34FB")] = "A2DP Source",
        [HandsFreeServiceClass] = "Hands-Free (микрофон)",
        [new Guid("0000111F-0000-1000-8000-00805F9B34FB")] = "Hands-Free AG",
        [HeadsetServiceClass] = "Headset (микрофон)",
        [new Guid("00001112-0000-1000-8000-00805F9B34FB")] = "Headset AG",
        [new Guid("0000110E-0000-1000-8000-00805F9B34FB")] = "AVRCP (управление)",
        [new Guid("0000110C-0000-1000-8000-00805F9B34FB")] = "AVRCP Target",
        [new Guid("00001101-0000-1000-8000-00805F9B34FB")] = "Serial Port",
    };

    public static bool IsKnown(Guid service) => KnownServices.ContainsKey(service);

    public static string Describe(Guid service) =>
        KnownServices.TryGetValue(service, out var name) ? name : service.ToString();

    /// <summary>
    /// Сервисы, которые переподключаются в режиме «Автоматически».
    /// Каждый вызов BluetoothSetServiceState занимает около 3 секунд, поэтому трогаем минимум:
    /// клавиатура/мышь — только HID, колонка/гарнитура — только Hands-Free/Headset (микрофон).
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

        return installed.Count > 0 ? installed.ToArray() : [HidServiceClass];
    }

    /// <summary>
    /// Полный набор для подключения устройства, которое сейчас не подключено к этому компьютеру
    /// (например, занято другим): все стандартные профили, без служебных сервисов производителя.
    /// </summary>
    public static Guid[] FullServices(IReadOnlyCollection<Guid> installed)
    {
        var known = installed.Where(IsKnown).ToArray();
        if (known.Length > 0)
        {
            return known;
        }

        return installed.Count > 0 ? installed.ToArray() : [HidServiceClass];
    }

    /// <summary>Установленные (включённые) сервисы устройства или null, если устройство не найдено.</summary>
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
    /// Отключает и снова включает сервисы устройства. Возвращает код ошибки Win32.
    /// </summary>
    /// <param name="services">Выбранные пользователем сервисы; null — автоматический выбор (<see cref="DefaultServices"/>).</param>
    /// <param name="full">
    /// true — устройство не подключено к этому ПК: переустанавливаются все стандартные профили
    /// (<see cref="FullServices"/>) плюс выбранные пользователем.
    /// </param>
    public static int ReinstallServices(ulong address, TimeSpan pause, IReadOnlyCollection<Guid>? services, bool full)
    {
        var radio = OpenFirstRadio();
        if (radio == IntPtr.Zero)
        {
            Log.Error("Bluetooth-адаптер не найден");
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

            Log.Info($"Установлены: [{string.Join(", ", installed.Select(Describe))}]");
            Log.Info($"Переустановка [{string.Join(", ", targets.Select(Describe))}] для «{info.szName}» ({BluetoothAddress.Format(address)})");

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
