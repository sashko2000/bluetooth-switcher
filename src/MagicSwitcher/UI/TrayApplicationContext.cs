using MagicSwitcher.Bluetooth;
using MagicSwitcher.Infrastructure;
using MagicSwitcher.Keyboard;
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
    private KeyRemapper? _localRemapper;
    private FileSystemWatcher? _topRowWatcher;
    private string? _topRowWatchedPath;
    private bool _topRowFKeys;
    private bool _audioRestarting;
    private readonly MicrophoneWatcher _microphones = new();
    private readonly System.Windows.Forms.Timer _microphoneTimer = new() { Interval = (int)TimeSpan.FromSeconds(30).TotalMilliseconds };
    private bool _microphoneChecking;
    private Action? _balloonAction;
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
        _tray.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            action?.Invoke();
        };
        _tray.BalloonTipClosed += (_, _) => _balloonAction = null;

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
        _microphoneTimer.Tick += async (_, _) => await CheckMicrophonesAsync();
        _microphoneTimer.Start();

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
        await ApplyServiceAndRemappingAsync();
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
        // A speaker or headset without its microphone: the same action, named by what it is needed for.
        bool lostMicrophone = !_audioRestarting && _microphones.Lost.Count > 0;
        var audioItem = new ToolStripMenuItem(
            _audioRestarting ? Strings.TrayRestartingAudio
            : lostMicrophone ? Strings.TrayRestoreMicrophone(LostMicrophoneNames())
            : Strings.TrayRestartAudio,
            null, async (_, _) => await RestartAudioAsync())
        {
            Enabled = !_audioRestarting,
            ToolTipText = Strings.TrayRestartAudioTooltip,
        };
        if (lostMicrophone)
        {
            audioItem.Font = new Font(audioItem.Font, FontStyle.Bold);
        }

        _menu.Items.Add(audioItem);
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
        if (ServiceInstaller.GetState() == ServiceState.Running)
        {
            // Nothing can take a keyboard from a host it is connected to: the user has to help, and soon.
            Notify(Strings.TrayConnecting(device.DisplayName), Strings.PullHint, ToolTipIcon.Info, force: true);
        }

        ServiceResponse? response;
        try
        {
            response = await PrivilegedActions.PullAsync(device, duration, progress: null);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            // Do not fall back to reinstalling services here: the service may be in the middle of pairing the keyboard
            // (or has just paired it), and a reinstall on top of that breaks the new connection.
            Log.Error("The service did not complete the connection", ex);
            await _monitor.RefreshHidAsync();
            return _monitor.IsConnected(device) == true
                ? new ReconnectResult(ReconnectOutcome.Connected, Strings.ResultConnected(device.DisplayName))
                : new ReconnectResult(ReconnectOutcome.NotConnected, Strings.ResultPullFailed(device.DisplayName));
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
                // Cancelled, but the top row choice (and a service installed or removed in the window) applies right away.
                if (appleMediaKeys != _settings.AppleMediaKeysByDefault)
                {
                    _settings.AppleMediaKeysByDefault = appleMediaKeys;
                    TrySaveSettings();
                }

                await ApplyServiceAndRemappingAsync();
                return;
            }

            _settings = result;
            AppLanguage.Apply(_settings.Language);
            TrySaveSettings();

            RebuildMenu();
            await ApplyServiceAndRemappingAsync();
            await RefreshMonitorAsync();
            await RefreshBatteryAsync();
        };
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    /// <summary>
    /// Sends the settings to the service, which also runs the key remapper (as SYSTEM, so it works in administrator
    /// windows too). Without the service the remapper runs here, in the tray app.
    /// </summary>
    private async Task ApplyServiceAndRemappingAsync()
    {
        await PrivilegedActions.ConfigureServiceAsync(_settings.Devices, _settings.AppleMediaKeysByDefault, _settings.AppleKeyMap);

        // Decide by the service state, not by whether this call got through: two remappers at once
        // (the service's helper and one here) would confuse each other.
        bool viaService = ServiceInstaller.GetState() is ServiceState.Running or ServiceState.Starting;

        _localRemapper?.Dispose();
        _localRemapper = null;
        bool driver = AppleKeyboardDriver.IsInstalled;
        if (!viaService && (_settings.AppleKeyMap.Count > 0 || driver))
        {
            if (driver && !AppleTopRow.Exists(AppleTopRow.UserPath))
            {
                // The choice made in versions up to 1.3, where the driver itself was switched.
                AppleTopRow.Write(AppleTopRow.UserPath, fKeys: _settings.AppleMediaKeysByDefault == false);
            }

            _localRemapper = new KeyRemapper(_settings.AppleKeyMap, driver ? AppleTopRow.UserPath : null);
            _localRemapper.Start();
        }

        WatchTopRow();
    }

    /// <summary>Shows a notification when Fn+Esc (or the settings) switches the top row between F-keys and media keys.</summary>
    private void WatchTopRow()
    {
        string path = AppleTopRow.CurrentPath;
        if (_topRowWatcher is not null && string.Equals(_topRowWatchedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _topRowWatcher?.Dispose();
        _topRowWatcher = null;
        _topRowWatchedPath = path;
        _topRowFKeys = AppleTopRow.ReadFKeys(path);
        try
        {
            string directory = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(directory))
            {
                return;
            }

            var watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) => _ui.Post(_ =>
            {
                bool fKeys = AppleTopRow.ReadFKeys(path);
                if (fKeys != _topRowFKeys)
                {
                    _topRowFKeys = fKeys;
                    Notify(Strings.AppleGroup, fKeys ? Strings.TopRowNowFKeys : Strings.TopRowNowMedia, ToolTipIcon.Info, force: true);
                }
            }, null);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.EnableRaisingEvents = true;
            _topRowWatcher = watcher;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to watch the top row state", ex);
        }
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

    private string LostMicrophoneNames() => string.Join(", ", _microphones.Lost.Select(d => d.DisplayName));

    /// <summary>
    /// Every 30 s: a connected speaker or headset that lost its microphone gets a notification (click it, or use the
    /// tray menu, to restore) or, if chosen in settings, is repaired right away.
    /// </summary>
    private async Task CheckMicrophonesAsync()
    {
        if (!_settings.WatchMicrophone)
        {
            _microphones.Clear();
            return;
        }

        if (_microphoneChecking || _audioRestarting || _busy.Count > 0)
        {
            return;
        }

        _microphoneChecking = true;
        try
        {
            int before = _microphones.Lost.Count;
            var found = await _microphones.CheckAsync();
            if (_audioRestarting || _busy.Count > 0)
            {
                _microphones.Clear(); // a reconnect started meanwhile: what was seen is no longer true
                return;
            }

            if (_microphones.Lost.Count != before)
            {
                RebuildMenu();
            }

            if (found.Count == 0)
            {
                return;
            }

            string names = string.Join(", ", found.Select(d => d.DisplayName));
            if (_settings.RestoreMicrophoneAutomatically && PrivilegedActions.CanRestartWindowsAudioSilently)
            {
                Log.Info($"Lost microphone ({names}): restoring automatically");
                Notify(Strings.MicrophoneLostTitle, Strings.MicrophoneLostFixing(names), ToolTipIcon.Info, force: true);
                await RestartAudioAsync();
            }
            else
            {
                Log.Info($"Lost microphone ({names}): notifying");
                Notify(Strings.MicrophoneLostTitle, Strings.MicrophoneLostText(names), ToolTipIcon.Warning, force: true,
                    onClick: () => _ = RestartAudioAsync());
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to check the microphones", ex);
        }
        finally
        {
            _microphoneChecking = false;
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
        var lost = _microphones.Lost.ToList();
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
                .Concat(lost) // also the ones not selected in settings
                .DistinctBy(d => d.Key)
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
            _microphones.Restart();
            _ = RecheckMicrophonesAsync();
            RebuildMenu();
        }
    }

    /// <summary>After a repair: update the menu item without waiting for the next two timer checks.</summary>
    private async Task RecheckMicrophonesAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        await CheckMicrophonesAsync();
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

    /// <param name="onClick">Runs if the user clicks the notification while it is shown.</param>
    private void Notify(string title, string text, ToolTipIcon icon, bool force = false, Action? onClick = null)
    {
        if (_settings.ShowNotifications || force)
        {
            _balloonAction = onClick;
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
            _localRemapper?.Dispose();
            _topRowWatcher?.Dispose();
            _batteryTimer.Dispose();
            _microphoneTimer.Dispose();
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
