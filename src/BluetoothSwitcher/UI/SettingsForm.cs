using BluetoothSwitcher.Bluetooth;
using BluetoothSwitcher.Infrastructure;
using BluetoothSwitcher.Keyboard;
using BluetoothSwitcher.Localization;

namespace BluetoothSwitcher.UI;

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
    private readonly CheckBox _autoStart;
    private readonly CheckBox _runAsAdmin;
    private readonly ComboBox _language;
    private readonly Label _status;

    private readonly GroupBox _servicesGroup;
    private readonly CheckBox _autoServices;
    private readonly CheckedListBox _services;
    private readonly Label _servicesNote;

    private readonly Label _appleInfo;
    private readonly Label _appleStatus;
    private readonly Button _appleInstall;
    private readonly ComboBox _appleFnMode;
    private readonly GroupBox _appleGroup;
    private bool _updatingAppleFn;

    private bool _loaded;
    private bool _updatingServices;
    private ListViewItem? _servicesItem;
    private int _servicesVersion;

    /// <summary>New settings if the user clicked "Save"; otherwise null.</summary>
    public AppSettings? Result { get; private set; }

    /// <summary>"Run as administrator" was just turned on in a non-elevated instance: restart through the task.</summary>
    public bool RestartElevatedRequested { get; private set; }

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;

        Text = Strings.SettingsTitle;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        MinimumSize = new Size(640, 830);
        Size = new Size(800, 910);
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

        // --- Services to reconnect for the selected device ---
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
            Height = 170,
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
        bool taskExists = ElevatedTask.TryGetState(out bool taskAtLogon);
        _autoStart = new CheckBox { Text = Strings.SettingsAutoStart, AutoSize = true, Checked = AutoStart.IsEnabled || taskAtLogon };
        _runAsAdmin = new CheckBox { Text = Strings.SettingsRunAsAdmin, AutoSize = true, Checked = taskExists };

        var options = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 8, 0, 0) };
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
        options.Controls.Add(new Label { Text = Strings.SettingsTimeout, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        options.Controls.Add(_timeout, 1, 1);
        options.Controls.Add(new Label { Text = Strings.SettingsLowBattery, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        options.Controls.Add(_lowBattery, 1, 2);
        options.Controls.Add(_notifications, 0, 3);
        options.SetColumnSpan(_notifications, 2);
        options.Controls.Add(_autoStart, 0, 4);
        options.SetColumnSpan(_autoStart, 2);
        options.Controls.Add(_runAsAdmin, 0, 5);
        options.SetColumnSpan(_runAsAdmin, 2);

        // The form is shown modelessly (Show, not ShowDialog), so a button's DialogResult does not close it:
        // both buttons close the form explicitly.
        _ok = new Button { Text = Strings.SettingsSave, AutoSize = true };
        _ok.Click += async (_, _) => await SaveAsync();
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

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12, 12, 12, 0) };
        // The column is pinned to the window width, otherwise long text stretches the layout past the edge.
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(hint, 0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(listButtons, 0, 2);
        root.Controls.Add(_servicesGroup, 0, 3);
        root.Controls.Add(_appleGroup, 0, 4);
        root.Controls.Add(options, 0, 5);

        // Wrap long labels to the window width.
        void Wrap()
        {
            int width = Math.Max(100, root.ClientSize.Width - root.Padding.Horizontal - hint.Margin.Horizontal);
            hint.MaximumSize = new Size(width, 0);
            _servicesNote.MaximumSize = new Size(Math.Max(100, width - 20), 0);
            _appleInfo.MaximumSize = new Size(Math.Max(100, width - 20), 0);
            _appleStatus.MaximumSize = new Size(Math.Max(100, width - 260), 0);
        }
        root.SizeChanged += (_, _) => Wrap();

        Controls.Add(root);
        Controls.Add(dialogButtons); // added after root so that Dock.Bottom is laid out first
        Wrap();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
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

    private sealed record LanguageItem(string Code, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record ServiceItem(Guid Id)
    {
        public override string ToString()
        {
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
            _appleFnMode.SelectedIndex = AppleKeyboardDriver.MediaKeysByDefault == true ? 1 : 0;
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
            var result = await AppleKeyboardDriver.InstallAsync(progress);
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
            bool restarted = await AppleKeyboardDriver.SetMediaKeysByDefaultAsync(media);
            Log.Info($"Apple keyboard: media keys by default = {media}, keyboard restarted = {restarted}");
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

    private async Task SaveAsync()
    {
        _ok.Enabled = false;
        _cancel.Enabled = false;
        try
        {
            await ApplyStartupAsync(_autoStart.Checked, _runAsAdmin.Checked);
        }
        catch (Exception ex)
        {
            // Keep the window open so the user can change the choice or try again.
            if (ex is not OperationCanceledException)
            {
                Log.Error("Failed to change startup settings", ex);
            }

            string reason = ex is OperationCanceledException ? Strings.OperationCancelled : ex.Message;
            MessageBox.Show(this, Strings.SettingsAutoStartFailed(reason), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _ok.Enabled = true;
            _cancel.Enabled = true;
            return;
        }

        var devices = _loaded
            ? _list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (SavedDevice)i.Tag!).ToList()
            : _settings.Devices;

        Result = new AppSettings
        {
            Devices = devices,
            ConnectTimeoutSeconds = (int)_timeout.Value,
            ShowNotifications = _notifications.Checked,
            LowBatteryThreshold = (int)_lowBattery.Value,
            Language = (_language.SelectedItem as LanguageItem)?.Code ?? "",
        };

        RestartElevatedRequested = _runAsAdmin.Checked && !Elevation.IsElevated;
        Close();
    }

    /// <summary>
    /// Start with Windows / run as administrator:
    /// - as administrator: a scheduled task (with a logon trigger if autostart is on), no Run key;
    /// - otherwise: no task, and the HKCU Run key for autostart.
    /// Creating or deleting the task asks for administrator rights once.
    /// </summary>
    private static async Task ApplyStartupAsync(bool startWithWindows, bool runAsAdmin)
    {
        bool taskExists = ElevatedTask.TryGetState(out bool taskAtLogon);
        if (runAsAdmin)
        {
            if (!taskExists || taskAtLogon != startWithWindows)
            {
                await ElevatedTask.CreateAsync(startWithWindows);
            }

            if (AutoStart.IsEnabled)
            {
                AutoStart.Set(false); // the task starts the app; avoid a second, non-elevated start
            }
        }
        else
        {
            if (taskExists)
            {
                await ElevatedTask.DeleteAsync();
            }

            if (AutoStart.IsEnabled != startWithWindows)
            {
                AutoStart.Set(startWithWindows);
            }
        }
    }
}
