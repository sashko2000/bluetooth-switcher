// InputProbe: logs every key seen by a low-level keyboard hook (WH_KEYBOARD_LL) and by raw input (WM_INPUT, with the
// device it came from), in arrival order. With --block-rwin the hook swallows the right Windows key, to see whether
// raw input still reports it. Press keys on each keyboard, then Ctrl+C. Log: InputProbe.log next to the exe.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Dictionary<IntPtr, string> DeviceNames = [];
    private static StreamWriter? _log;
    private static bool _blockRightWin;
    private static HookProc? _hookProc;
    private static WndProc? _wndProc;

    private static int Main(string[] args)
    {
        _blockRightWin = args.Contains("--block-rwin", StringComparer.OrdinalIgnoreCase);
        string logPath = Path.Combine(AppContext.BaseDirectory, "InputProbe.log");
        _log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        Write($"InputProbe started. Block right Win in the hook: {_blockRightWin}. Elevated: {IsElevated()}");
        Write("Press the keys to examine. Ctrl+C to stop.");

        // Hidden message-only window that receives raw input even when it is not focused (RIDEV_INPUTSINK).
        _wndProc = WindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "InputProbeWindow",
        };
        if (RegisterClassEx(ref wc) == 0)
        {
            Write($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            return 1;
        }

        var hwnd = CreateWindowEx(0, "InputProbeWindow", "InputProbe", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        // Keyboards, plus every HID collection on the consumer-control page (media keys) and Apple's vendor pages
        // (RIDEV_PAGEONLY): to see where the top-row keys that are not keyboard keys come from, and when.
        const uint Sink = 0x00000100, PageOnly = 0x00000020;
        RAWINPUTDEVICE[] devices =
        [
            new() { usUsagePage = 0x01, usUsage = 0x06, dwFlags = Sink, hwndTarget = hwnd },
            new() { usUsagePage = 0x0C, usUsage = 0, dwFlags = Sink | PageOnly, hwndTarget = hwnd },
            new() { usUsagePage = 0xFF00, usUsage = 0, dwFlags = Sink | PageOnly, hwndTarget = hwnd },
            new() { usUsagePage = 0xFF01, usUsage = 0, dwFlags = Sink | PageOnly, hwndTarget = hwnd },
        ];
        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            Write($"RegisterRawInputDevices failed: {Marshal.GetLastWin32Error()}");
            return 1;
        }

        _hookProc = HookCallback;
        var hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _hookProc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            Write($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
            return 1;
        }

        uint mainThread = GetCurrentThreadId();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; PostThreadMessage(mainThread, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero); };
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(hook);
        Write($"Stopped. Log: {logPath}");
        return 0;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool up = (k.flags & 0x80) != 0;
            bool injected = (k.flags & 0x10) != 0;
            bool block = _blockRightWin && k.vkCode == 0x5C; // VK_RWIN
            Write($"HOOK  {(up ? "up  " : "down")} vk {k.vkCode:X2} scan {k.scanCode:X2}{((k.flags & 1) != 0 ? " E0" : "")}" +
                  $"{(injected ? " injected" : "")}{(block ? "  -> BLOCKED" : "")}");
            if (block)
            {
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == 0x00FF) // WM_INPUT
        {
            uint size = 0;
            GetRawInputData(lParam, 0x10000003 /* RID_INPUT */, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lParam, 0x10000003, buffer, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) == size)
                {
                    var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
                    if (header.dwType == 1) // RIM_TYPEKEYBOARD
                    {
                        var kb = Marshal.PtrToStructure<RAWKEYBOARD>(buffer + Marshal.SizeOf<RAWINPUTHEADER>());
                        bool up = (kb.Flags & 1) != 0;
                        Write($"RAW   {(up ? "up  " : "down")} vk {kb.VKey:X2} scan {kb.MakeCode:X2}{((kb.Flags & 2) != 0 ? " E0" : "")}  device {DeviceName(header.hDevice)}");
                    }
                    else if (header.dwType == 2) // RIM_TYPEHID: RAWHID { DWORD dwSizeHid; DWORD dwCount; BYTE bRawData[]; }
                    {
                        int offset = Marshal.SizeOf<RAWINPUTHEADER>();
                        int sizeHid = Marshal.ReadInt32(buffer, offset);
                        int count = Marshal.ReadInt32(buffer, offset + 4);
                        var data = new byte[Math.Max(0, Math.Min(sizeHid * count, (int)size - offset - 8))];
                        Marshal.Copy(buffer + offset + 8, data, 0, data.Length);
                        Write($"HID   report {Convert.ToHexString(data)}  device {DeviceName(header.hDevice)}");
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static string DeviceName(IntPtr device)
    {
        if (device == IntPtr.Zero)
        {
            return "(none: injected)";
        }

        if (DeviceNames.TryGetValue(device, out var name))
        {
            return name;
        }

        uint chars = 0;
        GetRawInputDeviceInfo(device, 0x20000007 /* RIDI_DEVICENAME */, null, ref chars);
        var sb = new StringBuilder((int)chars + 1);
        GetRawInputDeviceInfo(device, 0x20000007, sb, ref chars);
        string full = sb.ToString();
        // Keep the interesting part, e.g. HID#{00001124-...}_VID&0001004c_PID&029c&Col01 or HID#VID_xxxx&PID_xxxx
        name = full.Length > 70 ? full[..70] + "…" : full;
        DeviceNames[device] = name;
        return name;
    }

    private static void Write(string text)
    {
        string line = $"{Clock.Elapsed.TotalMilliseconds,10:F1} ms  {text}";
        Console.WriteLine(line);
        _log?.WriteLine(line);
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    #region P/Invoke

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, ExtraInformation; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

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
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    #endregion
}
