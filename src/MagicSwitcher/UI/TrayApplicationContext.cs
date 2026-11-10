using MagicSwitcher.Bluetooth;
using MagicSwitcher.Infrastructure;
using MagicSwitcher.Localization;
using MagicSwitcher.Service;

namespace MagicSwitcher.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string AppTitle = "MagicSwitcher";

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _iconNormal;
    private readonly Icon _iconBusy;
    private readonly SynchronizationContext _ui;
    private readonly DeviceStatusMonitor _monitor = new();
    private readonly HashSet<string> _busy = [];
    private readonly System.Windows.Forms.Timer _batteryTimer = new() { Interval = (int)TimeSpan.FromMinutes(5).TotalMilliseconds };
    private Dictionary<string, int> _battery = [];
    private DateTime _batteryRefreshedAt = DateTime.MinValue;
    private bool _batteryRefreshing;
    private readonly HashSet<string> _lowBatteryNotified = [];
    private readonly ShellMessageWindow _shellWindow;
    private readonly System.Windows.Forms.Timer _trayRetryTimer = new() { Interval = 2000 };
    private int _trayAttempts;

    private AppSettings _settings;
    private bool _audioRestarting;
    private SettingsForm? _settingsForm;

    public TrayApplicationContext()
    {
        _settings = SettingsStore.Load();
        AppLanguage.Apply(_settings.Language);

        _menu = new ContextMenuStrip();
        // ContextMenuStrip is a Control; once it is created, WindowsFormsSynchronizationContext is installed.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _iconNormal = TrayIconFactory.Create();
        _iconBusy = TrayIconFactory.Create(busy: true);

        _tray = new NotifyIcon
        {
            Icon = _iconNormal,
            Text = AppTitle,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseDoubleClick += OnTrayDoubleClick;

        // Keep the tray icon visible when the app is started (elevated) at logon before the taskbar exists,
        // and when Explorer restarts. See ShellMessages for why the message filter is needed.
        ShellMessages.AllowFromNonElevatedProcesses();
        _shellWindow = new ShellMessageWindow();
        _shellWindow.TaskbarCreated += (_, _) =>
        {
            Log.Info("Taskbar created: adding the tray icon again");
            ReAddTrayIcon();
        };
        _shellWindow.ShowRequested += (_, _) =>
        {
            Log.Info("The app was started again: showing the running instance");
            ReAddTrayIcon();
            ShowSettings();
        };
        _trayRetryTimer.Tick += OnTrayRetryTick;
        if (!ShellMessages.IsTaskbarReady)
        {
            Log.Info("Taskbar is not ready yet: will retry adding the tray icon");
            _trayRetryTimer.Start();
        }

        _monitor.StatusChanged += (_, _) => _ui.Post(_ =>
        {
            RebuildMenu();
            // Windows publishes the battery level a few seconds after the device connects.
            _ = RefreshBatteryAsync(TimeSpan.FromSeconds(5));
        }, null);

        // Battery: refresh when the menu opens (at most every 30 s) and every 5 minutes for the tooltip.
        _menu.Opening += (_, _) =>
        {
            _ = _monitor.RefreshHidAsync();
            if (DateTime.UtcNow - _batteryRefreshedAt > TimeSpan.FromSeconds(30))
            {
                _ = RefreshBatteryAsync();
            }
        };
        _batteryTimer.Tick += (_, _) => _ = RefreshBatteryAsync();
        _batteryTimer.Start();

        RebuildMenu();
        _ = InitializeStatusAsync();

        if (_settings.Devices.Count == 0)
        {
            Notify(Strings.TraySelectDeviceTitle, Strings.TraySelectDeviceText, ToolTipIcon.Info);
        }
    }

    /// <summary>Removes and adds the icon again (NotifyIcon only re-adds it on Visible false → true).</summary>
    private void ReAddTrayIcon()
    {
        _tray.Visible = false;
        _tray.Visible = true;
    }

    /// <summary>Every 2 s for up to 2 minutes after start: add the icon as soon as the taskbar appears.</summary>
    private void OnTrayRetryTick(object? sender, EventArgs e)
    {
        _trayAttempts++;
        if (ShellMessages.IsTaskbarReady)
        {
            _trayRetryTimer.Stop();
            ReAddTrayIcon();
            Log.Info($"Tray icon added at attempt {_trayAttempts}");
        }
        else if (_trayAttempts >= 60)
        {
            _trayRetryTimer.Stop();
            Log.Info("Taskbar did not appear within 2 minutes; waiting for TaskbarCreated");
        }
    }

    private async Task InitializeStatusAsync()
    {
        await RefreshMonitorAsync();
        await RefreshBatteryAsync();
        await PrivilegedActions.ConfigureServiceAsync(_settings.Devices, _settings.AppleMediaKeysByDefault);
    }

    private async Task RefreshMonitorAsync()
    {
        try
        {
            await _monitor.UpdateAsync(_settings.Devices);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to refresh the status monitor", ex);
        }
    }

    private void RebuildMenu()
    {
        var old = _menu.Items.Cast<ToolStripItem>().ToList();
        _menu.Items.Clear();
        old.ForEach(i => i.Dispose());

        if (_settings.Devices.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem(Strings.TrayNoDevices) { Enabled = false });
        }

        foreach (var device in _settings.Devices)
        {
            bool busy = _busy.Contains(device.Key);
            bool? connected = _monitor.IsConnected(device);

            string text = busy ? Strings.TrayConnecting(device.DisplayName)
                : connected == true ? Strings.TrayReconnect(device.DisplayName)
                : connected == null ? Strings.TrayConnectNotFound(device.DisplayName)
                : Strings.TrayConnect(device.DisplayName);

            if (!busy && connected == true && _battery.TryGetValue(device.Key, out int level))
            {
                text = Strings.TrayItemBattery(text, level);
            }

            var item = new ToolStripMenuItem(text)
            {
                Checked = connected == true,
                Enabled = !busy,
                ToolTipText = connected == true ? Strings.TrayReconnectTooltip : Strings.TrayConnectTooltip,
            };
            if (_settings.Devices.Count == 1 && !busy)
            {
                item.Font = new Font(item.Font, FontStyle.Bold); // double-click action
            }

            item.Click += async (_, _) => await ReconnectAsync(device);
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(_audioRestarting ? Strings.TrayRestartingAudio : Strings.TrayRestartAudio, null,
            async (_, _) => await RestartAudioAsync())
        {
            Enabled = !_audioRestarting,
            ToolTipText = Strings.TrayRestartAudioTooltip,
        });
        _menu.Items.Add(new ToolStripMenuItem(Strings.TraySettings, null, (_, _) => ShowSettings()));

        _menu.Items.Add(new ToolStripMenuItem(Strings.TrayOpenLog, null, (_, _) => OpenLog(Log.FilePath)));
        if (File.Exists(Log.ServiceLogPath))
        {
            _menu.Items.Add(new ToolStripMenuItem(Strings.TrayOpenServiceLog, null, (_, _) => OpenLog(Log.ServiceLogPath)));
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(Strings.TrayExit, null, (_, _) => ExitThread()));

        _tray.Icon = _busy.Count > 0 ? _iconBusy : _iconNormal;
        _tray.Text = BuildTooltip();
    }

    private string BuildTooltip()
    {
        var text = _busy.Count > 0 ? Strings.TrayTooltipBusy(AppTitle) : AppTitle;
        foreach (var device in _settings.Devices)
        {
            if (_battery.TryGetValue(device.Key, out int level))
            {
                text += $"\n{device.DisplayName}: {level}%";
            }
        }

        // NotifyIcon.Text is limited to 127 characters.
        return text.Length <= 127 ? text : text[..126] + "…";
    }

    /// <summary>Re-reads battery levels of connected devices and updates the menu if anything changed.</summary>
    private async Task RefreshBatteryAsync(TimeSpan delay = default)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }

        // All calls run on the UI thread, so a plain flag is enough to avoid overlapping refreshes.
        if (_batteryRefreshing)
        {
            return;
        }

        _batteryRefreshing = true;
        try
        {
            var levels = new Dictionary<string, int>();
            foreach (var device in _settings.Devices.ToList())
            {
                // Only connected devices: Windows keeps a stale value after a disconnect.
                if (_monitor.IsConnected(device) == true && await BatteryReader.TryGetLevelAsync(device) is int level)
                {
                    levels[device.Key] = level;
                }
            }

            _batteryRefreshedAt = DateTime.UtcNow;
            CheckLowBattery(levels);
            bool changed = levels.Count != _battery.Count
                || levels.Any(kv => !_battery.TryGetValue(kv.Key, out int old) || old != kv.Value);
            _battery = levels;
            if (changed)
            {
                RebuildMenu();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to refresh battery levels", ex);
        }
        finally
        {
            _batteryRefreshing = false;
        }
    }

    /// <summary>One warning per device per discharge: re-armed once the level rises 5% above the threshold.</summary>
    private void CheckLowBattery(Dictionary<string, int> levels)
    {
        int threshold = _settings.LowBatteryThreshold;
        foreach (var device in _settings.Devices)
        {
            if (!levels.TryGetValue(device.Key, out int level))
            {
                continue;
            }

            if (threshold > 0 && level <= threshold)
            {
                if (_lowBatteryNotified.Add(device.Key))
                {
                    Log.Info($"Low battery: '{device.DisplayName}' {level}%");
                    Notify(Strings.LowBatteryTitle, Strings.LowBatteryText(device.DisplayName, level), ToolTipIcon.Warning, force: true);
                }
            }
            else if (level > threshold + 5)
            {
                _lowBatteryNotified.Remove(device.Key);
            }
        }
    }

    private async void OnTrayDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (_settings.Devices.Count == 1)
        {
            await ReconnectAsync(_settings.Devices[0]);
        }
        else
        {
            ShowSettings();
        }
    }

    private async Task ReconnectAsync(SavedDevice device)
    {
        if (!_busy.Add(device.Key))
        {
            return;
        }

        RebuildMenu();
        try
        {
            Log.Info($"Reconnecting '{device.DisplayName}' ({BluetoothAddress.Format(device.Address)}, LE={device.IsLowEnergy})");
            var timeout = TimeSpan.FromSeconds(_settings.ConnectTimeoutSeconds);
            var result = await PullThroughServiceAsync(device, timeout)
                         ?? await BluetoothManager.ReconnectAsync(device, timeout);

            Log.Info($"Result: {result.Outcome} — {result.Message}");
            Notify(result.IsSuccess ? Strings.NotifyDone : Strings.NotifyFailed,
                result.Message,
                result.IsSuccess ? ToolTipIcon.Info : ToolTipIcon.Warning,
                force: !result.IsSuccess);
        }
        catch (Exception ex)
        {
            Log.Error("Reconnect failed", ex);
            Notify(Strings.NotifyError, ex.Message, ToolTipIcon.Error, force: true);
        }
        finally
        {
            _busy.Remove(device.Key);
            // Recreate device objects: after a service reinstall the old ones may stop raising events.
            await RefreshMonitorAsync();
            RebuildMenu();
        }
    }

    /// <summary>
    /// A device restored by the service that does not work here: the service brings it over and, if another host
    /// took it, pairs it again without the Windows dialog. Null if this does not apply or the service is not running.
    /// </summary>
    private async Task<ReconnectResult?> PullThroughServiceAsync(SavedDevice device, TimeSpan timeout)
    {
        if (!device.RestoreAtStartup || device.IsLowEnergy || _monitor.IsConnected(device) == true)
        {
            return null;
        }

        // Pairing again takes longer than a plain connection, and the keyboard may need to be woken up.
        var duration = timeout < TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : timeout;
        ServiceResponse? response;
        try
        {
            response = await PrivilegedActions.PullAsync(device, duration, progress: null);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            Log.Error("The service did not complete the connection", ex);
            return null;
        }

        if (response is null)
        {
            return null;
        }

        Log.Info($"Service: pull '{device.DisplayName}' -> ok {response.Ok}, code {response.Code}, {response.Message}");
        if (response.Ok)
        {
            return new ReconnectResult(ReconnectOutcome.Connected, Strings.ResultConnected(device.DisplayName));
        }

        return response.Code == 1168 // not configured in the service yet: fall back to the normal connection
            ? null
            : new ReconnectResult(ReconnectOutcome.NotConnected, Strings.ResultPullFailed(device.DisplayName));
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_settings.Clone());
        _settingsForm.FormClosed += async (_, _) =>
        {
            var result = _settingsForm.Result;
            bool? appleMediaKeys = _settingsForm.AppleMediaKeysByDefault;
            _settingsForm = null;
            if (result is null)
            {
                // Cancelled, but the top row choice was applied right away: keep it.
                if (appleMediaKeys != _settings.AppleMediaKeysByDefault)
                {
                    _settings.AppleMediaKeysByDefault = appleMediaKeys;
                    TrySaveSettings();
                }

                return;
            }

            _settings = result;
            AppLanguage.Apply(_settings.Language);
            TrySaveSettings();

            RebuildMenu();
            await PrivilegedActions.ConfigureServiceAsync(_settings.Devices, _settings.AppleMediaKeysByDefault);
            await RefreshMonitorAsync();
            await RefreshBatteryAsync();
        };
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void TrySaveSettings()
    {
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings", ex);
            Notify(Strings.NotifyError, Strings.NotifySaveFailed(ex.Message), ToolTipIcon.Error, force: true);
        }
    }

    /// <summary>
    /// Restarts the Windows audio services, then reconnects the selected speakers and headsets that are connected:
    /// what helps when sound and the microphone stop working everywhere and otherwise only a reboot would.
    /// </summary>
    private async Task RestartAudioAsync()
    {
        if (_audioRestarting)
        {
            return;
        }

        _audioRestarting = true;
        RebuildMenu();
        try
        {
            Log.Info("Restarting Windows audio");
            int error = await PrivilegedActions.RestartWindowsAudioAsync();
            Log.Info($"Restart Windows audio -> {error}");
            if (error != 0)
            {
                Notify(Strings.NotifyFailed, Strings.AudioRestartFailed(new System.ComponentModel.Win32Exception(error).Message), ToolTipIcon.Warning, force: true);
                return;
            }

            // Bluetooth audio devices reconnect their audio endpoints to the restarted services.
            var audioDevices = _settings.Devices
                .Where(d => !d.IsLowEnergy && _monitor.IsConnected(d) == true && IsAudioDevice(d))
                .ToList();
            Notify(Strings.NotifyDone, audioDevices.Count > 0 ? Strings.AudioRestartedReconnecting : Strings.AudioRestarted, ToolTipIcon.Info);
            foreach (var device in audioDevices)
            {
                await ReconnectAsync(device);
            }
        }
        catch (OperationCanceledException)
        {
            // UAC prompt declined.
        }
        catch (Exception ex)
        {
            Log.Error("Failed to restart Windows audio", ex);
            Notify(Strings.NotifyError, ex.Message, ToolTipIcon.Error, force: true);
        }
        finally
        {
            _audioRestarting = false;
            RebuildMenu();
        }
    }

    private static bool IsAudioDevice(SavedDevice device)
    {
        var installed = NativeBluetooth.TryGetInstalledServices(device.Address);
        return installed is not null
               && (NativeBluetooth.HasMicrophoneProfile(installed) || installed.Contains(NativeBluetooth.A2dpSinkServiceClass));
    }

    private static void OpenLog(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Log.Info("Log created");
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Failed to open the log", ex);
        }
    }

    private void Notify(string title, string text, ToolTipIcon icon, bool force = false)
    {
        if (_settings.ShowNotifications || force)
        {
            _tray.ShowBalloonTip(4000, title, text, icon);
        }
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _settingsForm?.Close();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _batteryTimer.Dispose();
            _trayRetryTimer.Dispose();
            _shellWindow.Dispose();
            _monitor.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _iconNormal.Dispose();
            _iconBusy.Dispose();
        }

        base.Dispose(disposing);
    }
}
