using BluetoothSwitcher.Infrastructure;
using BluetoothSwitcher.UI;

namespace BluetoothSwitcher;

internal static class Program
{
    private const string MutexName = @"Local\BluetoothSwitcher.SingleInstance";
    internal const string RestartArgument = "--restart";

    [STAThread]
    private static void Main(string[] args)
    {
        bool isRestart = args.Contains(RestartArgument, StringComparer.OrdinalIgnoreCase);

        Mutex? mutex = null;
        bool ownsMutex = false;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out ownsMutex);
            if (!ownsMutex && isRestart)
            {
                // Предыдущий экземпляр (например, при перезапуске от имени администратора)
                // ещё завершается — даём ему время.
                try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { ownsMutex = true; }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Мьютекс создан процессом с повышенными правами — значит, приложение уже запущено.
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
            Log.Info($"Запуск. Права администратора: {Elevation.IsElevated}");
            Application.Run(new TrayApplicationContext());
        }
        finally
        {
            mutex!.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
