using MagicSwitcher.Infrastructure;
using Windows.Devices.Bluetooth;
using Windows.Foundation;

namespace MagicSwitcher.Bluetooth;

/// <summary>
/// Tracks the connection state of the selected devices.
/// The <see cref="StatusChanged"/> event is raised on an arbitrary thread.
/// <para>
/// For Classic HID devices (keyboards, mice) "connected" also requires the HID collections to be present:
/// a multi-host keyboard that another host took over may still show a Bluetooth connection while it does not
/// type here until it is paired again.
/// </para>
/// </summary>
public sealed class DeviceStatusMonitor : IDisposable
{
    private static readonly TimeSpan[] HidCheckDelays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

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

                        if (watched.HasHid)
                        {
                            await CheckHidAsync(watched);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to start watching '{saved.DisplayName}'", ex);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>true/false: connected (and, for HID devices, usable) or not; null: state unknown (device not found).</summary>
    public bool? IsConnected(SavedDevice device)
    {
        lock (_sync)
        {
            if (!_watched.TryGetValue(device.Key, out var w))
            {
                return null;
            }

            // Until the first HID check completes, trust the Bluetooth connection state.
            return w.IsConnected() && w.HidReady != false;
        }
    }

    /// <summary>Re-checks the HID collections of connected HID devices (e.g. when the menu opens).</summary>
    public async Task RefreshHidAsync()
    {
        List<Watched> watched;
        lock (_sync)
        {
            watched = _watched.Values.Where(w => w.HasHid).ToList();
        }

        bool changed = false;
        foreach (var w in watched)
        {
            changed |= await CheckHidAsync(w);
        }

        if (changed)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Returns true if the HID state changed.</summary>
    private static async Task<bool> CheckHidAsync(Watched w)
    {
        bool? ready = null;
        try
        {
            if (w.IsConnected() && w.ContainerId is { } containerId)
            {
                ready = (await HidNative.FindCollectionPathsAsync(containerId)).Count > 0;
            }
        }
        catch (Exception ex)
        {
            Log.Info($"HID check failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        if (w.HidReady == ready)
        {
            return false;
        }

        w.HidReady = ready;
        return true;
    }

    /// <summary>After a connection change the HID collections appear (or go away) with some delay.</summary>
    private async void OnConnectionChanged(Watched w)
    {
        StatusChanged?.Invoke(this, EventArgs.Empty);
        if (!w.HasHid)
        {
            return;
        }

        int version = Interlocked.Increment(ref w.CheckVersion);
        foreach (var delay in HidCheckDelays)
        {
            await Task.Delay(delay);
            if (version != Volatile.Read(ref w.CheckVersion) || w.Released)
            {
                return; // a newer change started its own checks
            }

            if (await CheckHidAsync(w))
            {
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private async Task<Watched?> WatchClassicAsync(SavedDevice saved)
    {
        var device = await BluetoothDevice.FromBluetoothAddressAsync(saved.Address);
        if (device is null)
        {
            return null;
        }

        var installed = await Task.Run(() => NativeBluetooth.TryGetInstalledServices(saved.Address));
        bool hasHid = installed?.Contains(NativeBluetooth.HidServiceClass) == true;
        Guid? containerId = hasHid ? await BatteryReader.TryGetContainerIdAsync(saved) : null;

        Watched? watched = null;
        TypedEventHandler<BluetoothDevice, object> handler = (_, _) => OnConnectionChanged(watched!);
        watched = new Watched(
            () => device.ConnectionStatus == BluetoothConnectionStatus.Connected,
            () =>
            {
                device.ConnectionStatusChanged -= handler;
                device.Dispose();
            })
        {
            HasHid = hasHid && containerId is not null,
            ContainerId = containerId,
        };
        device.ConnectionStatusChanged += handler;
        return watched;
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

    private sealed class Watched(Func<bool> isConnected, Action release)
    {
        public int CheckVersion;

        public bool HasHid { get; init; }
        public Guid? ContainerId { get; init; }

        /// <summary>null: not checked or not connected; false: connected but no HID collections.</summary>
        public bool? HidReady { get; set; }

        public bool Released { get; private set; }

        public bool IsConnected() => !Released && isConnected();

        public void Release()
        {
            Released = true;
            release();
        }
    }
}
