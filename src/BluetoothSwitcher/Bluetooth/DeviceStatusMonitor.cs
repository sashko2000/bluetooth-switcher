using BluetoothSwitcher.Infrastructure;
using Windows.Devices.Bluetooth;
using Windows.Foundation;

namespace BluetoothSwitcher.Bluetooth;

/// <summary>
/// Следит за состоянием подключения выбранных устройств.
/// Событие <see cref="StatusChanged"/> приходит из произвольного потока.
/// </summary>
public sealed class DeviceStatusMonitor : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Watched> _watched = [];
    private readonly Lock _sync = new();

    public event EventHandler? StatusChanged;

    public async Task UpdateAsync(IEnumerable<SavedDevice> devices)
    {
        await _gate.WaitAsync();
        try
        {
            ClearWatched();

            foreach (var saved in devices)
            {
                try
                {
                    var watched = saved.IsLowEnergy ? await WatchLowEnergyAsync(saved) : await WatchClassicAsync(saved);
                    if (watched is not null)
                    {
                        lock (_sync)
                        {
                            _watched[saved.Key] = watched;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Не удалось начать отслеживание «{saved.DisplayName}»", ex);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>true/false — подключено/нет; null — состояние неизвестно (устройство не найдено).</summary>
    public bool? IsConnected(SavedDevice device)
    {
        lock (_sync)
        {
            return _watched.TryGetValue(device.Key, out var w) ? w.IsConnected() : null;
        }
    }

    private async Task<Watched?> WatchClassicAsync(SavedDevice saved)
    {
        var device = await BluetoothDevice.FromBluetoothAddressAsync(saved.Address);
        if (device is null)
        {
            return null;
        }

        TypedEventHandler<BluetoothDevice, object> handler = (_, _) => StatusChanged?.Invoke(this, EventArgs.Empty);
        device.ConnectionStatusChanged += handler;
        return new Watched(
            () => device.ConnectionStatus == BluetoothConnectionStatus.Connected,
            () =>
            {
                device.ConnectionStatusChanged -= handler;
                device.Dispose();
            });
    }

    private async Task<Watched?> WatchLowEnergyAsync(SavedDevice saved)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(saved.Address);
        if (device is null)
        {
            return null;
        }

        TypedEventHandler<BluetoothLEDevice, object> handler = (_, _) => StatusChanged?.Invoke(this, EventArgs.Empty);
        device.ConnectionStatusChanged += handler;
        return new Watched(
            () => device.ConnectionStatus == BluetoothConnectionStatus.Connected,
            () =>
            {
                device.ConnectionStatusChanged -= handler;
                device.Dispose();
            });
    }

    private void ClearWatched()
    {
        lock (_sync)
        {
            foreach (var w in _watched.Values)
            {
                try { w.Release(); } catch { /* ignore */ }
            }

            _watched.Clear();
        }
    }

    public void Dispose()
    {
        ClearWatched();
        _gate.Dispose();
    }

    private sealed record Watched(Func<bool> IsConnected, Action Release);
}
