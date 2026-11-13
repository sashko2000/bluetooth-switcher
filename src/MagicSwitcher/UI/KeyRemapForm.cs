using MagicSwitcher.Keyboard;
using MagicSwitcher.Localization;

namespace MagicSwitcher.UI;

/// <summary>Choose what the modifier keys of an Apple keyboard send. Other keyboards are not affected.</summary>
[System.ComponentModel.DesignerCategory("Code")] // built in code: keeps Visual Studio from opening the designer and creating a .resx
internal sealed class KeyRemapForm : Form
{
    private readonly Dictionary<AppleKey, ComboBox> _combos = [];

    /// <summary>The mappings when the user clicked OK.</summary>
    public List<KeyMapping> Result { get; private set; }

    public KeyRemapForm(IReadOnlyCollection<KeyMapping> current)
    {
        Result = current.ToList();
        Text = Strings.RemapTitle;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var note = new Label { Text = Strings.RemapNote, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(3, 3, 3, 10) };

        // Two columns like the keyboard: left side keys, right side keys.
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 4 };
        AppleKey[] left = [AppleKey.CapsLock, AppleKey.LeftShift, AppleKey.LeftControl, AppleKey.LeftOption, AppleKey.LeftCommand];
        AppleKey?[] right = [null, AppleKey.RightShift, null, AppleKey.RightOption, AppleKey.RightCommand];
        for (int row = 0; row < left.Length; row++)
        {
            AddKey(grid, left[row], 0, row, current);
            if (right[row] is { } key)
            {
                AddKey(grid, key, 2, row, current);
            }
        }

        var ok = new Button { Text = Strings.SettingsSave, AutoSize = true, DialogResult = DialogResult.OK };
        ok.Click += (_, _) => Result = _combos
            .Where(kv => kv.Value.SelectedItem is TargetItem { Target: { } })
            .Select(kv => new KeyMapping(kv.Key, ((TargetItem)kv.Value.SelectedItem!).Target!.Value))
            .ToList();
        var cancel = new Button { Text = Strings.SettingsCancel, AutoSize = true, DialogResult = DialogResult.Cancel };
        var reset = new Button { Text = Strings.RemapReset, AutoSize = true };
        reset.Click += (_, _) =>
        {
            foreach (var combo in _combos.Values)
            {
                combo.SelectedIndex = 0;
            }
        };
        AcceptButton = ok;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(reset);

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(12) };
        root.Controls.Add(note, 0, 0);
        root.Controls.Add(grid, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
    }

    private void AddKey(TableLayoutPanel grid, AppleKey key, int column, int row, IReadOnlyCollection<KeyMapping> current)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, Margin = new Padding(3, 3, 18, 3) };
        combo.Items.Add(new TargetItem(null, Strings.RemapNoChange));
        foreach (var target in Enum.GetValues<KeyTarget>())
        {
            combo.Items.Add(new TargetItem(target, DescribeTarget(target)));
        }

        var mapped = current.FirstOrDefault(m => m.From == key);
        combo.SelectedItem = combo.Items.Cast<TargetItem>().FirstOrDefault(i => mapped is not null && i.Target == mapped.To) ?? combo.Items[0];
        _combos[key] = combo;

        grid.Controls.Add(new Label { Text = DescribeKey(key), AutoSize = true, Anchor = AnchorStyles.Left }, column, row);
        grid.Controls.Add(combo, column + 1, row);
    }

    public static string DescribeKey(AppleKey key) => key switch
    {
        AppleKey.CapsLock => "Caps Lock",
        AppleKey.LeftControl => $"Control ({Strings.SideLeft})",
        AppleKey.LeftOption => $"Option ({Strings.SideLeft})",
        AppleKey.LeftCommand => $"Command ({Strings.SideLeft})",
        AppleKey.RightCommand => $"Command ({Strings.SideRight})",
        AppleKey.RightOption => $"Option ({Strings.SideRight})",
        AppleKey.LeftShift => $"Shift ({Strings.SideLeft})",
        AppleKey.RightShift => $"Shift ({Strings.SideRight})",
        _ => key.ToString(),
    };

    public static string DescribeTarget(KeyTarget target) => target switch
    {
        KeyTarget.Disabled => Strings.RemapDisabled,
        KeyTarget.LeftControl => $"Ctrl ({Strings.SideLeft})",
        KeyTarget.RightControl => $"Ctrl ({Strings.SideRight})",
        KeyTarget.LeftAlt => $"Alt ({Strings.SideLeft})",
        KeyTarget.RightAlt => $"Alt ({Strings.SideRight}) / AltGr",
        KeyTarget.LeftWin => $"Win ({Strings.SideLeft})",
        KeyTarget.RightWin => $"Win ({Strings.SideRight})",
        KeyTarget.LeftShift => $"Shift ({Strings.SideLeft})",
        KeyTarget.RightShift => $"Shift ({Strings.SideRight})",
        KeyTarget.CapsLock => "Caps Lock",
        KeyTarget.Escape => "Esc",
        KeyTarget.Delete => "Delete",
        KeyTarget.Backspace => "Backspace",
        KeyTarget.Enter => "Enter",
        KeyTarget.Insert => "Insert",
        KeyTarget.PrintScreen => "Print Screen",
        KeyTarget.Menu => "Menu",
        _ => target.ToString(),
    };

    private sealed record TargetItem(KeyTarget? Target, string Name)
    {
        public override string ToString() => Name;
    }
}
