using System.ComponentModel;
using System.Runtime.InteropServices;
using MagicSwitcher.Infrastructure;
using static MagicSwitcher.Service.ServiceNative;

namespace MagicSwitcher.Service;

/// <summary>
/// Restarts the Windows audio services (Windows Audio Endpoint Builder and Windows Audio, with any services that
/// depend on them) — the same as <c>Restart-Service AudioEndpointBuilder -Force; Restart-Service Audiosrv -Force</c>.
/// Helps when sound and the microphone stop working everywhere (e.g. the audio DSP driver got stuck) and otherwise
/// only a reboot would help. Needs administrator rights: runs in the service or in an elevated helper.
/// </summary>
internal static class WindowsAudio
{
    public const string RestartArgument = "--restart-audio";

    private static readonly string[] Services = ["AudioEndpointBuilder", "Audiosrv"];
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    private const uint ServiceEnumerateDependents = 0x0008;
    private const uint ServiceActive = 0x01;

    /// <summary>Returns 0 or a Win32 error code.</summary>
    public static int Restart()
    {
        try
        {
            var scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero)
            {
                return Marshal.GetLastPInvokeError();
            }

            try
            {
                // Stop dependents first (Audiosrv depends on AudioEndpointBuilder), remember the order to start them again.
                var stopped = new List<string>();
                foreach (var name in Services)
                {
                    StopWithDependents(scm, name, stopped);
                }

                // While the services are stopped, restart the audio DSP's Bluetooth endpoints (see RestartBluetoothAudioDevices).
                RestartBluetoothAudioDevices();

                // Start in reverse order of stopping; also start the main services if they were not running.
                var toStart = Enumerable.Reverse(stopped).Concat(Services).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var name in toStart)
                {
                    StartAndWait(scm, name);
                }

                Log.Info($"Windows audio restarted (stopped: {string.Join(", ", stopped)})");
                return 0;
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }
        catch (Win32Exception ex)
        {
            Log.Error("Failed to restart Windows audio", ex);
            return ex.NativeErrorCode;
        }
    }

    /// <summary>
    /// On Intel Smart Sound laptops Bluetooth audio (including a headset's or speaker's Hands-Free microphone) is
    /// processed by the audio DSP through "Intel Smart Sound Technology for Bluetooth Audio" / "… LE Audio" devices.
    /// When that path gets stuck, the microphone does not come back even after reconnecting all audio profiles;
    /// restarting these devices resets it (otherwise only removing the speaker or a reboot helps).
    /// </summary>
    private static void RestartBluetoothAudioDevices()
    {
        List<(string Id, string Name)> devices;
        try
        {
            devices = FindMediaDevices(name => name.Contains("Smart Sound", StringComparison.OrdinalIgnoreCase)
                                               && name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to list the Bluetooth audio devices", ex);
            return;
        }

        string pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        foreach (var (id, name) in devices)
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pnputil, $"/restart-device \"{id}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                });
                if (process is null)
                {
                    continue;
                }

                process.StandardOutput.ReadToEnd();
                bool exited = process.WaitForExit(30000);
                Log.Info($"Restarted '{name}' -> {(exited ? process.ExitCode.ToString() : "timeout")}");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to restart '{name}'", ex);
            }
        }
    }

    /// <summary>Present devices of the Media class (sound, video and game controllers) whose name matches.</summary>
    private static List<(string Id, string Name)> FindMediaDevices(Func<string, bool> match)
    {
        var mediaClass = new Guid("4D36E96C-E325-11CE-BFC1-08002BE10318");
        var result = new List<(string, string)>();
        var set = SetupDiGetClassDevs(ref mediaClass, null, IntPtr.Zero, DigcfPresent);
        if (set == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var buffer = new char[1024];
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                string? name = GetProperty(set, ref data, SpdrpFriendlyName, buffer) ?? GetProperty(set, ref data, SpdrpDeviceDesc, buffer);
                if (name is null || !match(name))
                {
                    continue;
                }

                if (SetupDiGetDeviceInstanceId(set, ref data, buffer, buffer.Length, out _))
                {
                    result.Add((new string(buffer).TrimEnd('\0').Split('\0')[0], name));
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    private static string? GetProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property, char[] buffer)
    {
        Array.Clear(buffer);
        return SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, (uint)(buffer.Length * 2), out _)
            ? new string(buffer).Split('\0')[0]
            : null;
    }

    private const uint DigcfPresent = 0x2;
    private const uint SpdrpDeviceDesc = 0x0;
    private const uint SpdrpFriendlyName = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint property, out uint propertyRegDataType, [Out] char[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        [Out] char[] deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    private static void StopWithDependents(IntPtr scm, string name, List<string> stopped)
    {
        var service = OpenService(scm, name, ServiceStop | ServiceQueryStatus | ServiceEnumerateDependents);
        if (service == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            foreach (var dependent in GetActiveDependents(service))
            {
                StopWithDependents(scm, dependent, stopped);
            }

            var status = new SERVICE_STATUS();
            if (!QueryServiceStatus(service, ref status) || status.dwCurrentState == StateStopped)
            {
                return;
            }

            if (!ControlService(service, ControlStop, ref status) && Marshal.GetLastPInvokeError() is var error && error != ErrorServiceNotActive)
            {
                throw new Win32Exception(error);
            }

            WaitFor(service, StateStopped);
            if (!stopped.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                stopped.Add(name);
            }
        }
        finally
        {
            CloseServiceHandle(service);
        }
    }

    private static void StartAndWait(IntPtr scm, string name)
    {
        var service = OpenService(scm, name, ServiceStart | ServiceQueryStatus);
        if (service == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            if (!StartService(service, 0, IntPtr.Zero) && Marshal.GetLastPInvokeError() is var error && error != ErrorServiceAlreadyRunning)
            {
                throw new Win32Exception(error);
            }

            WaitFor(service, StateRunning);
        }
        finally
        {
            CloseServiceHandle(service);
        }
    }

    private static void WaitFor(IntPtr service, uint state)
    {
        var status = new SERVICE_STATUS();
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (QueryServiceStatus(service, ref status) && status.dwCurrentState != state && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
        }
    }

    private static List<string> GetActiveDependents(IntPtr service)
    {
        var result = new List<string>();
        EnumDependentServices(service, ServiceActive, IntPtr.Zero, 0, out uint needed, out _);
        if (needed == 0)
        {
            return result;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumDependentServices(service, ServiceActive, buffer, needed, out _, out uint count))
            {
                return result;
            }

            int size = Marshal.SizeOf<ENUM_SERVICE_STATUS>();
            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS>(buffer + i * size);
                if (Marshal.PtrToStringUni(entry.lpServiceName) is { } name)
                {
                    result.Add(name);
                }
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ENUM_SERVICE_STATUS
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public SERVICE_STATUS ServiceStatus;
    }

    [DllImport("advapi32.dll", EntryPoint = "EnumDependentServicesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDependentServices(IntPtr service, uint serviceState, IntPtr services, uint bufSize,
        out uint bytesNeeded, out uint servicesReturned);
}
