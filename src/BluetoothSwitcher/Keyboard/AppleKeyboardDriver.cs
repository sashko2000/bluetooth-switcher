using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using BluetoothSwitcher.Infrastructure;
using Microsoft.Win32;
using SharpCompress.Archives.Rar;
using SharpCompress.Readers;

namespace BluetoothSwitcher.Keyboard;

/// <summary>
/// Apple's Boot Camp keyboard filter driver (KeyMagic2) for Magic Keyboard 2015–2021 over Bluetooth/USB.
/// It handles the Fn key (Fn+Backspace = Delete, media keys on the top row), which Windows ignores on its own.
/// <para>
/// The driver is Apple's proprietary software, so it is not shipped with this app: on request it is downloaded
/// from Apple's own update server (Boot Camp 6.1.14 update, which is the first one to know the 2021 keyboards,
/// e.g. A2450 = PID 029C), verified by SHA-256, and installed with pnputil.
/// </para>
/// </summary>
internal static class AppleKeyboardDriver
{
    /// <summary>Boot Camp 6.1.14 update (May 2021): a RAR self-extracting archive.</summary>
    private const string DownloadUrl =
        "https://swcdn.apple.com/content/downloads/43/08/001-80084/t8tzfk4fhrphp9syj8g7imyrqginklwknk/AppleBcUpdate.exe";

    private const string ExpectedSha256 = "DEA199414AC0A57F700BAC4CD493C573FAC07C512B550AFD4F6B9B903B63C6E7";

    /// <summary>Folder inside the archive with the Magic Keyboard driver (Keymagic2.inf 6.1.8086.4).</summary>
    private const string DriverFolder = "AppleKeyboardMagic2";
    private const string DriverInf = "Keymagic2.inf";

    /// <summary>Service key of the filter driver. "OSXFnBehavior": 1 = media keys by default, 0 = F1–F12 by default.</summary>
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\KeyMagic2";
    private const string FnBehaviorValue = "OSXFnBehavior";

    private const int ErrorSuccessRebootRequired = 3010;
    private const int ErrorNoMoreItems = 259; // pnputil: package added, but no device was updated

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>Product IDs listed in Keymagic2.inf: Magic Keyboard 2015/2017 (0267, 026C) and 2021 (029A, 029C, 029F).</summary>
    private static readonly string[] SupportedProductIds = ["0267", "026C", "029A", "029C", "029F"];

    /// <summary>
    /// True if a keyboard supported by the driver is paired with (or was plugged into) this computer,
    /// even if it is not connected right now. Checked in HKLM\SYSTEM\CurrentControlSet\Enum.
    /// </summary>
    public static bool IsSupportedKeyboardPresent()
    {
        // Bluetooth: BTHENUM\{00001124-…}_VID&0001004c_PID&029c; USB: USB\VID_05AC&PID_029C&MI_01.
        return HasEnumKey("BTHENUM", name => SupportedProductIds.Any(pid =>
                   name.Contains($"_VID&0001004c_PID&{pid}", StringComparison.OrdinalIgnoreCase)))
               || HasEnumKey("USB", name => SupportedProductIds.Any(pid =>
                   name.StartsWith($"VID_05AC&PID_{pid}", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasEnumKey(string bus, Func<string, bool> match)
    {
        try
        {
            using var busKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{bus}");
            return busKey?.GetSubKeyNames().Any(match) == true;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }

    public static bool IsInstalled
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
            return key is not null;
        }
    }

    /// <summary>true = top row sends media keys by default (Mac behavior); null if the driver is not installed.</summary>
    public static bool? MediaKeysByDefault
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
            if (key is null)
            {
                return null;
            }

            // The driver's INF sets 1 (Mac behavior) on installation.
            return key.GetValue(FnBehaviorValue) is int value ? value != 0 : true;
        }
    }

    public enum InstallResult
    {
        Installed,
        InstalledRebootRequired,
        AlreadyUpToDate,
        AddedNoDevice,
    }

    /// <summary>Downloads, verifies, extracts and installs the driver. Throws on failure.</summary>
    public static async Task<InstallResult> InstallAsync(IProgress<InstallStage> progress, CancellationToken ct = default)
    {
        string workDir = Path.Combine(Path.GetTempPath(), "BluetoothSwitcher", "AppleKeyboardDriver");
        if (Directory.Exists(workDir))
        {
            Directory.Delete(workDir, recursive: true);
        }

        Directory.CreateDirectory(workDir);
        string archivePath = Path.Combine(workDir, "AppleBcUpdate.exe");
        string driverDir = Path.Combine(workDir, DriverFolder);

        try
        {
            progress.Report(InstallStage.Downloading);
            Log.Info($"Apple keyboard driver: downloading {DownloadUrl}");
            await using (var response = await Http.GetStreamAsync(DownloadUrl, ct))
            await using (var file = File.Create(archivePath))
            {
                await response.CopyToAsync(file, ct);
            }

            string actual;
            await using (var file = File.OpenRead(archivePath))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            }

            if (!string.Equals(actual, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unexpected SHA-256 of the downloaded file: {actual}");
            }

            progress.Report(InstallStage.Extracting);
            await Task.Run(() => ExtractDriver(archivePath, driverDir), ct);

            string inf = Path.Combine(driverDir, DriverInf);
            if (!File.Exists(inf))
            {
                throw new FileNotFoundException($"{DriverInf} was not found in the downloaded package.");
            }

            bool wasInstalled = IsInstalled;
            progress.Report(InstallStage.Installing);
            int exitCode = await RunAsAdminAsync(
                Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                $"/add-driver \"{inf}\" /install", ct);
            Log.Info($"Apple keyboard driver: pnputil exit code {exitCode}");

            return exitCode switch
            {
                0 => InstallResult.Installed,
                ErrorSuccessRebootRequired => InstallResult.InstalledRebootRequired,
                // 259: the package was added but no device needed an update — either the keyboard already
                // uses this driver, or no supported keyboard is connected.
                ErrorNoMoreItems => wasInstalled ? InstallResult.AlreadyUpToDate : InstallResult.AddedNoDevice,
                _ => throw new Win32Exception(exitCode),
            };
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* temp folder, ignore */ }
        }
    }

