using System.Runtime.InteropServices;
using BluetoothSwitcher.Infrastructure;

namespace BluetoothSwitcher.UI;

/// <summary>
/// Window messages between the shell (Explorer), other instances of the app and the running instance.
/// <para>
/// When the app runs elevated (started by the scheduled task), Windows' UIPI blocks window messages from
/// non-elevated processes — including Explorer's "TaskbarCreated" broadcast, which NotifyIcon relies on to add its
/// icon again once the taskbar exists. Without it, an app started at logon before the taskbar never shows its icon.
/// </para>
/// </summary>
internal static class ShellMessages
{
    /// <summary>Broadcast by Explorer when the taskbar is created (at logon or after Explorer restarts).</summary>
    public static readonly int TaskbarCreated = (int)RegisterWindowMessage("TaskbarCreated");

    /// <summary>Broadcast by a second start of the app: the running instance shows its icon and the settings.</summary>
    public static readonly int ShowInstance = (int)RegisterWindowMessage("BluetoothSwitcher.ShowInstance");

    /// <summary>Lets the messages above reach this process even when it runs elevated.</summary>
    public static void AllowFromNonElevatedProcesses()
    {
        bool taskbar = ChangeWindowMessageFilter((uint)TaskbarCreated, MsgfltAdd);
        bool show = ChangeWindowMessageFilter((uint)ShowInstance, MsgfltAdd);
        Log.Info($"Window message filter: TaskbarCreated {taskbar}, ShowInstance {show}");
    }

    /// <summary>True once Explorer's taskbar window exists.</summary>
    public static bool IsTaskbarReady => FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;

    /// <summary>Asks the already running instance to show itself.</summary>
    public static void BroadcastShowInstance() =>
        PostMessage(HwndBroadcast, (uint)ShowInstance, IntPtr.Zero, IntPtr.Zero);

    private const uint MsgfltAdd = 1;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>
/// Hidden top-level window that receives the broadcasts above
/// (message-only windows do not receive broadcasts, so this is a regular, never shown window).
/// </summary>
internal sealed class ShellMessageWindow : NativeWindow, IDisposable
{
    public event EventHandler? TaskbarCreated;
    public event EventHandler? ShowRequested;

    public ShellMessageWindow()
    {
        CreateHandle(new CreateParams { Caption = "BluetoothSwitcher.ShellMessages" });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == ShellMessages.TaskbarCreated)
        {
            TaskbarCreated?.Invoke(this, EventArgs.Empty);
        }
        else if (m.Msg == ShellMessages.ShowInstance)
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }

        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}
