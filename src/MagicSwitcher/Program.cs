using MagicSwitcher.Infrastructure;
using MagicSwitcher.Service;
using MagicSwitcher.UI;

namespace MagicSwitcher;

internal static class Program
{
    private const string MutexName = @"Local\MagicSwitcher.SingleInstance";
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

        if (Has(args, RemapHelper.HelperArgument))
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
            return RemapHelper.RunHelper();
        }

        if (Has(args, WindowsAudio.RestartArgument))
        {
            return WindowsAudio.Restart();
        }

        if (Has(args, PrivilegedActions.ReinstallArgument))
        {
            return PrivilegedActions.RunReinstallElevated(args);
        }

        RunTray();
        return 0;
    }

    private static void RunTray()
    {
        // Already running (e.g. without a visible icon): ask that instance to show itself instead of silently exiting.
        if (IsAnotherInstanceRunning())
        {
            ShellMessages.BroadcastShowInstance();
            return;
        }

        // Bluetooth Switcher 1.0 ran elevated through a scheduled task; the service replaces it.
        // The task starts the old executable, so it is removed here (one UAC prompt unless already elevated).
        if (ElevatedTask.Exists())
        {
            ElevatedTask.Remove();
        }

        Mutex? mutex = null;
        bool ownsMutex = false;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out ownsMutex);
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

        LegacyMigration.MigrateUserData();

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
