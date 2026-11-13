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
    /// <param name="includeDisabled">Also count an endpoint that is turned off (e.g. disabled in the Sound settings).</param>
    public static async Task<bool?> HasMicrophoneAsync(SavedDevice device, bool includeDisabled = false)
    {
        try
        {
            if (await BatteryReader.TryGetContainerIdAsync(device) is not { } containerId)
            {
                return null;
            }

            string aqs = $"{MediaDevice.GetAudioCaptureSelector()} AND System.Devices.ContainerId:=\"{{{containerId}}}\"";
            var endpoints = await DeviceInformation.FindAllAsync(aqs).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (endpoints.Count > 0 || !includeDisabled)
            {
                return endpoints.Count > 0;
            }

            // Without an explicit InterfaceEnabled clause the query returns enabled interfaces only.
            try
            {
                var disabled = await DeviceInformation
                    .FindAllAsync($"{aqs} AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#False")
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                return disabled.Count > 0;
            }
            catch (Exception ex)
            {
                // The first query did answer: no enabled microphone. Do not let this extra query hide that.
                Log.Info($"[{device.DisplayName}] disabled microphone query failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
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
