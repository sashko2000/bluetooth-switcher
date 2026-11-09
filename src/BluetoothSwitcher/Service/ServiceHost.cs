using System.Runtime.InteropServices;
using BluetoothSwitcher.Infrastructure;
using static BluetoothSwitcher.Service.ServiceNative;

namespace BluetoothSwitcher.Service;

/// <summary>Events the service reacts to.</summary>
internal enum ServiceTrigger
{
    Startup,
    Logon,
    Unlock,
    Resume,
    DisplayOn,
    Request,
}

/// <summary>
/// Runs the app as a Windows service (started by the Service Control Manager with <c>--service</c>),
/// with plain Win32 calls. Reports logon, unlock, resume and "display on" events to <see cref="BluetoothService"/>.
/// </summary>
internal static class ServiceHost
{
    public const string ServiceName = "BluetoothSwitcher";

    // Delegates passed to Windows must stay alive for the whole process lifetime.
    private static ServiceMainFunction? _main;
    private static HandlerEx? _handler;
    private static IntPtr _statusHandle;
    private static SERVICE_STATUS _status;
    private static readonly ManualResetEventSlim Stopped = new();
    private static BluetoothService? _service;

    /// <summary>Blocks until the service stops. Returns false if the process was not started by the SCM.</summary>
    public static bool Run()
    {
        _main = ServiceMain;
        var table = new[]
        {
            new SERVICE_TABLE_ENTRY { lpServiceName = ServiceName, lpServiceProc = Marshal.GetFunctionPointerForDelegate(_main) },
            new SERVICE_TABLE_ENTRY(),
        };

        if (!StartServiceCtrlDispatcher(table))
        {
            Log.Error($"StartServiceCtrlDispatcher failed: error {Marshal.GetLastPInvokeError()} (not started as a service?)");
            return false;
        }

        return true;
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _handler = OnControl;
        _statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, _handler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            Log.Error($"RegisterServiceCtrlHandlerEx failed: error {Marshal.GetLastPInvokeError()}");
            return;
        }

        _status = new SERVICE_STATUS { dwServiceType = ServiceWin32OwnProcess };
        Report(StateStartPending, waitHint: 10000);

        try
        {
            var display = ConsoleDisplayState;
            if (RegisterPowerSettingNotification(_statusHandle, ref display, DeviceNotifyServiceHandle) == IntPtr.Zero)
            {
                Log.Info($"RegisterPowerSettingNotification failed: error {Marshal.GetLastPInvokeError()}");
            }

            _service = new BluetoothService();
            _service.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Service failed to start", ex);
            _status.dwWin32ExitCode = 1064; // ERROR_EXCEPTION_IN_SERVICE
            Report(StateStopped);
            return;
        }

        Report(StateRunning);
        Stopped.Wait();

        try
        {
            _service.Stop();
        }
        catch (Exception ex)
        {
            Log.Error("Service failed to stop cleanly", ex);
        }

        Log.Info("Service stopped");
        Report(StateStopped);
    }

    private static uint OnControl(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        // Must return quickly: the service reacts on its own threads.
        switch (control)
        {
            case ControlStop:
            case ControlShutdown:
                Report(StateStopPending, waitHint: 10000);
                Stopped.Set();
                break;

            case ControlSessionChange when eventType == WtsSessionLogon:
                _service?.OnTrigger(ServiceTrigger.Logon);
                break;

            case ControlSessionChange when eventType == WtsSessionUnlock:
                _service?.OnTrigger(ServiceTrigger.Unlock);
                break;

            case ControlPowerEvent when eventType == PbtApmResumeAutomatic:
                _service?.OnTrigger(ServiceTrigger.Resume);
                break;

            case ControlPowerEvent when eventType == PbtPowerSettingChange && eventData != IntPtr.Zero:
                // POWERBROADCAST_SETTING { GUID PowerSetting; DWORD DataLength; UCHAR Data[1]; }
                var setting = Marshal.PtrToStructure<Guid>(eventData);
                int length = Marshal.ReadInt32(eventData, 16);
                if (setting == ConsoleDisplayState && length >= 4 && Marshal.ReadInt32(eventData, 20) == 1)
                {
                    _service?.OnTrigger(ServiceTrigger.DisplayOn);
                }

                break;
        }

        return 0; // NO_ERROR (also the answer to INTERROGATE)
    }

    private static void Report(uint state, uint waitHint = 0)
    {
        _status.dwCurrentState = state;
        _status.dwWaitHint = waitHint;
        _status.dwControlsAccepted = state == StateRunning
            ? AcceptStop | AcceptShutdown | AcceptSessionChange | AcceptPowerEvent
            : 0;
        _status.dwCheckPoint = state is StateStartPending or StateStopPending ? _status.dwCheckPoint + 1 : 0;
        SetServiceStatus(_statusHandle, ref _status);
    }
}
