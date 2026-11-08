using System.Diagnostics;
using BluetoothSwitcher.Infrastructure;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace BluetoothSwitcher.Bluetooth;

public static class BluetoothManager
{
    private const uint CodMajorPeripheral = 0x05;
    private const uint CodKeyboardBit = 0x40;
    private const uint CodPointingBit = 0x80;
    private const ushort LeAppearanceKeyboard = 0x03C1;

    /// <summary>Все сопряжённые с компьютером устройства (Classic и BLE).</summary>
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
                    : isKeyboard && (cod & CodPointingBit) != 0 ? "Клавиатура + мышь"
                    : isKeyboard ? "Клавиатура"
                    : (cod & CodPointingBit) != 0 ? "Мышь"
                    : "Периферия";

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
                Log.Error($"Ошибка чтения Classic-устройства {di.Name}", ex);
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
                    isKeyboard ? "Клавиатура (BLE)" : "BLE-устройство"));
            }
            catch (Exception ex)
            {
                Log.Error($"Ошибка чтения BLE-устройства {di.Name}", ex);
            }
        }

        return result
            .DistinctBy(d => d.Key)
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string DescribeMajorClass(BluetoothMajorClass major) => major switch
    {
        BluetoothMajorClass.AudioVideo => "Аудио (колонка/наушники)",
        BluetoothMajorClass.Computer => "Компьютер",
        BluetoothMajorClass.Phone => "Телефон",
        BluetoothMajorClass.Imaging => "Принтер/камера",
        BluetoothMajorClass.Wearable => "Носимое устройство",
        BluetoothMajorClass.Health => "Здоровье",
        BluetoothMajorClass.Toy => "Игрушка",
        BluetoothMajorClass.NetworkAccessPoint => "Точка доступа",
        _ => "Другое",
    };

    /// <summary>
    /// Если устройство не подключено — подключает его к этому компьютеру
    /// (в том числе «перетягивает» с другого хоста).
    /// Если подключено — выполняет полное переподключение (отключить → подключить),
    /// например чтобы колонка снова «вспомнила» про микрофон.
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
            return new(ReconnectOutcome.DeviceNotFound, $"«{saved.DisplayName}» не найдено среди сопряжённых устройств.");
        }

        bool IsConnected() => device.ConnectionStatus == BluetoothConnectionStatus.Connected;

        bool wasConnected = IsConnected();

        if (!wasConnected)
        {
            // Шаг 1. Мягкая попытка: SDP-запрос без кэша заставляет адаптер «позвать» (page) устройство.
            // Многим клавиатурам этого достаточно, и Windows сразу поднимает HID-канал.
            Log.Info($"[{saved.DisplayName}] шаг 1: запрос SDP без кэша");
            await PageDeviceAsync(device, saved, ct);

            // SDP сам по себе ненадолго поднимает соединение — поэтому проверяем, что оно держится.
            if (await WaitForStableConnectionAsync(IsConnected, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), ct))
            {
                return Connected(saved);
            }
        }

        // Шаг 2. Переустановка сервисов (= «Отключить → Подключить» в Windows).
        Log.Info($"[{saved.DisplayName}] {(wasConnected ? "полное переподключение" : "шаг 2")}: переустановка сервисов");
        int error = await Task.Run(() => NativeBluetooth.ReinstallServices(saved.Address, TimeSpan.FromSeconds(1), saved.Services, full: !wasConnected), ct);
        switch (error)
        {
            case NativeBluetooth.ErrorSuccess:
                break;
            case NativeBluetooth.ErrorAccessDenied:
                return new(ReconnectOutcome.AccessDenied,
                    "Windows не разрешила переустановить сервисы устройства без прав администратора. " +
                    "Выберите в меню «Перезапустить от имени администратора».");
            case NativeBluetooth.ErrorNoRadio:
                return new(ReconnectOutcome.Failed, "Bluetooth-адаптер не найден или выключен.");
            default:
                return new(ReconnectOutcome.Failed, $"Ошибка Bluetooth API: {error}.");
        }

        // Даём стеку время применить изменения, чтобы не принять «старое» соединение за новое.
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        var firstWait = TimeSpan.FromSeconds(Math.Min(6, timeout.TotalSeconds));
        if (await WaitForStableConnectionAsync(IsConnected, firstWait, TimeSpan.FromSeconds(2), ct))
        {
            return wasConnected ? Reconnected(saved) : Connected(saved);
        }

        // Windows не подключилась сама (бывает с аудиоустройствами) — подталкиваем ещё раз.
        Log.Info($"[{saved.DisplayName}] повторный запрос SDP после переустановки");
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
            return new(ReconnectOutcome.DeviceNotFound, $"«{saved.DisplayName}» не найдено среди сопряжённых устройств.");
        }

        bool IsConnected() => device.ConnectionStatus == BluetoothConnectionStatus.Connected;

        if (IsConnected())
        {
            // Принудительно разорвать BLE-соединение через публичный API Windows нельзя.
            return new(ReconnectOutcome.AlreadyConnected,
                $"«{saved.DisplayName}» (BLE) уже подключено. Принудительное переподключение для BLE Windows не поддерживает.");
        }

        // GattSession с MaintainConnection заставляет Windows активно подключаться к устройству,
        // как только оно станет доступно (начнёт рекламировать себя этому хосту).
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
            // Даём HID-драйверу Windows подхватить соединение, прежде чем отпустить сессию.
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            return Connected(saved);
        }

        return NotConnected(saved);
    }

    private static ReconnectResult Connected(SavedDevice saved) =>
        new(ReconnectOutcome.Connected, $"«{saved.DisplayName}» подключена к этому компьютеру.");

    private static ReconnectResult Reconnected(SavedDevice saved) =>
        new(ReconnectOutcome.Connected, $"«{saved.DisplayName}» переподключено.");

    private static ReconnectResult NotConnected(SavedDevice saved) =>
        new(ReconnectOutcome.NotConnected,
            $"«{saved.DisplayName}» не ответило. Проверьте, что устройство включено и рядом. " +
            "Некоторые устройства принимают входящее подключение только на активном канале.");

    /// <summary>
    /// Ждёт, пока соединение не будет непрерывно держаться <paramref name="stableFor"/>.
    /// Возвращает false, если за <paramref name="timeout"/> этого не произошло.
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
