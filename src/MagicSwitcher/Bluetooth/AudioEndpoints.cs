using MagicSwitcher.Infrastructure;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace MagicSwitcher.Bluetooth;

/// <summary>
/// Whether a Bluetooth speaker or headset currently provides a microphone to Windows. The Hands-Free capture endpoint
/// exists (enabled) only while the Hands-Free profile is connected; when a speaker "forgets" its microphone, Windows still
/// shows it as connected (audio only) but the endpoint is gone.
/// </summary>
internal static class AudioEndpoints
{
    /// <summary>true/false; null if it cannot be determined (the device's PnP container was not found).</summary>
    public static async Task<bool?> HasMicrophoneAsync(SavedDevice device)
    {
        try
        {
            if (await BatteryReader.TryGetContainerIdAsync(device) is not { } containerId)
            {
                return null;
            }

            string aqs = $"{MediaDevice.GetAudioCaptureSelector()} AND System.Devices.ContainerId:=\"{{{containerId}}}\"";
            var endpoints = await DeviceInformation.FindAllAsync(aqs).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            return endpoints.Count > 0;
        }
        catch (Exception ex)
        {
            Log.Info($"[{device.DisplayName}] microphone check failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Polls until the microphone appears. Returns false if it did not within <paramref name="timeout"/>, null if unknown.</summary>
    public static async Task<bool?> WaitForMicrophoneAsync(SavedDevice device, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var present = await HasMicrophoneAsync(device);
            if (present != false || DateTime.UtcNow >= deadline)
            {
                return present;
            }

            await Task.Delay(1000, ct);
        }
    }
}
