using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MagicSwitcher.Infrastructure;

namespace MagicSwitcher.Keyboard;

/// <summary>
/// Remaps keys of Apple keyboards only, leaving every other keyboard alone.
/// <para>
/// A low-level keyboard hook can replace keys but does not know which keyboard a key came from; raw input knows the
/// device but arrives about a millisecond after the hook, and not at all for a key the hook blocked (measured with
/// tools/InputProbe). So a remapped key is let through first; when raw input then reports that it came from an Apple
/// keyboard, the original key is released (with a neutral "mask" key first, so a lone Win or Alt does not open the Start
/// or window menu) and the replacement is pressed. Repeats and the release of a key that is known to be held on the Apple
/// keyboard are replaced right in the hook.
/// </para>
/// <para>
/// Top row ("Fn Lock", when a state file is given): Apple's driver stays in its default mode, where the top row sends
/// media keys and Fn+key sends F-keys. With Fn Lock on, the media keys of the Apple keyboard become F7–F12 and its
/// Spotlight/dictation keys (the driver sends Win+S and Win+C) become F4/F5. Media keys arrive without a device, but the
/// keyboard's consumer-control HID report comes just before them and names the device, so they can be replaced exactly.
/// F1/F2 (brightness) never reach Windows in this mode: they work only with Fn. Fn+Esc — the driver sends Pause —
/// toggles Fn Lock.
/// </para>
/// Runs its own thread with a message loop (hook callbacks and raw input are delivered through it).
/// </summary>
internal sealed class KeyRemapper : IDisposable
{
    private const uint WmQuit = 0x0012;
    private const uint WmInput = 0x00FF;
    private const int WhKeyboardLl = 13;
    private const uint LlkhfExtended = 0x01;
    private const uint LlkhfUp = 0x80;
    private const ushort MaskKey = 0xE8; // unassigned virtual key, the usual way to "mask" a lone Win/Alt press
    private static readonly UIntPtr Marker = new(0x4D53574B); // "MSWK": our own injected input, ignored by the hook

    private readonly Dictionary<int, (KeyCode From, KeyCode? To)> _map;
    private readonly Dictionary<int, long> _pending = []; // original passed, waiting for raw input (message thread only)
    private readonly HashSet<int> _held = [];              // remapped keys held down on an Apple keyboard
    private readonly Dictionary<IntPtr, bool> _isApple = [];

    // --- Top row (Fn Lock) ---
    private const int LeftWinId = 0x15B;
    private static readonly KeyCode LeftWin = new(0x5B, true, 0x5B);
    private static readonly Dictionary<uint, KeyCode> MediaToFKey = new()
    {
        [0xB1] = new(0x41, false, 0x76), // previous track -> F7
        [0xB3] = new(0x42, false, 0x77), // play/pause     -> F8
        [0xB0] = new(0x43, false, 0x78), // next track     -> F9
        [0xAD] = new(0x44, false, 0x79), // mute           -> F10
        [0xAE] = new(0x57, false, 0x7A), // volume down    -> F11
        [0xAF] = new(0x58, false, 0x7B), // volume up      -> F12
    };
    private static readonly Dictionary<int, uint> ConsumerUsageToVk = new()
    {
        [0xB6] = 0xB1, [0xCD] = 0xB3, [0xB5] = 0xB0, [0xE2] = 0xAD, [0xEA] = 0xAE, [0xE9] = 0xAF,
    };
    private static readonly Dictionary<uint, KeyCode> WinComboToFKey = new()
    {
        [0x1F] = new(0x3E, false, 0x73), // Win+S (Spotlight key) -> F4
        [0x2E] = new(0x3F, false, 0x74), // Win+C (dictation key) -> F5
    };
    private readonly string? _topRowStatePath;
    private bool _fKeys;
    private readonly Dictionary<uint, KeyCode> _topRowHeld = []; // media vk / combo letter scan -> F-key held instead
    private int _appleMediaUsage;
    private double _appleMediaAt = double.MinValue;
    private double _appleWinDownAt = double.MinValue;
    private bool _swallowWinUp;

    private static double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private HookProc? _hookProc;
    private WndProc? _wndProc;
    private IntPtr _hook;
    private WinEventProc? _winEventProc;
    private IntPtr _winEventHook;