    /// <summary>
    /// Switches the default behavior of the top row. The driver reads the value only when it starts,
    /// so every keyboard that uses it is restarted right away (pnputil /restart-device).
    /// </summary>
    /// <returns>true if at least one keyboard was restarted, i.e. the new behavior is already active.</returns>
    public static async Task<bool> SetMediaKeysByDefaultAsync(bool media, CancellationToken ct = default)
    {
        int value = media ? 1 : 0;
        var devices = FindDevicesUsingDriver();
        string pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        Log.Info($"Apple keyboard: OSXFnBehavior = {value}, devices to restart: {string.Join(", ", devices)}");

        if (Elevation.IsElevated)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(ServiceKey, writable: true)
                ?? throw new InvalidOperationException("The Apple keyboard driver is not installed."))
            {
                key.SetValue(FnBehaviorValue, value, RegistryValueKind.DWord);
            }

            bool restarted = false;
            foreach (var device in devices)
            {
                int code = await RunAsAdminAsync(pnputil, $"/restart-device \"{device}\"", ct);
                restarted |= code is 0 or ErrorSuccessRebootRequired;
            }

            return restarted;
        }

        // Not elevated: one UAC prompt for the registry change and the restarts together.
        string command = $"reg add \"HKLM\\{ServiceKey}\" /v {FnBehaviorValue} /t REG_DWORD /d {value} /f";
        foreach (var device in devices)
        {
            command += $" & \"{pnputil}\" /restart-device \"{device}\"";
        }

        int exitCode = await RunAsAdminAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c \"{command}\"", ct);
        Log.Info($"Apple keyboard: elevated command exit code {exitCode}");
        if (MediaKeysByDefault != media)
        {
            throw new Win32Exception(exitCode);
        }

        return devices.Count > 0; // the exit code of cmd is the one of the last restart, not reliable per device
    }

    /// <summary>
    /// Device instance IDs that have the KeyMagic2 filter (Bluetooth and USB Magic Keyboards),
    /// read from HKLM\SYSTEM\CurrentControlSet\Enum (readable without administrator rights).
    /// </summary>
    private static List<string> FindDevicesUsingDriver()
    {
        var result = new List<string>();
        foreach (var bus in new[] { "BTHENUM", "USB" })
        {
            try
            {
                using var busKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{bus}");
                if (busKey is null)
                {
                    continue;
                }

                foreach (var deviceName in busKey.GetSubKeyNames())
                {
                    using var deviceKey = busKey.OpenSubKey(deviceName);
                    if (deviceKey is null)
                    {
                        continue;
                    }

                    foreach (var instanceName in deviceKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var instanceKey = deviceKey.OpenSubKey(instanceName);
                            if (instanceKey?.GetValue("LowerFilters") is string[] filters &&
                                filters.Contains("KeyMagic2", StringComparer.OrdinalIgnoreCase))
                            {
                                result.Add($@"{bus}\{deviceName}\{instanceName}");
                            }
                        }
                        catch (System.Security.SecurityException)
                        {
                            // Some instance keys are not readable; they cannot be ours anyway.
                        }
                    }
                }
            }
            catch (System.Security.SecurityException)
            {
            }
        }

        return result;
    }

    public enum InstallStage
    {
        Downloading,
        Extracting,
        Installing,
    }

    /// <summary>Extracts only the AppleKeyboardMagic2 folder (flattened, no paths from the archive are trusted).</summary>
    private static void ExtractDriver(string archivePath, string driverDir)
    {
        Directory.CreateDirectory(driverDir);
        using var archive = RarArchive.Open(archivePath, new ReaderOptions { LookForHeader = true }); // skip the SFX stub
        using var reader = archive.ExtractAllEntries(); // the archive is solid: read sequentially
        int count = 0;
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory || reader.Entry.Key is not { } key)
            {
                continue;
            }

            var parts = key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !string.Equals(parts[0], DriverFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var output = File.Create(Path.Combine(driverDir, Path.GetFileName(parts[1])));
            reader.WriteEntryTo(output);
            count++;
        }

        Log.Info($"Apple keyboard driver: extracted {count} file(s)");
    }

    /// <summary>Runs a tool with administrator rights (directly if already elevated, otherwise via a UAC prompt).</summary>
    private static async Task<int> RunAsAdminAsync(string fileName, string arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        if (Elevation.IsElevated)
        {
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
        }
        else
        {
            info.UseShellExecute = true;
            info.Verb = "runas";
        }

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC prompt declined
        {
            throw new OperationCanceledException(ex.Message, ex);
        }

        using (process)
        {
            if (process is null)
            {
                throw new InvalidOperationException($"Could not start {Path.GetFileName(fileName)}.");
            }

            if (info.RedirectStandardOutput)
            {
                string output = await process.StandardOutput.ReadToEndAsync(ct);
                Log.Info($"{Path.GetFileName(fileName)} {arguments}{Environment.NewLine}{output.Trim()}");
            }

            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
    }
}
