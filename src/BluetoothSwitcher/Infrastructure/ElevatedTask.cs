using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace BluetoothSwitcher.Infrastructure;

/// <summary>
/// "Run as administrator without a UAC prompt": a Task Scheduler task with the highest privileges.
/// Creating or deleting it needs one UAC confirmation; starting it afterwards does not, so the app
/// relaunches itself through the task when it is started without administrator rights.
/// <para>
/// The task is created from XML because the defaults of <c>schtasks /Create</c> do not suit a tray app:
/// it would be stopped after 3 days, not started on battery power, and run with below-normal priority.
/// </para>
/// </summary>
internal static class ElevatedTask
{
    public const string TaskName = "BluetoothSwitcher";

    /// <summary>Command-line argument the task passes, so a non-elevated start through it does not loop.</summary>
    public const string LaunchArgument = "--from-task";

    /// <summary>
    /// Start delay after logon: lets Explorer create the taskbar first, so the tray icon can be added right away.
    /// </summary>
    private const string LogonDelay = "PT15S";

    private static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public static bool Exists() => Query(out _);

    /// <summary>Whether the task exists and whether it starts the app at logon (one schtasks call).</summary>
    public static bool TryGetState(out bool startsAtLogon) => Query(out startsAtLogon);

    /// <summary>True if the task exists and starts the app at logon.</summary>
    public static bool StartsAtLogon() => Query(out bool logon) && logon;

    /// <summary>Starts the task (no UAC prompt). Returns false if it could not be started.</summary>
    public static bool Run()
    {
        bool ok = RunHidden($"/Run /TN \"{TaskName}\"", out _) == 0;
        Log.Info($"Elevated task: run -> {ok}");
        return ok;
    }

    /// <summary>Creates or replaces the task (needs administrator rights: one UAC prompt if not elevated).</summary>
    public static async Task CreateAsync(bool startAtLogon, CancellationToken ct = default)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        string xmlPath = Path.Combine(Path.GetTempPath(), $"BluetoothSwitcher-task-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(xmlPath, BuildXml(exe, startAtLogon), Encoding.Unicode, ct);
        try
        {
            int exitCode = await Elevation.RunAsAdminAsync(Schtasks, $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", ct);
            Log.Info($"Elevated task: create (logon = {startAtLogon}) -> {exitCode}");
            if (exitCode != 0)
            {
                throw new Win32Exception(exitCode);
            }
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* temp file */ }
        }
    }

    /// <summary>Deletes the task (needs administrator rights: one UAC prompt if not elevated).</summary>
    public static async Task DeleteAsync(CancellationToken ct = default)
    {
        int exitCode = await Elevation.RunAsAdminAsync(Schtasks, $"/Delete /TN \"{TaskName}\" /F", ct);
        Log.Info($"Elevated task: delete -> {exitCode}");
        if (exitCode != 0 && Exists())
        {
            throw new Win32Exception(exitCode);
        }
    }

    /// <summary>
    /// Replaces a task created by an older version of the app or for another executable path
    /// (e.g. missing the logon delay). Only when elevated, so there is no UAC prompt.
    /// </summary>
    public static async Task EnsureUpToDateAsync()
    {
        try
        {
            if (!Elevation.IsElevated || Environment.ProcessPath is not { } exe)
            {
                return;
            }

            var (exists, xml) = await Task.Run(() => (QueryXml(out string x), x));
            if (!exists)
            {
                return;
            }

            bool logon = xml.Contains("<LogonTrigger", StringComparison.OrdinalIgnoreCase);
            bool upToDate = xml.Contains(SecurityElement.Escape(exe), StringComparison.OrdinalIgnoreCase)
                            && xml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", StringComparison.OrdinalIgnoreCase)
                            && (!logon || xml.Contains($"<Delay>{LogonDelay}</Delay>", StringComparison.OrdinalIgnoreCase));
            if (!upToDate)
            {
                Log.Info("Elevated task is outdated: updating it");
                await CreateAsync(logon);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update the elevated task", ex);
        }
    }

    private static bool Query(out bool startsAtLogon)
    {
        bool exists = QueryXml(out string xml);
        startsAtLogon = exists && xml.Contains("<LogonTrigger", StringComparison.OrdinalIgnoreCase);
        return exists;
    }

    private static bool QueryXml(out string xml)
    {
        int exitCode = RunHidden($"/Query /TN \"{TaskName}\" /XML", out string output);
        // The XML may come back as UTF-16 bytes read as a narrow encoding: drop the zero chars before searching.
        xml = output.Replace("\0", "");
        return exitCode == 0;
    }

    private static int RunHidden(string arguments, out string output)
    {
        output = "";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Schtasks, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return -1;
            }

            var errors = process.StandardError.ReadToEndAsync();
            output = process.StandardOutput.ReadToEnd();
            _ = errors.Result;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error($"schtasks {arguments} failed", ex);
            return -1;
        }
    }

    private static string BuildXml(string exePath, bool startAtLogon)
    {
        string user;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            user = SecurityElement.Escape(identity.Name);
        }

        string trigger = startAtLogon
            ? $"""
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>{LogonDelay}</Delay>
                </LogonTrigger>
            """
            : "";

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Bluetooth Switcher: starts the app with administrator rights without a UAC prompt.</Description>
              </RegistrationInfo>
              <Triggers>
            {trigger}
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exePath)}</Command>
                  <Arguments>{LaunchArgument}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
