# Bluetooth Switcher

A Windows tray app that connects or reconnects selected Bluetooth devices in one click:

- **switches a multi-host keyboard** to the current computer — no need to turn on the other computer
  or press the channel button on the keyboard;
- **disconnects and reconnects a speaker or headphones**, e.g. to bring the microphone (Hands-Free) back;
- **shows the battery level** of connected devices and warns when it runs low;
- with the optional **background service**, **brings a keyboard back at startup and sign-in** — even on the
  Windows sign-in screen — and pairs it again without a dialog if another device (a phone, another PC) took it over.

## Download

Get the latest `BluetoothSwitcher-<version>-win-x64.zip` from the
[Releases](../../releases) page, unzip it and run `BluetoothSwitcher.exe` (no installation and no .NET runtime
needed). The app is not code-signed, so Windows SmartScreen may warn on the first start: **More info → Run anyway**.

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

Without the service, the app does not pair devices: it works with devices that are already paired with Windows.
For keyboards marked **Restore at startup and sign-in**, the service also handles the case where another host
took the keyboard over (see below), and the tray's **Connect** goes through the service.

## Background service: keyboard at startup and sign-in

Some multi-host keyboards — Apple Magic Keyboard in particular — keep the pairing key of only one host. After the
keyboard was used with a phone or another computer, it rejects this PC until it is paired again, and Windows asks
for confirmation in a dialog. Before you sign in there is nobody to answer it, so the keyboard simply does not type.

The optional Windows service fixes that. Install it in **Settings → Background service → Install service**
(one administrator confirmation) and check **Restore at startup and sign-in** for the keyboard. The service:

- reacts to events: Windows startup, sign-in, unlock, wake from sleep, the display turning on, and **Connect** in the tray;
- first gives the keyboard time to reconnect by itself (45 seconds after startup or wake: knocking earlier breaks a
  connection that is coming up and makes Windows drop the key), then “knocks” every 20–30 seconds for up to 10 minutes;
- if Windows still has the key, reinstalls the HID service; if the key is gone (or Windows forgot the device),
  pairs again with bonding (`BluetoothAuthenticateDeviceEx`), answers the pairing request itself
  (`BluetoothRegisterForAuthenticationEx`) so no dialog appears, and enables the HID service;
- never knocks outside these events: when you hand the keyboard to a phone on purpose, it is left alone.

After another host used the keyboard, turn the keyboard off and on (or press a key) on the sign-in screen: it only
listens for a new host for a short while after it wakes up. It types within about a minute.

> 🔒 Automatic confirmation skips the pairing code check. The service confirms only devices you selected and only while
> it is restoring them after one of the events above; at any other time Windows asks as usual.

The service also performs the actions that need administrator rights for the tray app (reinstalling device services,
the Apple keyboard driver), so the app itself runs without them. It is installed to
`%ProgramFiles%\Bluetooth Switcher` and runs as LocalSystem: a service must not start an executable that a normal user
could replace. After updating the app, use **Update service** in the settings. Tested with Apple Magic Keyboard (A2450).

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

- **Devices** shown in the tray menu, and for each Classic device **Restore at startup and sign-in** (needs the
  service) and the **services to reconnect** when it is already connected (Automatic, or a manual selection).
- **Background service**: install, update or remove it (see above).
- **Apple Magic Keyboard** driver and top row behavior (see above).
- **Language**, **connection timeout**, **low battery warning** threshold, notifications on success and
  **start with Windows**.

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

The app runs without administrator rights. The actions that need them — reinstalling device services
(`BluetoothSetServiceState` may return `ERROR_ACCESS_DENIED`), installing the Apple keyboard driver and changing its
Fn mode — go through the background service when it is installed. Without the service, the app asks for a UAC
confirmation when Windows denies an action.

Versions before 1.1 ran the app elevated through a Task Scheduler task. The first start of 1.1 removes that task
(and keeps **Start with Windows** through the usual `Run` registry key).

## Releasing

Pushing a version tag builds the app on GitHub Actions and publishes a release with the archive
(`.github/workflows/release.yml`); the version is taken from the tag:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

## Files

| Path | Purpose |
|---|---|
| `%AppData%\BluetoothSwitcher\settings.json` | settings (selected devices and services, timeout, language, battery threshold) |
| `%AppData%\BluetoothSwitcher\log.txt` | log (can be opened from the tray menu) |
| `%ProgramFiles%\Bluetooth Switcher\` | the service's copy of the app |
| `%ProgramData%\BluetoothSwitcher\service.json` | devices the service restores |
| `%ProgramData%\BluetoothSwitcher\service-log.txt` | service log (**Open service log** in the tray menu) |

## Project structure

```
src/BluetoothSwitcher/
├─ Program.cs                     — entry point: tray app, service (--service), elevated helpers
├─ Bluetooth/
│  ├─ BluetoothManager.cs         — device enumeration and reconnect logic
│  ├─ DeviceStatusMonitor.cs      — tracks ConnectionStatus for the menu
│  ├─ NativeBluetooth.cs          — P/Invoke to bthprops.cpl (service reinstall)
│  ├─ NativeBluetooth.Pairing.cs  — pairing without the Windows dialog
│  ├─ BatteryReader.cs            — battery level: Windows property, BLE GATT
│  ├─ HidBatteryReader.cs         — battery level from HID reports (incl. Apple)
│  ├─ HidNative.cs                — shared hid.dll interop, HID collection lookup
│  └─ Models.cs
├─ Infrastructure/                — settings, log, autostart, UAC
├─ Service/
│  ├─ ServiceHost.cs              — Windows service plumbing (SCM, sign-in/unlock/wake/display events)
│  ├─ BluetoothService.cs         — the service: commands from the tray app
│  ├─ PairingGuard.cs             — restores keyboards, re-pairs and confirms pairing requests
│  ├─ ServicePipe.cs              — named pipe protocol between the tray app and the service
│  ├─ ServiceInstaller.cs         — installs to Program Files, registers / removes the service
│  └─ PrivilegedActions.cs        — admin actions: through the service, or with UAC
├─ Keyboard/
│  └─ AppleKeyboardDriver.cs      — downloads/installs Apple's keyboard driver, Fn mode
├─ Localization/                  — Strings*.resx (en/ru/uk), language selection
└─ UI/
   ├─ TrayApplicationContext.cs   — tray icon and menu
   ├─ SettingsForm.cs             — settings window
   └─ TrayIconFactory.cs          — icon drawn at runtime
```

## License

[MIT](LICENSE). Apple's Boot Camp keyboard driver is not part of this project: it is downloaded from Apple on demand
and is covered by Apple's own license terms.

## Support the project

If you find the app useful, you can [buy the author a coffee ☕](https://buymeacoffee.com/sashko2000).
