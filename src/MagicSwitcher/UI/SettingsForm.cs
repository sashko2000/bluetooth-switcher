using MagicSwitcher.Bluetooth;
using MagicSwitcher.Infrastructure;
using MagicSwitcher.Keyboard;
using MagicSwitcher.Localization;
using MagicSwitcher.Service;

namespace MagicSwitcher.UI;

[System.ComponentModel.DesignerCategory("Code")] // built in code: keeps Visual Studio from opening the designer and creating a .resx
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;

    private readonly ListView _list;
    private readonly Button _refresh;
    private readonly Button _ok;
    private readonly Button _cancel;
    private readonly NumericUpDown _timeout;
    private readonly NumericUpDown _lowBattery;
    private readonly CheckBox _notifications;
    private readonly CheckBox _watchMicrophone;
    private readonly CheckBox _restoreMicrophone;
    private readonly CheckBox _autoStart;
    private readonly ComboBox _language;
    private readonly Label _status;

    private readonly GroupBox _servicesGroup;
    private readonly ComboBox _keyboard;
    private readonly Label _keyboardHint;
    private readonly CheckBox _restore;
    private readonly Label _restoreNote;
    private readonly Label _experimentalNote;
    private bool _keyboardLoaded;
    private readonly CheckBox _autoServices;
    private readonly CheckedListBox _services;
    private readonly Label _servicesNote;

    private readonly Label _appleInfo;
    private readonly Label _appleStatus;
    private readonly Button _appleInstall;
    private readonly ComboBox _appleFnMode;
    private readonly Label _appleFnNote;
    private readonly Button _appleRemap;
    private readonly Label _appleRemapSummary;
    private List<MagicSwitcher.Keyboard.KeyMapping> _keyMap;
    private readonly GroupBox _appleGroup;
    private bool _updatingAppleFn;

    private readonly Label _serviceInfo;
    private readonly Label _serviceStatus;
    private readonly Button _serviceInstall;
    private readonly Button _serviceRemove;

    private bool _loaded;
    private bool _updatingServices;
    private ListViewItem? _servicesItem;
    private int _servicesVersion;

    /// <summary>
    /// Apple keyboard top row chosen in this window. It is applied right away (not on Save), so the tray keeps it
    /// even if the window is cancelled.
    /// </summary>
    public bool? AppleMediaKeysByDefault { get; private set; }

    /// <summary>New settings if the user clicked "Save"; otherwise null.</summary>
    public AppSettings? Result { get; private set; }

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        AppleMediaKeysByDefault = settings.AppleMediaKeysByDefault;
        _keyMap = settings.AppleKeyMap.ToList();

        Text = Strings.SettingsTitle;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        MinimumSize = new Size(640, 600);
        Size = new Size(800, 760);
        ShowInTaskbar = true;
        Icon = TrayIconFactory.Create();

        var hint = new Label
        {
            Text = Strings.SettingsHint,
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
        _list.Columns.Add(Strings.ColumnDevice, 220);
        _list.Columns.Add(Strings.ColumnAddress, 130);
        _list.Columns.Add(Strings.ColumnType, 200);
        _list.Columns.Add(Strings.ColumnStatus, 120);
        _list.SelectedIndexChanged += async (_, _) => await ShowServicesForSelectionAsync();

        _refresh = new Button { Text = Strings.SettingsRefresh, AutoSize = true };
        _refresh.Click += async (_, _) => await LoadDevicesAsync();

        _status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };

        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        listButtons.Controls.Add(_refresh);
        listButtons.Controls.Add(_status);

        // --- Keyboard tab: which keyboard, and whether the service restores it at startup and sign-in ---
        _keyboard = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
        _keyboard.Items.Add(new KeyboardItem(null, Strings.KeyboardNone));
        _keyboard.SelectedIndex = 0;
        _keyboard.SelectionChangeCommitted += (_, _) => OnKeyboardChosen();
        _keyboardHint = new Label { Text = Strings.KeyboardHint, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 8) };
        _restore = new CheckBox { Text = Strings.RestoreAtStartup, AutoSize = true, Enabled = false };
        _restoreNote = new Label
        {
            Text = Strings.RestoreAtStartupNote,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(20, 0, 3, 8),
        };

        // --- Experimental tab: services to reconnect for the selected device ---
        _autoServices = new CheckBox
        {
            Text = Strings.ServicesAuto,
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
        // ItemCheck fires before the state changes, so apply the edit afterwards.
        _services.ItemCheck += (_, _) => BeginInvoke(OnServicesEdited);

        _servicesNote = new Label
        {
            Text = Strings.ServicesNote,
            AutoSize = true,
            Dock = DockStyle.Bottom,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(0, 4, 0, 0),
        };

        _servicesGroup = new GroupBox
        {
            Text = Strings.ServicesGroup,
            Dock = DockStyle.Fill,
            Height = 190,
            Padding = new Padding(8),
            Enabled = false,
        };
        _servicesGroup.Controls.Add(_services);
        _servicesGroup.Controls.Add(_servicesNote);
        _servicesGroup.Controls.Add(_autoServices);

        // --- General options ---
        _timeout = new NumericUpDown
        {
            Minimum = 5,
            Maximum = 120,
            Value = Math.Clamp(settings.ConnectTimeoutSeconds, 5, 120),
            Width = 70,
        };
        _lowBattery = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 50,
            Value = Math.Clamp(settings.LowBatteryThreshold, 0, 50),
            Width = 70,
        };
        _notifications = new CheckBox { Text = Strings.SettingsNotifications, AutoSize = true, Checked = settings.ShowNotifications };
        _autoStart = new CheckBox { Text = Strings.SettingsAutoStart, AutoSize = true, Checked = AutoStart.IsEnabled };
        _watchMicrophone = new CheckBox { Text = Strings.SettingsWatchMicrophone, AutoSize = true, Checked = settings.WatchMicrophone };
        _restoreMicrophone = new CheckBox
        {
            Text = Strings.SettingsRestoreMicrophone,
            AutoSize = true,
            Checked = settings.RestoreMicrophoneAutomatically,
            Enabled = settings.WatchMicrophone,
            Margin = new Padding(22, 3, 3, 0),
        };
        _watchMicrophone.CheckedChanged += (_, _) => _restoreMicrophone.Enabled = _watchMicrophone.Checked;
        var restoreMicrophoneNote = new Label
        {
            Text = Strings.SettingsRestoreMicrophoneNote,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(40, 0, 3, 6),
        };

        var options = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        _language.Items.Add(new LanguageItem("", Strings.SettingsLanguageSystem));
        foreach (var (code, nativeName) in AppLanguage.Supported)
        {
            _language.Items.Add(new LanguageItem(code, nativeName));
        }
        _language.SelectedItem = _language.Items.Cast<LanguageItem>()
            .FirstOrDefault(l => string.Equals(l.Code, settings.Language, StringComparison.OrdinalIgnoreCase))
            ?? _language.Items[0];

        options.Controls.Add(new Label { Text = Strings.SettingsLanguage, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        options.Controls.Add(_language, 1, 0);
        options.Controls.Add(new Label { Text = Strings.SettingsLowBattery, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        options.Controls.Add(_lowBattery, 1, 1);
        options.Controls.Add(_notifications, 0, 2);
        options.SetColumnSpan(_notifications, 2);
        options.Controls.Add(_autoStart, 0, 3);
        options.SetColumnSpan(_autoStart, 2);
        options.Controls.Add(_watchMicrophone, 0, 4);
        options.SetColumnSpan(_watchMicrophone, 2);
        options.Controls.Add(_restoreMicrophone, 0, 5);
        options.SetColumnSpan(_restoreMicrophone, 2);
        options.Controls.Add(restoreMicrophoneNote, 0, 6);
        options.SetColumnSpan(restoreMicrophoneNote, 2);

        // The form is shown modelessly (Show, not ShowDialog), so a button's DialogResult does not close it:
        // both buttons close the form explicitly.
        _ok = new Button { Text = Strings.SettingsSave, AutoSize = true };
        _ok.Click += (_, _) => Save();
        _cancel = new Button { Text = Strings.SettingsCancel, AutoSize = true };
        _cancel.Click += (_, _) => Close();
        AcceptButton = _ok;
        CancelButton = _cancel;

        // Dialog buttons live in a separate panel docked to the bottom, so they stay visible at any size.
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

        // --- Apple Magic Keyboard driver (Boot Camp) ---
        _appleInfo = new Label { Text = Strings.AppleDriverInfo, AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
        _appleInstall = new Button { AutoSize = true };
        _appleInstall.Click += async (_, _) => await InstallAppleDriverAsync();
        _appleStatus = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
        _appleFnMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        _appleFnMode.Items.Add(Strings.AppleFnStandard);
        _appleFnMode.Items.Add(Strings.AppleFnMedia);
        _appleFnMode.SelectionChangeCommitted += async (_, _) => await ApplyAppleFnModeAsync();

        var appleLayout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        appleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        appleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        appleLayout.Controls.Add(_appleInfo, 0, 0);
        appleLayout.SetColumnSpan(_appleInfo, 2);
        appleLayout.Controls.Add(_appleInstall, 0, 1);
        appleLayout.Controls.Add(_appleStatus, 1, 1);
        appleLayout.Controls.Add(new Label { Text = Strings.AppleFnLabel, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        appleLayout.Controls.Add(_appleFnMode, 1, 2);
        _appleFnNote = new Label { Text = Strings.AppleFnSignInNote, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 3, 3, 0) };
        appleLayout.Controls.Add(_appleFnNote, 0, 3);
        appleLayout.SetColumnSpan(_appleFnNote, 2);
        _appleRemap = new Button { Text = Strings.RemapButton, AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        _appleRemap.Click += (_, _) => EditKeyMap();
        _appleRemapSummary = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3) };
        appleLayout.Controls.Add(_appleRemap, 0, 4);
        appleLayout.Controls.Add(_appleRemapSummary, 1, 4);
        UpdateRemapSummary();

        _appleGroup = new GroupBox
        {
            Text = Strings.AppleGroup,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
        };
        _appleGroup.Controls.Add(appleLayout);
        RefreshAppleDriverState();

        // --- Background service ---
        _serviceInfo = new Label { Text = Strings.ServiceInfo, AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
        _serviceInstall = new Button { Text = Strings.ServiceInstall, AutoSize = true };
        _serviceInstall.Click += async (_, _) => await InstallServiceAsync();
        _serviceRemove = new Button { Text = Strings.ServiceRemove, AutoSize = true };
        _serviceRemove.Click += async (_, _) => await RemoveServiceAsync();
        _serviceStatus = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };

        var serviceButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        serviceButtons.Controls.Add(_serviceInstall);
        serviceButtons.Controls.Add(_serviceRemove);

        var serviceLayout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        serviceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        serviceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        serviceLayout.Controls.Add(_serviceInfo, 0, 0);
        serviceLayout.SetColumnSpan(_serviceInfo, 2);
        serviceLayout.Controls.Add(serviceButtons, 0, 1);
        serviceLayout.Controls.Add(_serviceStatus, 1, 1);

        var serviceGroup = new GroupBox
        {
            Text = Strings.ServiceGroup,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
        };
        serviceGroup.Controls.Add(serviceLayout);

        // --- Tabs: the keyboard first; reconnecting other devices is a separate, experimental tab ---
        var keyboardRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 2) };
        keyboardRow.Controls.Add(new Label { Text = Strings.KeyboardLabel, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) });
        keyboardRow.Controls.Add(_keyboard);

        var keyboardLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(8) };
        keyboardLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        keyboardLayout.Controls.Add(keyboardRow, 0, 0);
        keyboardLayout.Controls.Add(_keyboardHint, 0, 1);
        keyboardLayout.Controls.Add(_restore, 0, 2);
        keyboardLayout.Controls.Add(_restoreNote, 0, 3);
        keyboardLayout.Controls.Add(_appleGroup, 0, 4);
        keyboardLayout.Controls.Add(serviceGroup, 0, 5);
        var keyboardPage = new TabPage(Strings.TabKeyboard) { AutoScroll = true, UseVisualStyleBackColor = true };
        keyboardPage.Controls.Add(keyboardLayout);

        var generalPage = new TabPage(Strings.TabGeneral) { AutoScroll = true, UseVisualStyleBackColor = true };
        generalPage.Controls.Add(options);

        _experimentalNote = new Label { Text = Strings.ExperimentalNote, AutoSize = true, Padding = new Padding(0, 0, 0, 6) };
        var timeoutRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        timeoutRow.Controls.Add(new Label { Text = Strings.SettingsTimeout, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) });
        timeoutRow.Controls.Add(_timeout);

        var devicesLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
        // The column is pinned to the page width, otherwise long text stretches the layout past the edge.
        devicesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        devicesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        devicesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        devicesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        devicesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        devicesLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        devicesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        devicesLayout.Controls.Add(_experimentalNote, 0, 0);
        devicesLayout.Controls.Add(hint, 0, 1);
        devicesLayout.Controls.Add(_list, 0, 2);
        devicesLayout.Controls.Add(listButtons, 0, 3);
        devicesLayout.Controls.Add(_servicesGroup, 0, 4);
        devicesLayout.Controls.Add(timeoutRow, 0, 5);
        var devicesPage = new TabPage(Strings.TabExperimental) { UseVisualStyleBackColor = true };
        devicesPage.Controls.Add(devicesLayout);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(keyboardPage);
        tabs.TabPages.Add(generalPage);
        tabs.TabPages.Add(devicesPage);
        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0) };
        root.Controls.Add(tabs);

        // Wrap long labels to the page width.
        void Wrap()
        {
            int width = Math.Max(100, tabs.DisplayRectangle.Width - 40);
            hint.MaximumSize = new Size(width, 0);
            _experimentalNote.MaximumSize = new Size(width, 0);
            _keyboardHint.MaximumSize = new Size(width, 0);
            _restoreNote.MaximumSize = new Size(Math.Max(100, width - 20), 0);
            _servicesNote.MaximumSize = new Size(Math.Max(100, width - 20), 0);
            _appleInfo.MaximumSize = new Size(Math.Max(100, width - 30), 0);
            _appleFnNote.MaximumSize = new Size(Math.Max(100, width - 30), 0);
            _appleRemapSummary.MaximumSize = new Size(Math.Max(100, width - 230), 0);
            _appleStatus.MaximumSize = new Size(Math.Max(100, width - 270), 0);
            _serviceInfo.MaximumSize = new Size(Math.Max(100, width - 30), 0);
            _serviceStatus.MaximumSize = new Size(Math.Max(100, width - 330), 0);
        }
        tabs.SizeChanged += (_, _) => Wrap();

        Controls.Add(root);
        Controls.Add(dialogButtons); // added after root so that Dock.Bottom is laid out first
        Wrap();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await RefreshServiceStateAsync();
        await LoadDevicesAsync();
    }

    private async Task LoadDevicesAsync()
    {
        // Keep the user's checks and settings across list refreshes.
        var checkedKeys = _loaded
            ? _list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => ((SavedDevice)i.Tag!).Key).ToHashSet()
            : _settings.Devices.Select(d => d.Key).ToHashSet();
        var knownSaved = (_loaded
            ? _list.Items.Cast<ListViewItem>().Select(i => (SavedDevice)i.Tag!)
            : _settings.Devices).ToDictionary(d => d.Key);

        _refresh.Enabled = false;
        _status.Text = Strings.SettingsSearching;
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
                item.SubItems.Add(device.IsConnected ? Strings.StatusConnected : Strings.StatusNotConnected);
                if (device.IsKeyboard)
                {
                    item.Font = new Font(_list.Font, FontStyle.Bold);
                }

                _list.Items.Add(item);
            }

            // Previously selected devices that are no longer paired.
            var pairedKeys = paired.Select(p => p.Key).ToHashSet();
            foreach (var saved in knownSaved.Values.Where(s => !pairedKeys.Contains(s.Key) && checkedKeys.Contains(s.Key)))
            {
                var item = new ListViewItem(saved.DisplayName) { Tag = saved, Checked = true, ForeColor = SystemColors.GrayText };
                item.SubItems.Add(BluetoothAddress.Format(saved.Address));
                item.SubItems.Add(saved.IsLowEnergy ? "BLE" : "Classic");
                item.SubItems.Add(Strings.StatusNotFound);
                _list.Items.Add(item);
            }

            _list.EndUpdate();
            FillKeyboards(paired, knownSaved, checkedKeys);
            _loaded = true;
            _appleGroup.Visible = AppleKeyboardDriver.IsInstalled || AppleKeyboardDriver.IsSupportedKeyboardPresent();
            _status.Text = paired.Count == 0
                ? Strings.SettingsNoneFound
                : Strings.SettingsFoundCount(paired.Count);

            // Select the first checked device right away to show its services.
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
            Log.Error("Failed to list devices", ex);
            _status.Text = Strings.SettingsError(ex.Message);
        }
        finally
        {
            _refresh.Enabled = true;
        }
    }

    /// <summary>
    /// The keyboard chooser on the first tab: paired Classic keyboards. The chosen one is always in the tray menu,
    /// and "Restore at startup and sign-in" applies to it.
    /// </summary>
    private void FillKeyboards(IReadOnlyList<PairedDeviceInfo> paired, Dictionary<string, SavedDevice> knownSaved, HashSet<string> checkedKeys)
    {
        var keyboards = paired.Where(p => p.IsKeyboard && !p.IsLowEnergy).ToList();
        string? wantedKey;
        bool restore = _restore.Checked;
        if (_keyboardLoaded)
        {
            wantedKey = (_keyboard.SelectedItem as KeyboardItem)?.Device?.Key;
        }
        else
        {
            // First load: the keyboard restored by the service, else a keyboard that is in the tray menu already.
            var restored = _settings.Devices.FirstOrDefault(d => d.RestoreAtStartup && !d.IsLowEnergy);
            wantedKey = restored?.Key ?? keyboards.FirstOrDefault(k => checkedKeys.Contains(k.Key))?.Key;
            restore = restored is not null;
        }

        _keyboard.BeginUpdate();
        _keyboard.Items.Clear();
        _keyboard.Items.Add(new KeyboardItem(null, Strings.KeyboardNone));
        foreach (var keyboard in keyboards)
        {
            var saved = knownSaved.TryGetValue(keyboard.Key, out var existing) ? existing with { Name = keyboard.Name } : keyboard.ToSaved();
            _keyboard.Items.Add(new KeyboardItem(saved, keyboard.Name));
        }

        if (wantedKey is not null && keyboards.All(k => k.Key != wantedKey) && knownSaved.TryGetValue(wantedKey, out var missing))
        {
            _keyboard.Items.Add(new KeyboardItem(missing, $"{missing.DisplayName} — {Strings.StatusNotFound}"));
        }

        _keyboard.SelectedItem = _keyboard.Items.Cast<KeyboardItem>().FirstOrDefault(i => i.Device?.Key == wantedKey && wantedKey is not null)
                                 ?? _keyboard.Items[0];
        _keyboard.EndUpdate();
        _keyboardLoaded = true;

        bool chosen = (_keyboard.SelectedItem as KeyboardItem)?.Device is not null;
        _restore.Enabled = chosen;
        _restore.Checked = chosen && restore;
        CheckChosenKeyboardInList();
    }

    private void OnKeyboardChosen()
    {
        bool chosen = (_keyboard.SelectedItem as KeyboardItem)?.Device is not null;
        _restore.Enabled = chosen;
        _restore.Checked = chosen; // restoring is the point of choosing a keyboard; it can be unchecked
        CheckChosenKeyboardInList();
    }

    /// <summary>The chosen keyboard is always shown in the tray menu.</summary>
    private void CheckChosenKeyboardInList()
    {
        if ((_keyboard.SelectedItem as KeyboardItem)?.Device is not { } keyboard)
        {
            return;
        }

        foreach (ListViewItem item in _list.Items)
        {
            if (((SavedDevice)item.Tag!).Key == keyboard.Key)
            {
                item.Checked = true;
            }
        }
    }

    private sealed record KeyboardItem(SavedDevice? Device, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record LanguageItem(string Code, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record ServiceItem(Guid Id)
    {
        public override string ToString()
        {
            if (NativeBluetooth.IsInformational(Id))
            {
                return $"{NativeBluetooth.Describe(Id)} ({Strings.ServiceHintDeviceInfo})";
            }

            if (!NativeBluetooth.IsKnown(Id))
            {
                return Strings.ServiceVendor(Id);
            }

            string name = NativeBluetooth.Describe(Id);
            string? hint = Id == NativeBluetooth.HidServiceClass ? Strings.ServiceHintHid
                : Id == NativeBluetooth.A2dpSinkServiceClass ? Strings.ServiceHintAudio
                : Id == NativeBluetooth.HandsFreeServiceClass || Id == NativeBluetooth.HeadsetServiceClass ? Strings.ServiceHintMicrophone
                : Id == NativeBluetooth.AvrcpServiceClass ? Strings.ServiceHintControls
                : null;
            return hint is null ? name : $"{name} ({hint})";
        }
    }

    private async Task ShowServicesForSelectionAsync()
    {
        var item = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0] : null;
        if (item is null)
        {
            // Selection cleared (e.g. while moving to another row): keep the current panel.
            if (_list.Items.Count == 0)
            {
                ResetServicesPanel(Strings.ServicesGroup, enabled: false);
            }

            return;
        }

        var saved = (SavedDevice)item.Tag!;
        int version = ++_servicesVersion;
        _servicesItem = null;

        if (saved.IsLowEnergy)
        {
            ResetServicesPanel(Strings.ServicesGroupBle(saved.DisplayName), enabled: false);
            return;
        }

        ResetServicesPanel(Strings.ServicesGroupLoading(saved.DisplayName), enabled: false);

        Guid[]? installed;
        try
        {
            installed = await Task.Run(() => NativeBluetooth.TryGetInstalledServices(saved.Address));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to list services", ex);
            installed = null;
        }

        if (version != _servicesVersion)
        {
            return; // the user has already selected another device
        }

        if (installed is null)
        {
            ResetServicesPanel(Strings.ServicesGroupNotFound(saved.DisplayName), enabled: false);
            return;
        }

        // A manually saved service may have been left disabled (e.g. after a failure), so show it too.
        var all = installed.Concat(saved.Services ?? []).Distinct()
            .OrderByDescending(NativeBluetooth.IsKnown)
            .ToList();
        var defaults = NativeBluetooth.DefaultServices(installed).ToHashSet();
        bool auto = saved.Services is null;

        _updatingServices = true;
        try
        {
            _servicesGroup.Text = Strings.ServicesGroupFor(saved.DisplayName);
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
            // Show what will be selected automatically.
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
            // An empty selection means "automatic", otherwise there would be nothing to reconnect.
            _servicesItem.Tag = saved with { Services = selected.Count > 0 ? selected : null };
        }
    }

    private async Task RefreshServiceStateAsync(string? status = null)
    {
        var state = await Task.Run(ServiceInstaller.GetState);
        string? version = state == ServiceState.Running ? await PrivilegedActions.GetServiceVersionAsync() : null;
        if (IsDisposed)
        {
            return;
        }

        bool outdated = version is not null && version != AppInfo.Version;
        _serviceInstall.Text = state == ServiceState.NotInstalled ? Strings.ServiceInstall
            : outdated ? Strings.ServiceUpdate
            : Strings.ServiceReinstall;
        _serviceRemove.Enabled = state != ServiceState.NotInstalled;
        _serviceInstall.Enabled = true;
        _serviceStatus.Text = status ?? state switch
        {
            ServiceState.NotInstalled => Strings.ServiceNotInstalled,
            ServiceState.Running when outdated => Strings.ServiceRunningOutdated(version),
            ServiceState.Running => Strings.ServiceRunning(version ?? "?"),
            ServiceState.Starting => Strings.ServiceStarting,
            _ => Strings.ServiceStopped,
        };
    }

    private async Task InstallServiceAsync()
    {
        _serviceInstall.Enabled = false;
        _serviceRemove.Enabled = false;
        _serviceStatus.Text = Strings.ServiceInstalling;
        string? status = null;
        try
        {
            await ServiceInstaller.InstallAsync();
            // Give the service a moment to open its pipe, then tell it which devices to restore.
            for (int i = 0; i < 20 && await PrivilegedActions.GetServiceVersionAsync() is null; i++)
            {
                await Task.Delay(500);
            }

            await PrivilegedActions.ConfigureServiceAsync(CurrentDevices(), AppleMediaKeysByDefault, CurrentKeyMap());
        }
        catch (OperationCanceledException)
        {
            status = Strings.OperationCancelled;
        }
        catch (Exception ex)
        {
            Log.Error("Service installation failed", ex);
            status = Strings.ServiceFailed(ex.Message);
        }

        if (!IsDisposed)
        {
            await RefreshServiceStateAsync(status);
        }
    }

    private async Task RemoveServiceAsync()
    {
        _serviceInstall.Enabled = false;
        _serviceRemove.Enabled = false;
        string? status = null;
        try
        {
            await ServiceInstaller.UninstallAsync();
        }
        catch (OperationCanceledException)
        {
            status = Strings.OperationCancelled;
        }
        catch (Exception ex)
        {
            Log.Error("Service removal failed", ex);
            status = Strings.ServiceFailed(ex.Message);
        }

        if (!IsDisposed)
        {
            await RefreshServiceStateAsync(status);
        }
    }

    private List<MagicSwitcher.Keyboard.KeyMapping> CurrentKeyMap() => _keyMap;

    private void EditKeyMap()
    {
        using var dialog = new KeyRemapForm(_keyMap);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _keyMap = dialog.Result;
            UpdateRemapSummary();
        }
    }

    private void UpdateRemapSummary() => _appleRemapSummary.Text = _keyMap.Count == 0
        ? Strings.RemapNone
        : string.Join(", ", _keyMap.Select(m => $"{KeyRemapForm.DescribeKey(m.From)} → {KeyRemapForm.DescribeTarget(m.To)}"));

    /// <summary>
    /// Devices for the tray menu: the checked ones on the experimental tab plus the keyboard chosen on the first tab,
    /// which is the only one the service restores.
    /// </summary>
    private List<SavedDevice> CurrentDevices()
    {
        if (!_loaded)
        {
            return _settings.Devices;
        }

        var keyboard = (_keyboard.SelectedItem as KeyboardItem)?.Device;
        var all = _list.Items.Cast<ListViewItem>().Select(i => (Item: i, Device: (SavedDevice)i.Tag!)).ToList();
        var devices = all
            .Where(x => x.Item.Checked || x.Device.Key == keyboard?.Key)
            .Select(x => x.Device with { RestoreAtStartup = x.Device.Key == keyboard?.Key && _restore.Checked })
            .ToList();
        if (keyboard is not null && devices.All(d => d.Key != keyboard.Key))
        {
            devices.Insert(0, keyboard with { RestoreAtStartup = _restore.Checked }); // chosen, but no longer paired
        }

        return devices;
    }

    private void RefreshAppleDriverState(string? status = null)
    {
        bool installed = AppleKeyboardDriver.IsInstalled;
        // Only relevant with an Apple keyboard the driver supports (or when the driver is already installed).
        _appleGroup.Visible = installed || AppleKeyboardDriver.IsSupportedKeyboardPresent();
        _appleInstall.Text = installed ? Strings.AppleDriverReinstall : Strings.AppleDriverInstall;
        _appleStatus.Text = status ?? (installed ? Strings.AppleDriverInstalled : Strings.AppleDriverNotInstalled);

        _updatingAppleFn = true;
        try
        {
            _appleFnMode.Enabled = installed;
            _appleFnMode.SelectedIndex = AppleTopRow.ReadFKeys(AppleTopRow.CurrentPath) ? 0 : 1;
        }
        finally
        {
            _updatingAppleFn = false;
        }
    }

    private async Task InstallAppleDriverAsync()
    {
        _appleInstall.Enabled = false;
        _appleFnMode.Enabled = false;
        var progress = new Progress<AppleKeyboardDriver.InstallStage>(stage => _appleStatus.Text = stage switch
        {
            AppleKeyboardDriver.InstallStage.Downloading => Strings.AppleDriverDownloading,
            AppleKeyboardDriver.InstallStage.Extracting => Strings.AppleDriverExtracting,
            _ => Strings.AppleDriverInstalling,
        });

        string status;
        try
        {
            var result = await PrivilegedActions.InstallAppleDriverAsync(progress);
            status = result switch
            {
                AppleKeyboardDriver.InstallResult.InstalledRebootRequired => Strings.AppleDriverInstallReboot,
                AppleKeyboardDriver.InstallResult.AlreadyUpToDate => Strings.AppleDriverUpToDate,
                AppleKeyboardDriver.InstallResult.AddedNoDevice => Strings.AppleDriverInstallNoDevice,
                _ => Strings.AppleDriverInstallDone,
            };
        }
        catch (OperationCanceledException)
        {
            status = Strings.AppleDriverCancelled;
        }
        catch (Exception ex)
        {
            Log.Error("Apple keyboard driver installation failed", ex);
            status = Strings.AppleDriverFailed(ex.Message);
        }

        if (IsDisposed)
        {
            return;
        }

        _appleInstall.Enabled = true;
        RefreshAppleDriverState(status);
    }

    private async Task ApplyAppleFnModeAsync()
    {
        if (_updatingAppleFn)
        {
            return;
        }

        bool media = _appleFnMode.SelectedIndex == 1;
        _appleFnMode.Enabled = false;
        string status;
        try
        {
            bool restarted = await PrivilegedActions.SetAppleMediaKeysByDefaultAsync(media);
            Log.Info($"Apple keyboard: media keys by default = {media}, keyboard restarted = {restarted}");
            AppleMediaKeysByDefault = media;
            status = restarted ? Strings.AppleFnAppliedNow : Strings.AppleFnApplied;
        }
        catch (OperationCanceledException)
        {
            status = Strings.AppleDriverCancelled;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to change the Apple keyboard Fn mode", ex);
            status = Strings.AppleDriverFailed(ex.Message);
        }

        if (!IsDisposed)
        {
            RefreshAppleDriverState(status);
        }
    }

    private void Save()
    {
        try
        {
            if (AutoStart.IsEnabled != _autoStart.Checked)
            {
                AutoStart.Set(_autoStart.Checked);
            }
        }
        catch (Exception ex)
        {
            // Keep the window open so the user can change the choice or try again.
            Log.Error("Failed to change autostart", ex);
            MessageBox.Show(this, Strings.SettingsAutoStartFailed(ex.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = new AppSettings
        {
            Devices = CurrentDevices(),
            ConnectTimeoutSeconds = (int)_timeout.Value,
            ShowNotifications = _notifications.Checked,
            LowBatteryThreshold = (int)_lowBattery.Value,
            WatchMicrophone = _watchMicrophone.Checked,
            RestoreMicrophoneAutomatically = _watchMicrophone.Checked && _restoreMicrophone.Checked,
            Language = (_language.SelectedItem as LanguageItem)?.Code ?? "",
            AppleMediaKeysByDefault = AppleMediaKeysByDefault,
            AppleKeyMap = _keyMap,
        };

        Close();
    }
}
