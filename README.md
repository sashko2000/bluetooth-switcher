# Bluetooth Switcher

A Windows tray app that connects or reconnects selected Bluetooth devices in one click:

- **switches a multi-host keyboard** to the current computer — no need to turn on the other computer
  or press the channel button on the keyboard;
- **disconnects and reconnects a speaker or headphones**, e.g. to bring the microphone (Hands-Free) back;
- **shows the battery level** of connected devices and warns when it runs low.

## How it works

1. In **Settings**, check any paired devices (the list comes from Windows: Classic and BLE).
2. Each of them gets an item in the tray icon menu:
   - **Connect “Name”** if the device is not connected to this PC;
   - **Reconnect “Name”** with a check mark if it is connected.

   If only one device is selected, double-clicking the tray icon does the same.
3. When you click the item:
   - **Classic (BR/EDR), not connected**:
     1. An uncached SDP query makes the adapter page the device.
     2. If that is not enough, the device's standard profiles are reinstalled
        (`BluetoothSetServiceState` disable → enable), and Windows initiates the connection again.
   - **Classic, already connected**: only the services selected for the device are reinstalled
     (by default HID for keyboards/mice and Hands-Free for audio devices). This is the same as
     “Disconnect → Connect” in Windows Settings. Each service takes about 3 seconds.
   - **BLE**: `GattSession.MaintainConnection = true` plus an uncached GATT query.
     The public Windows API offers no way to force-disconnect an active BLE connection.
4. The app waits until the connection has held steadily for a few seconds and shows a notification.

> ⚠️ The result depends on the device firmware. Many keyboards (Logitech MX Keys/K380/K780, Keychron,
> Microsoft, Apple Magic Keyboard, etc.) accept an incoming connection from a host paired on another
> channel and switch over by themselves. Some only listen on the active channel; those cannot be
> switched from software at all. Speakers that support only one connection at a time may also refuse
> until they are disconnected from the other computer.

The app does not pair devices: it works only with devices that are already paired with Windows.

## Battery level

If a connected device reports its battery level, it is shown in the menu item
(“Reconnect “SRS-XB100” — battery 80%”) and in the tray icon tooltip. Sources, in order:

1. The value Windows shows in Settings → Bluetooth & devices (Hands-Free speakers and headsets, many BLE devices).
2. The standard GATT Battery Service for BLE devices.
3. The device's HID reports: standard Battery Strength / Absolute State Of Charge usages and Apple's
   report `0x90` — this is how the battery of Apple Magic Keyboard is read (other Apple Bluetooth devices
   that use the same report should work too, but have not been tested).

When the level drops to the configured threshold (15% by default, 0 turns it off), a notification is shown.

## Apple Magic Keyboard: Fn key and top row

On its own, Windows ignores the Fn key of Apple keyboards, so Fn+Backspace (Delete) and the media keys on the
top row do not work. Apple's Boot Camp keyboard driver fixes that, and the app can install it for you:
**Settings → Apple Magic Keyboard → Install driver**. The section is shown only when a supported Magic Keyboard
(2015–2021) is paired with the computer or the driver is already installed.

- The driver is **not shipped with the app**: it is Apple's proprietary software. The app downloads the official
  Boot Camp 6.1.14 update from Apple's servers, verifies its SHA-256, extracts the keyboard driver
  (`AppleKeyboardMagic2`, version 6.1.8086.4, which knows the 2021 keyboards, e.g. A2450) and installs it with
  `pnputil`. An internet connection and administrator rights are required. Apple's Boot Camp license terms apply.
