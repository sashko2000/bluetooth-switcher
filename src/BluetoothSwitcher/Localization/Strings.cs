// Strongly typed access to Localization/Strings*.resx.
// To add a language, add Strings.<culture>.resx with the same keys and list it in AppLanguage.Supported.
using System.Globalization;
using System.Resources;

namespace BluetoothSwitcher.Localization;

internal static class Strings
{
    private static readonly ResourceManager Resources = new("BluetoothSwitcher.Localization.Strings", typeof(Strings).Assembly);

    private static string Get(string name) => Resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    private static string Format(string name, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(name), args);

    public static string TraySelectDeviceTitle => Get(nameof(TraySelectDeviceTitle));
    public static string TraySelectDeviceText => Get(nameof(TraySelectDeviceText));
    public static string TrayNoDevices => Get(nameof(TrayNoDevices));
    public static string TrayConnecting(object? arg0) => Format(nameof(TrayConnecting), arg0);
    public static string TrayReconnect(object? arg0) => Format(nameof(TrayReconnect), arg0);
    public static string TrayConnectNotFound(object? arg0) => Format(nameof(TrayConnectNotFound), arg0);
    public static string TrayConnect(object? arg0) => Format(nameof(TrayConnect), arg0);
    public static string TrayReconnectTooltip => Get(nameof(TrayReconnectTooltip));
    public static string TrayItemBattery(object? arg0, object? arg1) => Format(nameof(TrayItemBattery), arg0, arg1);
    public static string LowBatteryTitle => Get(nameof(LowBatteryTitle));
    public static string LowBatteryText(object? arg0, object? arg1) => Format(nameof(LowBatteryText), arg0, arg1);
    public static string SettingsLowBattery => Get(nameof(SettingsLowBattery));
    public static string AppleGroup => Get(nameof(AppleGroup));
    public static string AppleDriverInfo => Get(nameof(AppleDriverInfo));
    public static string AppleDriverInstalled => Get(nameof(AppleDriverInstalled));
    public static string AppleDriverNotInstalled => Get(nameof(AppleDriverNotInstalled));
    public static string AppleDriverInstall => Get(nameof(AppleDriverInstall));
    public static string AppleDriverReinstall => Get(nameof(AppleDriverReinstall));
    public static string AppleDriverDownloading => Get(nameof(AppleDriverDownloading));
    public static string AppleDriverExtracting => Get(nameof(AppleDriverExtracting));
    public static string AppleDriverInstalling => Get(nameof(AppleDriverInstalling));
    public static string AppleDriverInstallDone => Get(nameof(AppleDriverInstallDone));
    public static string AppleDriverInstallReboot => Get(nameof(AppleDriverInstallReboot));
    public static string AppleDriverUpToDate => Get(nameof(AppleDriverUpToDate));
    public static string AppleDriverInstallNoDevice => Get(nameof(AppleDriverInstallNoDevice));
    public static string AppleDriverFailed(object? arg0) => Format(nameof(AppleDriverFailed), arg0);
    public static string AppleDriverCancelled => Get(nameof(AppleDriverCancelled));
    public static string AppleFnLabel => Get(nameof(AppleFnLabel));
    public static string AppleFnStandard => Get(nameof(AppleFnStandard));
    public static string AppleFnMedia => Get(nameof(AppleFnMedia));
    public static string AppleFnAppliedNow => Get(nameof(AppleFnAppliedNow));
    public static string AppleFnApplied => Get(nameof(AppleFnApplied));
    public static string TrayConnectTooltip => Get(nameof(TrayConnectTooltip));
    public static string TraySettings => Get(nameof(TraySettings));
    public static string TrayRestartAsAdmin => Get(nameof(TrayRestartAsAdmin));
    public static string TrayOpenLog => Get(nameof(TrayOpenLog));
    public static string TrayExit => Get(nameof(TrayExit));
    public static string TrayTooltipBusy(object? arg0) => Format(nameof(TrayTooltipBusy), arg0);
    public static string NotifyDone => Get(nameof(NotifyDone));
    public static string NotifyFailed => Get(nameof(NotifyFailed));
    public static string NotifyError => Get(nameof(NotifyError));
    public static string NotifySaveFailed(object? arg0) => Format(nameof(NotifySaveFailed), arg0);
    public static string KindKeyboardMouse => Get(nameof(KindKeyboardMouse));
    public static string KindKeyboard => Get(nameof(KindKeyboard));
    public static string KindMouse => Get(nameof(KindMouse));
    public static string KindPeripheral => Get(nameof(KindPeripheral));
    public static string KindKeyboardBle => Get(nameof(KindKeyboardBle));
    public static string KindBleDevice => Get(nameof(KindBleDevice));
    public static string KindAudio => Get(nameof(KindAudio));
    public static string KindComputer => Get(nameof(KindComputer));
    public static string KindPhone => Get(nameof(KindPhone));
    public static string KindImaging => Get(nameof(KindImaging));
    public static string KindWearable => Get(nameof(KindWearable));
    public static string KindHealth => Get(nameof(KindHealth));
    public static string KindToy => Get(nameof(KindToy));
    public static string KindAccessPoint => Get(nameof(KindAccessPoint));
    public static string KindOther => Get(nameof(KindOther));
    public static string ResultNotFound(object? arg0) => Format(nameof(ResultNotFound), arg0);
    public static string ResultAccessDenied => Get(nameof(ResultAccessDenied));
    public static string ResultNoRadio => Get(nameof(ResultNoRadio));
    public static string ResultApiError(object? arg0) => Format(nameof(ResultApiError), arg0);
    public static string ResultBleAlreadyConnected(object? arg0) => Format(nameof(ResultBleAlreadyConnected), arg0);
    public static string ResultConnected(object? arg0) => Format(nameof(ResultConnected), arg0);
    public static string ResultReconnected(object? arg0) => Format(nameof(ResultReconnected), arg0);
    public static string ResultNotConnected(object? arg0) => Format(nameof(ResultNotConnected), arg0);
    public static string SettingsTitle => Get(nameof(SettingsTitle));
    public static string SettingsHint => Get(nameof(SettingsHint));
    public static string ColumnDevice => Get(nameof(ColumnDevice));
    public static string ColumnAddress => Get(nameof(ColumnAddress));
    public static string ColumnType => Get(nameof(ColumnType));
    public static string ColumnStatus => Get(nameof(ColumnStatus));
    public static string SettingsRefresh => Get(nameof(SettingsRefresh));
    public static string SettingsSearching => Get(nameof(SettingsSearching));
    public static string SettingsNoneFound => Get(nameof(SettingsNoneFound));
    public static string SettingsFoundCount(object? arg0) => Format(nameof(SettingsFoundCount), arg0);
    public static string SettingsError(object? arg0) => Format(nameof(SettingsError), arg0);
    public static string StatusConnected => Get(nameof(StatusConnected));
    public static string StatusNotConnected => Get(nameof(StatusNotConnected));
    public static string StatusNotFound => Get(nameof(StatusNotFound));
    public static string ServicesGroup => Get(nameof(ServicesGroup));
    public static string ServicesGroupFor(object? arg0) => Format(nameof(ServicesGroupFor), arg0);
    public static string ServicesGroupBle(object? arg0) => Format(nameof(ServicesGroupBle), arg0);
    public static string ServicesGroupLoading(object? arg0) => Format(nameof(ServicesGroupLoading), arg0);
    public static string ServicesGroupNotFound(object? arg0) => Format(nameof(ServicesGroupNotFound), arg0);
    public static string ServicesAuto => Get(nameof(ServicesAuto));
    public static string ServicesNote => Get(nameof(ServicesNote));
    public static string ServiceVendor(object? arg0) => Format(nameof(ServiceVendor), arg0);
    public static string ServiceHintHid => Get(nameof(ServiceHintHid));
    public static string ServiceHintAudio => Get(nameof(ServiceHintAudio));
    public static string ServiceHintMicrophone => Get(nameof(ServiceHintMicrophone));
    public static string ServiceHintControls => Get(nameof(ServiceHintControls));
    public static string SettingsTimeout => Get(nameof(SettingsTimeout));
    public static string SettingsNotifications => Get(nameof(SettingsNotifications));
    public static string SettingsAutoStart => Get(nameof(SettingsAutoStart));
    public static string SettingsRunAsAdmin => Get(nameof(SettingsRunAsAdmin));
    public static string OperationCancelled => Get(nameof(OperationCancelled));
    public static string SettingsLanguage => Get(nameof(SettingsLanguage));
    public static string SettingsLanguageSystem => Get(nameof(SettingsLanguageSystem));
    public static string SettingsSave => Get(nameof(SettingsSave));
    public static string SettingsCancel => Get(nameof(SettingsCancel));
    public static string SettingsAutoStartFailed(object? arg0) => Format(nameof(SettingsAutoStartFailed), arg0);
}
