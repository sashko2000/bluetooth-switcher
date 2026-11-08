using BluetoothSwitcher.Bluetooth;
using BluetoothSwitcher.Infrastructure;

namespace BluetoothSwitcher.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string AppTitle = "Bluetooth Switcher";

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _iconNormal;
    private readonly Icon _iconBusy;
    private readonly SynchronizationContext _ui;
    private readonly DeviceStatusMonitor _monitor = new();
    private readonly HashSet<string> _busy = [];

    private AppSettings _settings;
    private SettingsForm? _settingsForm;

    public TrayApplicationContext()
    {
        _settings = SettingsStore.Load();

        _menu = new ContextMenuStrip();
        // ContextMenuStrip — это Control, после его создания установлен WindowsFormsSynchronizationContext.
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

        _monitor.StatusChanged += (_, _) => _ui.Post(_ => RebuildMenu(), null);

        RebuildMenu();
        _ = RefreshMonitorAsync();

        if (_settings.Devices.Count == 0)
        {
            Notify("Выберите клавиатуру", "Откройте «Настройки» в меню значка в трее и отметьте устройство.", ToolTipIcon.Info);
        }
    }

    private async Task RefreshMonitorAsync()
    {
        try
        {
            await _monitor.UpdateAsync(_settings.Devices);
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка обновления монитора состояния", ex);
        }
    }

    private void RebuildMenu()
    {
        var old = _menu.Items.Cast<ToolStripItem>().ToList();
        _menu.Items.Clear();
        old.ForEach(i => i.Dispose());

        if (_settings.Devices.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("Нет выбранных устройств") { Enabled = false });
        }

        foreach (var device in _settings.Devices)
        {
            bool busy = _busy.Contains(device.Key);
            bool? connected = _monitor.IsConnected(device);

            string text = busy ? $"Подключение «{device.DisplayName}»…"
                : connected == true ? $"Переподключить «{device.DisplayName}»"
                : connected == null ? $"Подключить «{device.DisplayName}» (не найдено)"
                : $"Подключить «{device.DisplayName}»";

            var item = new ToolStripMenuItem(text)
            {
                Checked = connected == true,
                Enabled = !busy,
                ToolTipText = connected == true
                    ? "Устройство подключено к этому компьютеру. Нажмите, чтобы отключить и подключить заново."
                    : "Подключить устройство к этому компьютеру",
            };
            if (_settings.Devices.Count == 1 && !busy)
            {
                item.Font = new Font(item.Font, FontStyle.Bold); // действие по двойному клику
            }

            item.Click += async (_, _) => await ReconnectAsync(device);
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Настройки…", null, (_, _) => ShowSettings()));

        if (!Elevation.IsElevated)
        {
            _menu.Items.Add(new ToolStripMenuItem("Перезапустить от имени администратора", null, (_, _) => RestartElevated()));
        }

        _menu.Items.Add(new ToolStripMenuItem("Открыть лог", null, (_, _) => OpenLog()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => ExitThread()));

        _tray.Icon = _busy.Count > 0 ? _iconBusy : _iconNormal;
        _tray.Text = _busy.Count > 0 ? $"{AppTitle} — подключение…" : AppTitle;
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
            Log.Info($"Переподключение «{device.DisplayName}» ({BluetoothAddress.Format(device.Address)}, LE={device.IsLowEnergy})");
            var timeout = TimeSpan.FromSeconds(_settings.ConnectTimeoutSeconds);
            var result = await BluetoothManager.ReconnectAsync(device, timeout);

            Log.Info($"Результат: {result.Outcome} — {result.Message}");
            Notify(result.IsSuccess ? "Готово" : "Не удалось подключить",
                result.Message,
                result.IsSuccess ? ToolTipIcon.Info : ToolTipIcon.Warning,
                force: !result.IsSuccess);
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка переподключения", ex);
            Notify("Ошибка", ex.Message, ToolTipIcon.Error, force: true);
        }
        finally
        {
            _busy.Remove(device.Key);
            // Объекты устройств пересоздаём: после переустановки сервиса старые могут перестать слать события.
            await RefreshMonitorAsync();
            RebuildMenu();
        }
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
            _settingsForm = null;
            if (result is null)
            {
                return;
            }

            _settings = result;
            try
            {
                SettingsStore.Save(_settings);
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось сохранить настройки", ex);
                Notify("Ошибка", "Не удалось сохранить настройки: " + ex.Message, ToolTipIcon.Error, force: true);
            }

            RebuildMenu();
            await RefreshMonitorAsync();
        };
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void RestartElevated()
    {
        if (Elevation.TryRestartElevated())
        {
            ExitThread();
        }
    }

    private static void OpenLog()
    {
        try
        {
            if (!File.Exists(Log.FilePath))
            {
                Log.Info("Лог создан");
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось открыть лог", ex);
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
            _monitor.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _iconNormal.Dispose();
            _iconBusy.Dispose();
        }

        base.Dispose(disposing);
    }
}
