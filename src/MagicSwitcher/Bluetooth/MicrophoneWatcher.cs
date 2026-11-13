using MagicSwitcher.Infrastructure;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace MagicSwitcher.Bluetooth;

/// <summary>
/// Notices a Bluetooth speaker or headset that is connected with its Hands-Free profile enabled but provides no
/// microphone to Windows ("Connected audio" instead of "Connected mic, audio"). Covers every connected audio device,
/// not only the ones selected in settings.
/// <para>
/// The microphone endpoint appears a few seconds after the device connects, so a device counts as having lost it only
/// when it is missing in two checks in a row. Each loss is reported once: again only after the microphone came back
/// or the device reconnected.
/// </para>
/// </summary>
internal sealed class MicrophoneWatcher
{
    private const int MissesToReport = 2;

    private readonly Dictionary<ulong, int> _misses = [];
    private readonly Dictionary<ulong, SavedDevice> _lost = [];

    /// <summary>Devices currently known to be without their microphone.</summary>
    public IReadOnlyCollection<SavedDevice> Lost => _lost.Values;

    /// <summary>Checks the connected audio devices. Returns the ones whose microphone was found lost just now.</summary>
    public async Task<IReadOnlyList<SavedDevice>> CheckAsync()
    {
        var missing = await FindWithoutMicrophoneAsync();
        var found = new List<SavedDevice>();

        foreach (ulong address in _misses.Keys.Where(a => !missing.ContainsKey(a)).ToList())
        {
            _misses.Remove(address);
            if (_lost.Remove(address, out var back))
            {
                Log.Info($"[{back.DisplayName}] the microphone is back (or the device disconnected)");
            }
        }

        foreach (var (address, device) in missing)
        {
            int count = _misses.GetValueOrDefault(address) + 1;
            _misses[address] = count;
            if (count == MissesToReport)
            {
                Log.Info($"[{device.DisplayName}] connected with Hands-Free enabled, but Windows has no microphone for it");
                _lost[address] = device;
                found.Add(device);
            }
        }

        return found;
    }

    /// <summary>After a repair attempt: count from the start, but do not report the same loss again.</summary>
    public void Restart()
    {
        foreach (ulong address in _misses.Keys.Where(a => !_lost.ContainsKey(a)).ToList())
        {
            _misses.Remove(address);
        }
    }

    public void Clear()
    {
        _misses.Clear();
        _lost.Clear();
    }

    private static async Task<Dictionary<ulong, SavedDevice>> FindWithoutMicrophoneAsync()
    {
        var result = new Dictionary<ulong, SavedDevice>();
        try
        {
            string selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var connected = await DeviceInformation.FindAllAsync(selector).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var info in connected)
            {
                SavedDevice saved;
                using (var device = await BluetoothDevice.FromIdAsync(info.Id).AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
                {
                    if (device is null || device.ClassOfDevice.MajorClass != BluetoothMajorClass.AudioVideo
                        || device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                    {
                        continue;
                    }

                    saved = new SavedDevice
                    {
                        Address = device.BluetoothAddress,
                        Name = string.IsNullOrWhiteSpace(device.Name) ? info.Name : device.Name,
                    };
                }

                // Hands-Free turned off in the device's properties is the user's choice, not a lost microphone.
                var installed = await Task.Run(() => NativeBluetooth.TryGetInstalledServices(saved.Address));
                if (installed is null || !NativeBluetooth.HasMicrophoneProfile(installed))
                {
                    continue;
                }

                // A microphone disabled in the Sound settings still has its endpoint: that is not a loss either.
                if (await AudioEndpoints.HasMicrophoneAsync(saved, includeDisabled: true) == false)
                {
                    result[saved.Address] = saved;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Microphone watch failed: {ex.GetType().Name}: {ex.Message}");
        }

        return result;
    }
}
