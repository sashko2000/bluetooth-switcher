using BluetoothSwitcher.Infrastructure;
using BluetoothSwitcher.Service;
using BluetoothSwitcher.UI;

namespace BluetoothSwitcher;

internal static class Program
{
    private const string MutexName = @"Local\BluetoothSwitcher.SingleInstance";
    private const string ServiceArgument = "--service";

    private static bool IsAnotherInstanceRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName, out var existing))
            {
                existing.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // created by the elevated instance: it exists, we just cannot open it
        }
    }

    private static bool Has(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

    [STAThread]
    private static int Main(string[] args)
    {
        // The same executable is the Windows service and the elevated helpers the tray app starts.
        if (Has(args, ServiceArgument))
        {
            AppPaths.UseMachineData();
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Unobserved task exception", e.Exception); e.SetObserved(); };
            return ServiceHost.Run() ? 0 : 1;
        }

        if (Has(args, ServiceInstaller.InstallArgument))
        {
            return ServiceInstaller.InstallElevated();
        }

        if (Has(args, ServiceInstaller.UninstallArgument))
        {
            return ServiceInstaller.UninstallElevated();
        }

        if (Has(args, PrivilegedActions.ReinstallArgument))
        {
            return PrivilegedActions.RunReinstallElevated(args);
        }

        RunTray(args);
        return 0;
    }

    private static void RunTray(string[] args)
    {
        bool fromTask = Has(args, ElevatedTask.LaunchArgument);

        // Already running (e.g. without a visible icon): ask that instance to show itself instead of silently exiting.
        if (!fromTask && IsAnotherInstanceRunning())
        {
            ShellMessages.BroadcastShowInstance();
            return;
        }

        // Versions before 1.1 ran elevated through a scheduled task. The service replaces it: the elevated
        // instance removes the task (no UAC prompt needed), so start it once through the task if needed.
        if (ElevatedTask.Exists())
        {
            if (Elevation.IsElevated)
            {
                ElevatedTask.MigrateToRunKey();
            }
            else if (!fromTask && ElevatedTask.Run())
            {
                return;
            }
        }

        Mutex? mutex = null;
        bool ownsMutex = false;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out ownsMutex);
            if (!ownsMutex && fromTask)
            {
                // The instance that started the task is still shutting down, so give it some time.
                try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { ownsMutex = true; }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex was created by an elevated process, so the app is already running.
            ownsMutex = false;
        }

        if (!ownsMutex)
        {
            mutex?.Dispose();
            return;
        }

        Application.ThreadException += (_, e) => Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Unobserved task exception", e.Exception); e.SetObserved(); };

        try
        {
            ApplicationConfiguration.Initialize();
            Log.Info($"Started {AppInfo.Version}. Elevated: {Elevation.IsElevated}");
            Application.Run(new TrayApplicationContext());
        }
        finally
        {
            mutex!.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
