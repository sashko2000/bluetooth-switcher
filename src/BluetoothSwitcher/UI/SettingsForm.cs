using BluetoothSwitcher.Bluetooth;
using BluetoothSwitcher.Infrastructure;

namespace BluetoothSwitcher.UI;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;

    private readonly ListView _list;
    private readonly Button _refresh;
    private readonly Button _ok;
    private readonly Button _cancel;
    private readonly NumericUpDown _timeout;
    private readonly CheckBox _notifications;
    private readonly CheckBox _autoStart;
    private readonly Label _status;

    private readonly GroupBox _servicesGroup;
    private readonly CheckBox _autoServices;
    private readonly CheckedListBox _services;
    private readonly Label _servicesNote;

    private bool _loaded;
    private bool _updatingServices;
    private ListViewItem? _servicesItem;
    private int _servicesVersion;

    /// <summary>Новые настройки, если пользователь нажал «Сохранить»; иначе null.</summary>
    public AppSettings? Result { get; private set; }

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;

        Text = "Bluetooth Switcher — настройки";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        MinimumSize = new Size(640, 600);
        Size = new Size(800, 680);
        ShowInTaskbar = true;
        Icon = TrayIconFactory.Create();

        var hint = new Label
        {
            Text = "Отметьте устройства (клавиатуры, колонки, наушники…), которые нужно показывать в меню трея. " +
                   "Клик по пункту подключает устройство, а если оно уже подключено — отключает и подключает заново. " +
                   "Устройство должно быть заранее сопряжено с этим компьютером.",
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Padding = new Padding(0, 0, 0, 6),
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
            MultiSelect = false,
        };
        _list.Columns.Add("Устройство", 220);
        _list.Columns.Add("Адрес", 130);
        _list.Columns.Add("Тип", 200);
        _list.Columns.Add("Состояние", 120);
        _list.SelectedIndexChanged += async (_, _) => await ShowServicesForSelectionAsync();

        _refresh = new Button { Text = "Обновить список", AutoSize = true };
        _refresh.Click += async (_, _) => await LoadDevicesAsync();

        _status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };

        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        listButtons.Controls.Add(_refresh);
        listButtons.Controls.Add(_status);

        // --- Сервисы для переподключения выбранного устройства ---
        _autoServices = new CheckBox
        {
            Text = "Автоматически (клавиатура/мышь — HID, колонка/наушники — Hands-Free)",
            AutoSize = true,
            Dock = DockStyle.Top,
        };
        _autoServices.CheckedChanged += (_, _) => OnServicesEdited();

        _services = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false,
        };
        // ItemCheck приходит до изменения состояния — фиксируем изменения после него.
        _services.ItemCheck += (_, _) => BeginInvoke(OnServicesEdited);

        _servicesNote = new Label
        {
            Text = "Каждый сервис переподключается ≈ 3 с. Чтобы вернуть микрофон колонки, обычно достаточно Hands-Free.",
            AutoSize = true,
            Dock = DockStyle.Bottom,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(0, 4, 0, 0),
        };

        _servicesGroup = new GroupBox
        {
            Text = "Сервисы для переподключения",
            Dock = DockStyle.Fill,
            Height = 170,
            Padding = new Padding(8),
            Enabled = false,
        };
        _servicesGroup.Controls.Add(_services);
        _servicesGroup.Controls.Add(_servicesNote);
        _servicesGroup.Controls.Add(_autoServices);

        // --- Общие параметры ---
        _timeout = new NumericUpDown
        {
            Minimum = 5,
            Maximum = 120,
            Value = Math.Clamp(settings.ConnectTimeoutSeconds, 5, 120),
            Width = 70,
        };
        _notifications = new CheckBox { Text = "Показывать уведомления об успешном подключении", AutoSize = true, Checked = settings.ShowNotifications };
        _autoStart = new CheckBox { Text = "Запускать вместе с Windows", AutoSize = true, Checked = AutoStart.IsEnabled };

        var options = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 8, 0, 0) };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        options.Controls.Add(new Label { Text = "Таймаут подключения, сек:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        options.Controls.Add(_timeout, 1, 0);
        options.Controls.Add(_notifications, 0, 1);
        options.SetColumnSpan(_notifications, 2);
        options.Controls.Add(_autoStart, 0, 2);
        options.SetColumnSpan(_autoStart, 2);

        _ok = new Button { Text = "Сохранить", AutoSize = true, DialogResult = DialogResult.OK };
        _ok.Click += (_, _) => Save();
        _cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = _ok;
        CancelButton = _cancel;

        // Кнопки диалога — отдельной панелью, прижатой к низу окна: они видны при любом размере.
        var dialogButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(12, 6, 12, 12),
        };
        dialogButtons.Controls.Add(_cancel);
        dialogButtons.Controls.Add(_ok);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12, 12, 12, 0) };
        // Колонка строго по ширине окна, иначе длинный текст растягивает разметку за край.
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(hint, 0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(listButtons, 0, 2);
        root.Controls.Add(_servicesGroup, 0, 3);
        root.Controls.Add(options, 0, 4);

        // Перенос длинных подписей по ширине окна.
        void Wrap()
        {
            int width = Math.Max(100, root.ClientSize.Width - root.Padding.Horizontal - hint.Margin.Horizontal);
            hint.MaximumSize = new Size(width, 0);
            _servicesNote.MaximumSize = new Size(Math.Max(100, width - 20), 0);
        }
        root.SizeChanged += (_, _) => Wrap();

        Controls.Add(root);
        Controls.Add(dialogButtons); // добавляется после root, чтобы Dock.Bottom обрабатывался первым
        Wrap();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await LoadDevicesAsync();
    }

    private async Task LoadDevicesAsync()
    {
        // Сохраняем отметки и настройки пользователя между обновлениями списка.
        var checkedKeys = _loaded
            ? _list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => ((SavedDevice)i.Tag!).Key).ToHashSet()
            : _settings.Devices.Select(d => d.Key).ToHashSet();
        var knownSaved = (_loaded
            ? _list.Items.Cast<ListViewItem>().Select(i => (SavedDevice)i.Tag!)
            : _settings.Devices).ToDictionary(d => d.Key);

        _refresh.Enabled = false;
        _status.Text = "Поиск сопряжённых устройств…";
        try
        {
            var paired = await BluetoothManager.GetPairedDevicesAsync();

            _list.BeginUpdate();
            _servicesItem = null;
            _list.Items.Clear();

            foreach (var device in paired)
            {
                var saved = knownSaved.TryGetValue(device.Key, out var existing)
                    ? existing with { Name = device.Name }
                    : device.ToSaved();

                var item = new ListViewItem(device.Name)
                {
                    Tag = saved,
                    Checked = checkedKeys.Contains(device.Key),
                };
                item.SubItems.Add(BluetoothAddress.Format(device.Address));
                item.SubItems.Add(device.Kind + (device.IsLowEnergy ? "" : " (Classic)"));
                item.SubItems.Add(device.IsConnected ? "Подключено" : "Не подключено");
                if (device.IsKeyboard)
                {
                    item.Font = new Font(_list.Font, FontStyle.Bold);
                }

                _list.Items.Add(item);
            }

            // Ранее выбранные устройства, которых сейчас нет среди сопряжённых.
            var pairedKeys = paired.Select(p => p.Key).ToHashSet();
            foreach (var saved in knownSaved.Values.Where(s => !pairedKeys.Contains(s.Key) && checkedKeys.Contains(s.Key)))
            {
                var item = new ListViewItem(saved.DisplayName) { Tag = saved, Checked = true, ForeColor = SystemColors.GrayText };
                item.SubItems.Add(BluetoothAddress.Format(saved.Address));
                item.SubItems.Add(saved.IsLowEnergy ? "BLE" : "Classic");
                item.SubItems.Add("Не найдено");
                _list.Items.Add(item);
            }

            _list.EndUpdate();
            _loaded = true;
            _status.Text = paired.Count == 0
                ? "Сопряжённых устройств не найдено. Выполните сопряжение в параметрах Windows."
                : $"Найдено устройств: {paired.Count}. Выделите устройство, чтобы настроить сервисы.";

            // Сразу выделяем первое отмеченное устройство, чтобы показать его сервисы.
            var first = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Checked) ?? (_list.Items.Count > 0 ? _list.Items[0] : null);
            if (first is not null)
            {
                first.Selected = true;
            }
            else
            {
                await ShowServicesForSelectionAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка получения списка устройств", ex);
            _status.Text = "Ошибка: " + ex.Message;
        }
        finally
        {
            _refresh.Enabled = true;
        }
    }

    private sealed record ServiceItem(Guid Id)
    {
        public override string ToString() =>
            NativeBluetooth.IsKnown(Id) ? NativeBluetooth.Describe(Id) : $"{Id} (служебный)";
    }

    private async Task ShowServicesForSelectionAsync()
    {
        var item = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0] : null;
        if (item is null)
        {
            // Снятие выделения (например, при переходе к другой строке) — оставляем текущую панель.
            if (_list.Items.Count == 0)
            {
                ResetServicesPanel("Сервисы для переподключения", enabled: false);
            }

            return;
        }

        var saved = (SavedDevice)item.Tag!;
        int version = ++_servicesVersion;
        _servicesItem = null;

        if (saved.IsLowEnergy)
        {
            ResetServicesPanel($"Сервисы для переподключения: {saved.DisplayName} — не применяется для BLE", enabled: false);
            return;
        }

        ResetServicesPanel($"Сервисы для переподключения: {saved.DisplayName} — загрузка…", enabled: false);

        Guid[]? installed;
        try
        {
            installed = await Task.Run(() => NativeBluetooth.TryGetInstalledServices(saved.Address));
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка получения списка сервисов", ex);
            installed = null;
        }

        if (version != _servicesVersion)
        {
            return; // пользователь уже выделил другое устройство
        }

        if (installed is null)
        {
            ResetServicesPanel($"Сервисы для переподключения: {saved.DisplayName} — устройство не найдено", enabled: false);
            return;
        }

        // Сохранённый вручную сервис мог остаться выключенным (например, после сбоя) — показываем и его.
        var all = installed.Concat(saved.Services ?? []).Distinct()
            .OrderByDescending(NativeBluetooth.IsKnown)
            .ToList();
        var defaults = NativeBluetooth.DefaultServices(installed).ToHashSet();
        bool auto = saved.Services is null;

        _updatingServices = true;
        try
        {
            _servicesGroup.Text = $"Сервисы для переподключения: {saved.DisplayName}";
            _services.Items.Clear();
            foreach (var id in all)
            {
                bool isChecked = auto ? defaults.Contains(id) : saved.Services!.Contains(id);
                _services.Items.Add(new ServiceItem(id), isChecked);
            }

            _autoServices.Checked = auto;
            _services.Enabled = !auto;
            _servicesGroup.Enabled = true;
            _servicesItem = item;
        }
        finally
        {
            _updatingServices = false;
        }
    }

    private void ResetServicesPanel(string title, bool enabled)
    {
        _updatingServices = true;
        try
        {
            _servicesGroup.Text = title;
            _services.Items.Clear();
            _autoServices.Checked = true;
            _servicesGroup.Enabled = enabled;
        }
        finally
        {
            _updatingServices = false;
        }
    }

    private void OnServicesEdited()
    {
        if (_updatingServices || _servicesItem is null || _servicesItem.ListView is null)
        {
            return;
        }

        var saved = (SavedDevice)_servicesItem.Tag!;

        if (_autoServices.Checked)
        {
            // Показываем, что будет выбрано автоматически.
            _updatingServices = true;
            try
            {
                var installed = _services.Items.Cast<ServiceItem>().Select(s => s.Id).ToList();
                var defaults = NativeBluetooth.DefaultServices(installed).ToHashSet();
                for (int i = 0; i < _services.Items.Count; i++)
                {
                    _services.SetItemChecked(i, defaults.Contains(((ServiceItem)_services.Items[i]).Id));
                }
            }
            finally
            {
                _updatingServices = false;
            }

            _services.Enabled = false;
            _servicesItem.Tag = saved with { Services = null };
        }
        else
        {
            _services.Enabled = true;
            var selected = _services.CheckedItems.Cast<ServiceItem>().Select(s => s.Id).ToList();
            // Пустой выбор равнозначен «автоматически», иначе переподключать было бы нечего.
            _servicesItem.Tag = saved with { Services = selected.Count > 0 ? selected : null };
        }
    }

    private void Save()
    {
        var devices = _loaded
            ? _list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (SavedDevice)i.Tag!).ToList()
            : _settings.Devices;

        Result = new AppSettings
        {
            Devices = devices,
            ConnectTimeoutSeconds = (int)_timeout.Value,
            ShowNotifications = _notifications.Checked,
        };

        try
        {
            if (_autoStart.Checked != AutoStart.IsEnabled)
            {
                AutoStart.Set(_autoStart.Checked);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось изменить автозапуск", ex);
            MessageBox.Show(this, "Не удалось изменить автозапуск: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Close();
    }
}
