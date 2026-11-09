using System.ComponentModel;
using System.Runtime.InteropServices;
using BluetoothSwitcher.Infrastructure;
using static BluetoothSwitcher.Service.ServiceNative;

namespace BluetoothSwitcher.Service;

internal enum ServiceState
{
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Stopping,
}

/// <summary>
/// Installs the service: copies the app to %ProgramFiles%\Bluetooth Switcher (a service running as SYSTEM must not
/// start an executable that a normal user can replace, e.g. in Downloads) and registers it to start automatically.
/// The tray app runs <c>BluetoothSwitcher.exe --install-service</c> / <c>--uninstall-service</c> elevated (one UAC prompt).
/// </summary>
internal static class ServiceInstaller
{
    public const string InstallArgument = "--install-service";
    public const string UninstallArgument = "--uninstall-service";

    private const string DisplayName = "Bluetooth Switcher";
    private const string Description =
        "Restores the Bluetooth keyboards selected in Bluetooth Switcher at startup and sign-in (pairs them again without " +
        "a dialog if another device took them over), and performs actions that need administrator rights for the tray app.";

    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Bluetooth Switcher");

    private static string InstalledExe => Path.Combine(InstallDirectory, "BluetoothSwitcher.exe");

    public static ServiceState GetState()
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            return ServiceState.NotInstalled;
        }

        try
        {
            var service = OpenService(scm, ServiceHost.ServiceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return ServiceState.NotInstalled;
            }

            try
            {
                var status = new SERVICE_STATUS();
                if (!QueryServiceStatus(service, ref status))
                {
                    return ServiceState.Stopped;
                }

                return status.dwCurrentState switch
                {
                    StateRunning => ServiceState.Running,
                    StateStartPending => ServiceState.Starting,
                    StateStopPending => ServiceState.Stopping,
                    _ => ServiceState.Stopped,
                };
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static bool IsInstalled => GetState() != ServiceState.NotInstalled;

    /// <summary>Installs or updates the service (one UAC prompt). Throws <see cref="OperationCanceledException"/> if declined.</summary>
    public static async Task InstallAsync(CancellationToken ct = default) => await RunElevatedAsync(InstallArgument, ct);

    /// <summary>Stops and removes the service (one UAC prompt).</summary>
    public static async Task UninstallAsync(CancellationToken ct = default) => await RunElevatedAsync(UninstallArgument, ct);

    private static async Task RunElevatedAsync(string argument, CancellationToken ct)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        int exitCode = await Elevation.RunAsAdminAsync(exe, argument, ct);
        Log.Info($"{argument} -> {exitCode}");
        if (exitCode != 0)
        {
            throw new Win32Exception(exitCode);
        }
    }

    /// <summary>Runs in the elevated helper process. Returns 0 or a Win32 error code.</summary>
    public static int InstallElevated()
    {
        try
        {
            StopIfRunning();
            CopyFiles();
            Register();
            Start();
            Log.Info($"Service installed to {InstallDirectory}");
            return 0;
        }
        catch (Win32Exception ex)
        {
            Log.Error("Service installation failed", ex);
            return ex.NativeErrorCode;
        }
        catch (Exception ex)
        {
            Log.Error("Service installation failed", ex);
            return ex is UnauthorizedAccessException ? 5 : 1603; // ERROR_ACCESS_DENIED / ERROR_INSTALL_FAILURE
        }
    }

    /// <summary>Runs in the elevated helper process. Returns 0 or a Win32 error code.</summary>
    public static int UninstallElevated()
    {
        try
        {
            StopIfRunning();
            WithService(ServiceAllAccess, service =>
            {
                if (!DeleteService(service))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            });

            DeleteWithRetry(InstallDirectory);
            try { File.Delete(BluetoothService.ConfigPath); } catch { /* keep going */ }
            Log.Info("Service removed");
            return 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorServiceDoesNotExist)
        {
            return 0;
        }
        catch (Win32Exception ex)
        {
            Log.Error("Service removal failed", ex);
            return ex.NativeErrorCode;
        }
        catch (Exception ex)
        {
            Log.Error("Service removal failed", ex);
            return 1603;
        }
    }

    /// <summary>
    /// A self-contained single-file build is one exe: copy just it. A framework-dependent build (e.g. from Visual Studio)
    /// needs its whole output folder.
    /// </summary>
    private static void CopyFiles()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        Directory.CreateDirectory(InstallDirectory);
        bool singleFile = string.IsNullOrEmpty(typeof(ServiceInstaller).Assembly.Location);
        if (singleFile)
        {
            CopyWithRetry(exe, InstalledExe);
            return;
        }

        string source = AppContext.BaseDirectory;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(InstallDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyWithRetry(file, target);
        }
    }

    private static void CopyWithRetry(string source, string target)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            return; // installing from the installed copy
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(500); // the stopped service may still be releasing the file
            }
        }
    }

    private static void DeleteWithRetry(string directory)
    {
        for (int attempt = 1; Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static void Register()
    {
        string binaryPath = $"\"{InstalledExe}\" --service";
        var scm = OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            var service = OpenService(scm, ServiceHost.ServiceName, ServiceAllAccess);
            if (service == IntPtr.Zero)
            {
                service = CreateService(scm, ServiceHost.ServiceName, DisplayName, ServiceAllAccess, ServiceWin32OwnProcess,
                    ServiceAutoStart, ServiceErrorNormal, binaryPath, null, IntPtr.Zero, null, null, null);
                if (service == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            else if (!ChangeServiceConfig(service, ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal, binaryPath,
                         null, IntPtr.Zero, null, null, null, DisplayName))
            {
                int error = Marshal.GetLastPInvokeError();
                CloseServiceHandle(service);
                throw new Win32Exception(error);
            }

            try
            {
                var description = new SERVICE_DESCRIPTION { lpDescription = Description };
                ChangeServiceConfig2(service, ServiceConfigDescription, ref description);

                // Restart after a crash: 5 s, 10 s, then 1 min; the counter resets after a day.
                var actions = new[]
                {
                    new SC_ACTION { Type = 1, Delay = 5000 },
                    new SC_ACTION { Type = 1, Delay = 10000 },
                    new SC_ACTION { Type = 1, Delay = 60000 },
                };
                var handle = GCHandle.Alloc(actions, GCHandleType.Pinned);
                try
                {
                    var failure = new SERVICE_FAILURE_ACTIONS
                    {
                        dwResetPeriod = 86400,
                        cActions = (uint)actions.Length,
                        lpsaActions = handle.AddrOfPinnedObject(),
                    };
                    ChangeServiceConfig2(service, ServiceConfigFailureActions, ref failure);
                }
                finally
                {
                    handle.Free();
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static void Start() => WithService(ServiceStart | ServiceQueryStatus, service =>
    {
        if (!StartService(service, 0, IntPtr.Zero) && Marshal.GetLastPInvokeError() is var error && error != ErrorServiceAlreadyRunning)
        {
            throw new Win32Exception(error);
        }
    });

    private static void StopIfRunning()
    {
        try
        {
            WithService(ServiceStop | ServiceQueryStatus, service =>
            {
                var status = new SERVICE_STATUS();
                if (!ControlService(service, ControlStop, ref status))
                {
                    return; // not running
                }

                for (int i = 0; i < 60 && QueryServiceStatus(service, ref status) && status.dwCurrentState != StateStopped; i++)
                {
                    Thread.Sleep(250);
                }
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorServiceDoesNotExist)
        {
            // First installation.
        }
    }

    private static void WithService(uint access, Action<IntPtr> action)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            var service = OpenService(scm, ServiceHost.ServiceName, access);
            if (service == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                action(service);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }
}