    /// <param name="topRowStatePath">File with the Fn Lock state (see <see cref="AppleTopRow"/>); null turns the top row handling off.</param>
    public KeyRemapper(IEnumerable<KeyMapping> mappings, string? topRowStatePath = null)
    {
        _topRowStatePath = topRowStatePath;
        _fKeys = topRowStatePath is not null && AppleTopRow.ReadFKeys(topRowStatePath);
        _map = mappings
            .Select(m => (From: KeyCode.Of(m.From), To: KeyCode.Of(m.To)))
            .Where(m => m.To != m.From)
            .GroupBy(m => m.From.Id)
            .ToDictionary(g => g.Key, g => g.Last());
        _thread = new Thread(Run) { IsBackground = true, Name = "Key remapper" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public void Start()
    {
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(3));
        }
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        try
        {
            _wndProc = WindowProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                // A new class per instance: a class registered earlier in this process points at an old delegate.
                lpszClassName = $"MagicSwitcherKeyRemapper{Guid.NewGuid():N}",
            };
            RegisterClassEx(ref wc);
            var hwnd = CreateWindowEx(0, wc.lpszClassName, "MagicSwitcher key remapper", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            const uint Sink = 0x00000100, PageOnly = 0x00000020; // RIDEV_INPUTSINK, RIDEV_PAGEONLY
            var devices = new List<RAWINPUTDEVICE> { new() { usUsagePage = 0x01, usUsage = 0x06, dwFlags = Sink, hwndTarget = hwnd } };
            if (_topRowStatePath is not null)
            {
                // Consumer-control collections: the HID reports behind media keys, with the device they come from.
                devices.Add(new() { usUsagePage = 0x0C, usUsage = 0, dwFlags = Sink | PageOnly, hwndTarget = hwnd });
            }

            if (hwnd == IntPtr.Zero || !RegisterRawInputDevices(devices.ToArray(), (uint)devices.Count, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                Log.Error($"Key remapper: raw input registration failed (error {Marshal.GetLastWin32Error()})");
                return;
            }

            _hookProc = HookCallback;
            _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Log.Error($"Key remapper: SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()})");
                return;
            }

            // When the desktop switches (lock screen, UAC prompt) the hook misses key releases: a replacement key
            // would stay "held". Release them when the desktop changes (it works once our desktop is active again).
            _winEventProc = (_, _, _, _, _, _, _) => ReleaseHeld();
            _winEventHook = SetWinEventHook(0x0020, 0x0020, IntPtr.Zero, _winEventProc, 0, 0, 0); // EVENT_SYSTEM_DESKTOPSWITCH

            Log.Info($"Key remapper started: {string.Join(", ", _map.Values.Select(m => $"{m.From.VirtualKey:X2}->{(m.To is { } t ? t.VirtualKey.ToString("X2") : "off")}"))}" +
                     (_topRowStatePath is null ? "" : $"; top row: {(_fKeys ? "F-keys" : "media keys")}"));
        }
        finally
        {
            _ready.Set();
        }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_hook);
        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
        }

        ReleaseHeld();
        Log.Info("Key remapper stopped");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int id = (int)k.scanCode | ((k.flags & LlkhfExtended) != 0 ? 0x100 : 0);

            if (k.dwExtraInfo != Marker && _topRowStatePath is not null && HandleTopRow(k, id))
            {
                return new IntPtr(1);
            }

            // Scan codes above 0xFF are fake keys made by Windows itself (e.g. the left Ctrl of AltGr is 0x21D).
            if (k.dwExtraInfo != Marker && k.scanCode <= 0xFF && _map.TryGetValue(id, out var mapping))
            {
                bool up = (k.flags & LlkhfUp) != 0;
                if (_held.Contains(id))
                {
                    // Known to be held on the Apple keyboard: replace repeats and the release right here.
                    if (up)
                    {
                        _held.Remove(id);
                    }

                    if (mapping.To is { } to)
                    {
                        Send([Key(to, up)]);
                    }

                    return new IntPtr(1);
                }

                if (up)
                {
                    _pending.Remove(id); // another keyboard (or raw input never said Apple): the original stays
                }
                else
                {
                    _pending[id] = Environment.TickCount64; // let it through; raw input decides in a moment
                }
            }
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>Fn Lock in the hook. Returns true if the key was replaced and must be swallowed.</summary>
    private bool HandleTopRow(KBDLLHOOKSTRUCT k, int id)
    {
        bool up = (k.flags & LlkhfUp) != 0;
        bool extended = (k.flags & LlkhfExtended) != 0;

        // The Win key release that belongs to a Win+S / Win+C already turned into F4 / F5.
        if (_swallowWinUp && id == LeftWinId && up)
        {
            _swallowWinUp = false;
            return true;
        }

        // A media key or a combo letter that is being held as an F-key: replace repeats and the release.
        uint heldKey = MediaToFKey.ContainsKey(k.vkCode) ? k.vkCode : !extended ? 0x1000 | k.scanCode : 0;
        if (heldKey != 0 && _topRowHeld.TryGetValue(heldKey, out var held))
        {
            if (up)
            {
                _topRowHeld.Remove(heldKey);
            }

            Send([Key(held, up)]);
            return true;
        }

        if (!_fKeys || up)
        {
            return false;
        }

        // Media key: ours only if the Apple keyboard's consumer-control report for this key arrived just before.
        if (MediaToFKey.TryGetValue(k.vkCode, out var fKey))
        {
            DrainRawInput(); // the report is posted before the key, but the hook may get to run first
            if (NowMs - _appleMediaAt <= 100 && ConsumerUsageToVk.TryGetValue(_appleMediaUsage, out uint vk) && vk == k.vkCode)
            {
                _appleMediaAt = double.MinValue;
                _topRowHeld[k.vkCode] = fKey;
                Send([Key(fKey, up: false)]);
                return true;
            }

            return false;
        }

        // Win+S / Win+C sent by the driver for the Spotlight / dictation keys: the letter follows Win within a
        // millisecond or two, which no human does.
        if (!extended && WinComboToFKey.TryGetValue(k.scanCode, out fKey))
        {
            DrainRawInput();
            if (NowMs - _appleWinDownAt <= 15)
            {
                _appleWinDownAt = double.MinValue;
                var inputs = new List<INPUT>();
                if (_held.Remove(LeftWinId))
                {
                    // Left Command is remapped: release its replacement.
                    if (_map.TryGetValue(LeftWinId, out var m) && m.To is { } to)
                    {
                        inputs.Add(Key(to, up: true));
                    }
                }
                else
                {
                    _pending.Remove(LeftWinId);
                    inputs.Add(Key(new KeyCode(0, false, MaskKey), up: false));
                    inputs.Add(Key(new KeyCode(0, false, MaskKey), up: true));
                    inputs.Add(Key(LeftWin, up: true));
                }

                inputs.Add(Key(fKey, up: false));
                _swallowWinUp = true;
                _topRowHeld[0x1000 | k.scanCode] = fKey;
                Send(inputs);
                return true;
            }
        }

        return false;
    }

    /// <summary>Processes raw input that is already queued, from inside the hook.</summary>
    private void DrainRawInput()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, WmInput, WmInput, 1 /* PM_REMOVE */))
        {
            DispatchMessage(ref msg);
        }
    }

    private void ToggleTopRow()
    {
        _fKeys = !_fKeys;
        Log.Info($"Top row (Fn+Esc): {(_fKeys ? "F-keys" : "media keys")}");
        try
        {
            AppleTopRow.Write(_topRowStatePath!, _fKeys);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save the top row state", ex);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmInput)
        {
            try
            {
                OnRawInput(lParam);
            }
            catch (Exception ex)
            {
                Log.Error("Key remapper: raw input failed", ex);
            }
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void OnRawInput(IntPtr handle)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(handle, 0x10000003 /* RID_INPUT */, IntPtr.Zero, ref size, headerSize);
        if (size == 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(handle, 0x10000003, buffer, ref size, headerSize) != size)
            {
                return;
            }

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType == 2 && _topRowStatePath is not null) // RIM_TYPEHID: RAWHID { DWORD dwSizeHid; DWORD dwCount; BYTE bRawData[]; }
            {
                // Consumer-control report [report id, usage (1 or 2 bytes)]; usage 0 is the release.
                int sizeHid = Marshal.ReadInt32(buffer, (int)headerSize);
                if (sizeHid >= 2 && size >= headerSize + 8 + sizeHid && IsApple(header.hDevice))
                {
                    int usage = Marshal.ReadByte(buffer, (int)headerSize + 9);
                    if (sizeHid >= 3)
                    {
                        usage |= Marshal.ReadByte(buffer, (int)headerSize + 10) << 8;
                    }

                    if (usage != 0)
                    {
                        _appleMediaUsage = usage;
                        _appleMediaAt = NowMs;
                    }
                }

                return;
            }

            if (header.dwType != 1) // RIM_TYPEKEYBOARD
            {
                return;
            }

            var kb = Marshal.PtrToStructure<RAWKEYBOARD>(buffer + (int)headerSize);
            bool up = (kb.Flags & 1) != 0;
            if (kb.VKey == 0x13) // Pause (comes as E1 1D 45): the Apple driver sends it for Fn+Esc
            {
                if (!up && _topRowStatePath is not null && IsApple(header.hDevice))
                {
                    ToggleTopRow();
                }

                return;
            }

            if (kb.VKey == 0xFF || (kb.Flags & 4) != 0) // the rest of an E1 sequence, fake keys
            {
                return;
            }

            int id = (kb.MakeCode & 0xFF) | ((kb.Flags & 2) != 0 ? 0x100 : 0);
            if (!up && id == LeftWinId && IsApple(header.hDevice))
            {
                _appleWinDownAt = NowMs; // for the driver's Win+S / Win+C (see HandleTopRow)
            }

            if (up || !_pending.Remove(id, out long since) || Environment.TickCount64 - since > 500)
            {
                return;
            }

            if (!IsApple(header.hDevice) || !_map.TryGetValue(id, out var mapping))
            {
                return; // another keyboard: the original key stays as it is
            }

            // From the Apple keyboard: take the original back and press the replacement instead.
            var inputs = new List<INPUT>();
            if (mapping.From.IsWinOrAlt)
            {
                inputs.Add(Key(new KeyCode(0, false, MaskKey), up: false));
                inputs.Add(Key(new KeyCode(0, false, MaskKey), up: true));
            }

            inputs.Add(Key(mapping.From, up: true));
            if (mapping.From.VirtualKey == 0x14)
            {
                // Caps Lock toggled on the way down: toggle it back.
                inputs.Add(Key(mapping.From, up: false));
                inputs.Add(Key(mapping.From, up: true));
            }

            if (mapping.To is { } to)
            {
                inputs.Add(Key(to, up: false));
            }

            _held.Add(id);
            Send(inputs);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private bool IsApple(IntPtr device)
    {
        if (device == IntPtr.Zero)
        {
            return false; // injected input
        }

        if (_isApple.TryGetValue(device, out bool apple))
        {
            return apple;
        }

        uint chars = 0;
        GetRawInputDeviceInfo(device, 0x20000007 /* RIDI_DEVICENAME */, null, ref chars);
        var sb = new StringBuilder((int)chars + 1);
        GetRawInputDeviceInfo(device, 0x20000007, sb, ref chars);
        apple = AppleKeyboards.IsAppleDevice(sb.ToString());
        _isApple[device] = apple;
        return apple;
    }

    /// <summary>Releases replacement keys still held (e.g. when stopping), so no modifier stays stuck.</summary>
    private void ReleaseHeld()
    {
        _pending.Clear();
        _swallowWinUp = false;
        var inputs = _held
            .Select(id => _map.TryGetValue(id, out var m) ? m.To : null)
            .OfType<KeyCode>()
            .Concat(_topRowHeld.Values)
            .Select(to => Key(to, up: true))
            .ToList();

        // Keep the state if the keys could not be released (our desktop is not the active one): the next switch retries.
        if (inputs.Count == 0 || Send(inputs, quiet: true))
        {
            _held.Clear();
            _topRowHeld.Clear();
        }
    }

    private static INPUT Key(KeyCode key, bool up) => new()
    {
        type = 1, // INPUT_KEYBOARD
        ki = new KEYBDINPUT
        {
            wVk = key.VirtualKey,
            wScan = key.Scan,
            dwFlags = (up ? 0x0002u : 0) | (key.Extended ? 0x0001u : 0), // KEYEVENTF_KEYUP, KEYEVENTF_EXTENDEDKEY
            dwExtraInfo = Marker,
        },
    };

    private static bool Send(List<INPUT> inputs, bool quiet = false)
    {
        var array = inputs.ToArray();
        bool ok = SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>()) == array.Length;
        if (!ok && !quiet)
        {
            Log.Error($"Key remapper: SendInput failed (error {Marshal.GetLastWin32Error()})");
        }

        return ok;
    }

    #region P/Invoke

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, ExtraInformation; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }

    // INPUT is a union of MOUSEINPUT (the largest: 32 bytes on x64), KEYBDINPUT and HARDWAREINPUT after the type field.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder? data, ref uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    #endregion
}
