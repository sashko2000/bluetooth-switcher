using MagicSwitcher.Infrastructure;
using MagicSwitcher.Service;

namespace MagicSwitcher.Keyboard;

/// <summary>
/// "Fn Lock" for Apple keyboards: whether the top row sends F-keys instead of media keys. Apple's driver always stays
/// in its default (media keys) mode; the <see cref="KeyRemapper"/> does the swap in the user's session, and Fn+Esc
/// toggles it. The state lives in a small file next to the process that runs the remapper: the service's helper
/// (%ProgramData%) or, without the service, the tray app (%AppData%).
/// </summary>
internal static class AppleTopRow
{
    private const string FileName = "top-row.txt";
    private const string FKeys = "fkeys";
    private const string Media = "media";

    public static string MachinePath { get; } = Path.Combine(AppPaths.MachineDataDirectory, FileName);

    public static string UserPath { get; } = Path.Combine(AppPaths.UserDataDirectory, FileName);

    /// <summary>The file of the remapper that is in charge now: the service's when it runs, otherwise the tray's.</summary>
    public static string CurrentPath =>
        ServiceInstaller.GetState() is ServiceState.Running or ServiceState.Starting ? MachinePath : UserPath;

    public static bool Exists(string path) => File.Exists(path);

    /// <summary>true: the top row sends F1–F12; false (default): media keys.</summary>
    public static bool ReadFKeys(string path)
    {
        try
        {
            return File.Exists(path) && File.ReadAllText(path).Trim() == FKeys;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Write(string path, bool fKeys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, fKeys ? FKeys : Media);
    }
}
