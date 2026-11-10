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