- **Top row keys** lets you choose the default behavior: F1–F12 with media on Fn, or media keys with F1–F12 on Fn
  (the driver's `OSXFnBehavior` setting, Mac behavior after installation). The driver reads it only when it starts,
  so the app restarts the keyboard right away to apply the change (`pnputil /restart-device`).
- The lock key (top right on 2021 models) is not handled by the driver.
- To remove the driver: Device Manager → the keyboard (“Apple Keyboard”) → Uninstall device → check
  “Attempt to remove the driver for this device”, then reconnect the keyboard.

## Settings

- **Devices** shown in the tray menu, and for each Classic device the **services to reconnect** when it is
  already connected (Automatic, or a manual selection).
- **Apple Magic Keyboard** driver and top row behavior (see above).
- **Language**, **connection timeout**, **low battery warning** threshold, notifications on success,
  and **start with Windows**.

## Languages

The UI is available in English, Russian and Ukrainian. By default the Windows display language is used
(English if it is not supported). The language can be changed in **Settings**.

Strings live in `src/BluetoothSwitcher/Localization/Strings*.resx`. To add a language, copy `Strings.resx`
to `Strings.<culture>.resx` (e.g. `Strings.de.resx`), translate the values and add the language to
`AppLanguage.Supported` and to `SatelliteResourceLanguages` in the `.csproj`.

## Requirements

- Windows 10 2004 (19041) or Windows 11
- .NET 10 SDK, Visual Studio 2026 / Rider / `dotnet` CLI
- The device must be **paired beforehand** with each computer (on its own channel for multi-host keyboards).
  Install the app on every computer you want to pull the keyboard to.
- NuGet restores the only third-party dependency, [SharpCompress](https://github.com/adamhathcock/sharpcompress)
  (MIT), used to extract Apple's driver package.

## Build and run

```powershell
dotnet build -c Release
dotnet run --project src/BluetoothSwitcher

# Publish to a folder (requires the .NET 10 Desktop Runtime)
dotnet publish src/BluetoothSwitcher -c Release -r win-x64 --self-contained false -o publish

# Or fully self-contained
dotnet publish src/BluetoothSwitcher -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## Administrator rights

On some systems `BluetoothSetServiceState` returns `ERROR_ACCESS_DENIED` without elevation.
In that case the menu offers **Restart as administrator**.
If you need elevation permanently, autostart via the `Run` registry key will not work (UAC blocks it).
Use a Task Scheduler task with “Run with highest privileges” instead:

```powershell
schtasks /Create /TN "BluetoothSwitcher" /TR "\"C:\Path\BluetoothSwitcher.exe\"" /SC ONLOGON /RL HIGHEST /F
```

## Files

| Path | Purpose |
|---|---|
| `%AppData%\BluetoothSwitcher\settings.json` | settings (selected devices and services, timeout, language, battery threshold) |
| `%AppData%\BluetoothSwitcher\log.txt` | log (can be opened from the tray menu) |

## Project structure

```
src/BluetoothSwitcher/
├─ Program.cs                     — entry point, single instance
├─ Bluetooth/
│  ├─ BluetoothManager.cs         — device enumeration and reconnect logic
│  ├─ DeviceStatusMonitor.cs      — tracks ConnectionStatus for the menu
│  ├─ NativeBluetooth.cs          — P/Invoke to bthprops.cpl (service reinstall)
│  ├─ BatteryReader.cs            — battery level: Windows property, BLE GATT
│  ├─ HidBatteryReader.cs         — battery level from HID reports (incl. Apple)
│  ├─ HidNative.cs                — shared hid.dll interop, HID collection lookup
│  └─ Models.cs
├─ Infrastructure/                — settings, log, autostart, UAC
├─ Keyboard/
│  └─ AppleKeyboardDriver.cs      — downloads/installs Apple's keyboard driver, Fn mode
├─ Localization/                  — Strings*.resx (en/ru/uk), language selection
└─ UI/
   ├─ TrayApplicationContext.cs   — tray icon and menu
   ├─ SettingsForm.cs             — settings window
   └─ TrayIconFactory.cs          — icon drawn at runtime
```

## Support the project

If you find the app useful, you can [buy the author a coffee ☕](https://buymeacoffee.com/sashko2000).
