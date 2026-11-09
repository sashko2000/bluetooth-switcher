using System.Runtime.InteropServices;

namespace MagicSwitcher.Service;

/// <summary>Service Control Manager API (advapi32), used instead of the ServiceController package.</summary>
internal static class ServiceNative
{
    public const uint ServiceWin32OwnProcess = 0x10;

    public const uint StateStopped = 1;
    public const uint StateStartPending = 2;
    public const uint StateStopPending = 3;
    public const uint StateRunning = 4;

    public const uint AcceptStop = 0x01;
    public const uint AcceptShutdown = 0x04;
    public const uint AcceptPowerEvent = 0x40;
    public const uint AcceptSessionChange = 0x80;

    public const uint ControlStop = 0x01;
    public const uint ControlInterrogate = 0x04;
    public const uint ControlShutdown = 0x05;
    public const uint ControlPowerEvent = 0x0D;
    public const uint ControlSessionChange = 0x0E;

    public const int WtsSessionLogon = 5;
    public const int WtsSessionUnlock = 8;

    public const int PbtApmResumeAutomatic = 0x12;
    public const int PbtPowerSettingChange = 0x8013;

    /// <summary>GUID_CONSOLE_DISPLAY_STATE: 0 = off, 1 = on, 2 = dimmed.</summary>
    public static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    public const uint ScManagerConnect = 0x0001;
    public const uint ScManagerCreateService = 0x0002;
    public const uint ServiceQueryStatus = 0x0004;
    public const uint ServiceStart = 0x0010;
    public const uint ServiceStop = 0x0020;
    public const uint ServiceChangeConfig = 0x0002;
    public const uint ServiceQueryConfig = 0x0001;
    public const uint Delete = 0x10000;
    public const uint ServiceAllAccess = 0xF01FF;

    public const uint ServiceAutoStart = 2;
    public const uint ServiceErrorNormal = 1;
    public const uint ServiceNoChange = 0xFFFFFFFF;

    public const uint ServiceConfigDescription = 1;
    public const uint ServiceConfigFailureActions = 2;

    public const int ErrorServiceDoesNotExist = 1060;
    public const int ErrorServiceAlreadyRunning = 1056;
    public const int ErrorServiceNotActive = 1062;

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SERVICE_TABLE_ENTRY
    {
        public string? lpServiceName;
        public IntPtr lpServiceProc;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SERVICE_DESCRIPTION
    {
        public string lpDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SC_ACTION
    {
        public uint Type; // 1 = restart
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SERVICE_FAILURE_ACTIONS
    {
        public uint dwResetPeriod;
        public string? lpRebootMsg;
        public string? lpCommand;
        public uint cActions;
        public IntPtr lpsaActions;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void ServiceMainFunction(int argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate uint HandlerEx(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StartServiceCtrlDispatcher([In] SERVICE_TABLE_ENTRY[] serviceTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr RegisterServiceCtrlHandlerEx(string serviceName, HandlerEx handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetServiceStatus(IntPtr statusHandle, ref SERVICE_STATUS status);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, uint flags);

    public const uint DeviceNotifyServiceHandle = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateService(
        IntPtr scManager, string serviceName, string displayName, uint desiredAccess, uint serviceType, uint startType,
        uint errorControl, string binaryPathName, string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, string? password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig(
        IntPtr service, uint serviceType, uint startType, uint errorControl, string? binaryPathName, string? loadOrderGroup,
        IntPtr tagId, string? dependencies, string? serviceStartName, string? password, string? displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref SERVICE_DESCRIPTION info);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref SERVICE_FAILURE_ACTIONS info);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StartService(IntPtr service, int numArgs, IntPtr args);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ControlService(IntPtr service, uint control, ref SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceStatus(IntPtr service, ref SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseServiceHandle(IntPtr handle);
}
