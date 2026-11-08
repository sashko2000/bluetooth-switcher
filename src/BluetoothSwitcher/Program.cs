using BluetoothSwitcher.Infrastructure;
using BluetoothSwitcher.UI;

namespace BluetoothSwitcher;

internal static class Program
{
    private const string MutexName = @"Local\BluetoothSwitcher.SingleInstance";
    internal const string RestartArgument = "--restart";

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

    [STAThread]
    private static void Main(string[] args)
    {
        bool isRestart = args.Contains(RestartArgument, StringComparer.OrdinalIgnoreCase);
        bool fromTask = args.Contains(ElevatedTask.LaunchArgument, StringComparer.OrdinalIgnoreCase);

        // Already running (e.g. without a visible icon): ask that instance to show itself instead of silently exiting.
        if (!isRestart && !fromTask && IsAnotherInstanceRunning())
        {
            ShellMessages.BroadcastShowInstance();
            return;
        }

        // "Run as administrator" is on (the scheduled task exists) but we were started without elevation:
        // hand over to the task, which starts an elevated instance without a UAC prompt.
        // fromTask prevents a loop when the task itself cannot elevate (e.g. a standard user account).
        if (!fromTask && !Elevation.IsElevated && ElevatedTask.Exists() && ElevatedTask.Run())
        {
            return;
        }

        Mutex? mutex = null;
        bool ownsMutex = false;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out ownsMutex);
            if (!ownsMutex && (isRestart || fromTask))
            {
                // The previous instance (e.g. when restarting as administrator)
                // is still shutting down, so give it some time.
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
            Log.Info($"Started. Elevated: {Elevation.IsElevated}");
            Application.Run(new TrayApplicationContext());
        }
        finally
        {
            mutex!.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
