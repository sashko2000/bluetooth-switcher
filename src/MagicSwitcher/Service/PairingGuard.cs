using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MagicSwitcher.Bluetooth;
using MagicSwitcher.Infrastructure;

namespace MagicSwitcher.Service;

/// <summary>
/// Keeps the selected devices (keyboards) working on this computer, including at the sign-in screen.
/// <para>
/// A multi-host keyboard like Apple Magic Keyboard keeps the key of one host only: once it was used with
/// another device, it rejects this PC and has to be paired again. The guard does that without the Windows
/// dialog: it answers the pairing request itself (only for the selected devices and only while a "session"
/// is running), pairs with bonding so the key is stored, and enables the HID service.
/// </para>
/// <para>
/// A session starts on an event (service start, sign-in, unlock, wake, display on, a request from the tray).
/// It first gives the device time to connect by itself (knocking right after boot breaks a connection that is
/// coming up and makes Windows drop the key), then "knocks" periodically until the device works or time is up.
/// The guard never knocks outside a session: when the user hands the keyboard to a phone on purpose,
/// knocking would take it back or break the pairing.
/// </para>
/// </summary>
internal sealed class PairingGuard : IDisposable
{
    private const uint WmQuit = 0x0012;
    private const uint WmApp = 0x8000;

    private readonly Thread _loop;
    private uint _loopThreadId;
    private readonly ManualResetEventSlim _loopReady = new();
    private readonly ConcurrentQueue<Action> _loopActions = new();
    private readonly Dictionary<ulong, (NativeBluetooth.PairingRegistration Registration, int Count)> _registrations = []; // loop thread only

    private readonly Lock _sync = new();
    private readonly Dictionary<ulong, Session> _sessions = [];
    private IReadOnlyList<GuardedDevice> _devices = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly System.Threading.Timer _stateTimer;
    private readonly Dictionary<ulong, string> _lastStates = []; // state timer only
    private int _loggingStates;

    public PairingGuard()
    {
        _stateTimer = new System.Threading.Timer(_ => LogStateChanges(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        // Pairing callbacks are delivered through the message queue of the thread that registered them.
        _loop = new Thread(RunLoop) { IsBackground = true, Name = "Pairing message loop" };
        _loop.SetApartmentState(ApartmentState.STA);
        _loop.Start();
        _loopReady.Wait();
    }

    /// <summary>
    /// Called after a guarded device was paired again. Windows then installs the keyboard anew, and the Apple driver
    /// resets its top row setting, so the service re-applies it.
    /// </summary>
    public Action? AfterPairing { get; set; }

    public IReadOnlyList<GuardedDevice> Devices
    {
        get { lock (_sync) { return _devices; } }
        set { lock (_sync) { _devices = value; } }
    }

    /// <summary>Starts a session for every guarded device that is not working yet (no-op if one is running).</summary>
    public void Trigger(ServiceTrigger trigger)
    {
        var plan = Plan.For(trigger);
        foreach (var device in Devices)
        {
            var state = NativeBluetooth.GetState(device.Address);
            string hid = DescribeHid(device, state);
            lock (_sync)
            {
                if (_sessions.ContainsKey(device.Address))
                {
                    Log.Info($"[Guard] {trigger}: '{Name(device)}' {state}, {hid}: already watching");
                    continue;
                }

                if (state.IsWorking)
                {
                    Log.Info($"[Guard] {trigger}: '{Name(device)}' {state}, {hid}: works, nothing to do");
                    continue;
                }

                Log.Info($"[Guard] {trigger}: '{Name(device)}' is not working ({state}, {hid}): watching for {plan.Duration.TotalMinutes:0} min");
                StartSession(device, plan, trigger.ToString());
            }
        }
    }

    /// <summary>
    /// Brings the device to this PC now (the tray's "Connect"): knocks right away and every 10 s.
    /// Returns true when it works. Joins a session that is already running.
    /// </summary>
    public Task<bool> PullAsync(GuardedDevice device, TimeSpan duration, bool force, Action<string>? progress)
    {
        lock (_sync)
        {
            if (_sessions.TryGetValue(device.Address, out var running))
            {
                running.Expedite(duration, progress);
                return running.Completion;
            }

            var state = NativeBluetooth.GetState(device.Address);
            if (state.IsWorking && !force)
            {
                return Task.FromResult(true);
            }

            Log.Info($"[Guard] Request: connecting '{Name(device)}' ({state}) for up to {duration.TotalSeconds:0} s");
            return StartSession(device, Plan.Request(duration), "Request", progress, force).Completion;
        }
    }

    private Session StartSession(GuardedDevice device, Plan plan, string reason, Action<string>? progress = null, bool forceFirstKnock = false)
    {
        var session = new Session(device, plan, progress) { ForceFirstKnock = forceFirstKnock };
        _sessions[device.Address] = session;
        _ = Task.Run(async () =>
        {
            bool working = false;
            try
            {
                await RunOnLoopAsync(() => AddRegistration(device.Address));
                working = await RunSessionAsync(session, _stopping.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error($"[Guard] Session for '{Name(device)}' failed", ex);
            }
            finally
            {
                lock (_sync)
                {
                    _sessions.Remove(device.Address);
                }

                try
                {
                    await RunOnLoopAsync(() => RemoveRegistration(device.Address));
                }
                catch (Exception ex)
                {
                    Log.Error("[Guard] Failed to unregister", ex);
                }

                Log.Info($"[Guard] {reason}: '{Name(device)}' {(working ? "works" : "still not working, stopped watching")}: {NativeBluetooth.GetState(device.Address)}");
                session.Complete(working);
            }
        });
        return session;
    }

    /// <summary>
    /// Knocks until the device works and keeps working for <see cref="Plan.StableFor"/>. Right after startup a keyboard
    /// may connect and drop again a few seconds later (Windows then even removes the pairing), so one "works" is not enough.
    /// </summary>
    private async Task<bool> RunSessionAsync(Session session, CancellationToken ct)
    {
        var device = session.Device;
        int knocks = 0;
        DateTime? workingSince = null;
        while (DateTime.UtcNow < session.Deadline || workingSince is not null)
        {
            ct.ThrowIfCancellationRequested();
            var state = NativeBluetooth.GetState(device.Address);
            if (state.IsWorking && !(session.ForceFirstKnock && knocks == 0))
            {
                workingSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - workingSince >= session.Plan.StableFor)
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }

            if (workingSince is not null)
            {
                // It worked and dropped again. Without a pairing, pair right away; with one, give it a moment to
                // reconnect by itself first (knocking a connection that is coming up breaks it).
                workingSince = null;
                session.NextKnock = DateTime.UtcNow + (state.Known && state.Paired ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
                Log.Info($"[Guard] '{Name(device)}' stopped working again ({state}): keep restoring");
            }

            if (DateTime.UtcNow >= session.NextKnock)
            {
                knocks++;
                session.Report(knocks == 1 ? "Connecting…" : $"Connecting… (attempt {knocks})");
                if (await Task.Run(() => Knock(device, state, knocks), ct))
                {
                    AfterPairing?.Invoke();
                }

                session.NextKnock = DateTime.UtcNow + session.Plan.Interval;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        return NativeBluetooth.GetState(device.Address).IsWorking;
    }

    /// <summary>One attempt (tested with Apple Magic Keyboard, see tools/PairingProbe in the history).</summary>
    /// <summary>Returns true if the device was paired again.</summary>
    private static bool Knock(GuardedDevice device, DeviceState state, int attempt)
    {
        string name = Name(device);
        if (state.Known && state.Paired)
        {
            // Windows has the key but the device is not connected: reinstalling HID makes Windows connect.
            // If the device was used with another host, it rejects the key and Windows removes the device;
            // the next attempt then pairs again.
            int error = NativeBluetooth.EnableServices(device.Address, device.Services);
            Log.Info($"[Guard] '{name}' knock {attempt} ({state}): services reinstalled -> {error}");
            return false;
        }

        // No key (or Windows forgot the device): pair with bonding; our callback confirms the request.
        int pairError = NativeBluetooth.PairWithBonding(device.Address);
        Log.Info($"[Guard] '{name}' knock {attempt} ({state}): pairing -> {pairError}" +
                 (pairError == NativeBluetooth.ErrorTimeout ? " (no answer: the device is asleep, off or busy with another host)" : ""));
        if (pairError == NativeBluetooth.ErrorSuccess)
        {
            // Pairing stores the key but does not install the HID service; without it the keyboard cannot connect.
            Thread.Sleep(1000);
            int error = NativeBluetooth.EnableServices(device.Address, device.Services);
            Log.Info($"[Guard] '{name}' services enabled after pairing -> {error}: {NativeBluetooth.GetState(device.Address)}");
            return true;
        }

        return false;
    }

    /// <summary>Called on the message loop thread when a guarded device asks to pair.</summary>
    private void OnPairingRequest(NativeBluetooth.PairingRequest request)
    {
        bool accept;
        lock (_sync)
        {
            accept = _sessions.ContainsKey(request.Address);
        }

        int error = NativeBluetooth.AnswerPairingRequest(request, accept);
        Log.Info($"[Guard] Pairing request from {BluetoothAddress.Format(request.Address)} '{request.Name}': {request.Method}, " +
                 $"{request.Requirements} -> {(accept ? "accepted" : "rejected (no session)")}, error {error}" +
                 (accept && request.WithoutBonding ? " (without bonding: the key is not kept, the next knock pairs with bonding)" : ""));
        if (accept && error == 0 && !request.WithoutBonding)
        {
            AfterPairing?.Invoke();
        }
    }

    private void AddRegistration(ulong address)
    {
        if (_registrations.TryGetValue(address, out var entry))
        {
            _registrations[address] = (entry.Registration, entry.Count + 1);
            return;
        }

        var registration = NativeBluetooth.RegisterForPairing(address, OnPairingRequest);
        _registrations[address] = (registration, 1);
    }

    private void RemoveRegistration(ulong address)
    {
        if (!_registrations.TryGetValue(address, out var entry))
        {
            return;
        }

        if (entry.Count > 1)
        {
            _registrations[address] = (entry.Registration, entry.Count - 1);
            return;
        }

        entry.Registration.Dispose();
        _registrations.Remove(address);
    }

    private Task RunOnLoopAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loopActions.Enqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        PostThreadMessage(_loopThreadId, WmApp, IntPtr.Zero, IntPtr.Zero);
        return tcs.Task;
    }

    private void RunLoop()
    {
        _loopThreadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // creates the thread's message queue
        _loopReady.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.hwnd == IntPtr.Zero && msg.message == WmApp)
            {
                while (_loopActions.TryDequeue(out var action))
                {
                    action();
                }

                continue;
            }

            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        foreach (var (registration, _) in _registrations.Values)
        {
            registration.Dispose();
        }

        _registrations.Clear();
    }

    public void Dispose()
    {
        _stateTimer.Dispose();
        _stopping.Cancel();
        PostThreadMessage(_loopThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _loop.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Writes every change of a guarded device's state to the log (checked every 5 seconds), so it is clear later
    /// what happened between events, e.g. on the sign-in screen.
    /// </summary>
    private void LogStateChanges()
    {
        if (Interlocked.Exchange(ref _loggingStates, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var device in Devices)
            {
                var state = NativeBluetooth.GetState(device.Address);
                string text = state.ToString();
                if (_lastStates.TryGetValue(device.Address, out var last) && last == text)
                {
                    continue;
                }

                _lastStates[device.Address] = text;
                Log.Info($"[Guard] '{Name(device)}' state: {text}, {DescribeHid(device, state)}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("[Guard] State check failed", ex);
        }
        finally
        {
            Volatile.Write(ref _loggingStates, 0);
        }
    }

    /// <summary>How many HID collections Windows has for the device: none while it is connected means it does not type.</summary>
    private static string DescribeHid(GuardedDevice device, DeviceState state)
    {
        if (!state.Connected)
        {
            return "HID -";
        }

        try
        {
            return CountHid(device) is { } count ? $"HID collections {count}" : "HID ? (no container)";
        }
        catch (Exception ex)
        {
            return $"HID ? ({ex.GetType().Name})";
        }
    }

    /// <summary>Number of HID collections of the device; null if its PnP container is not found. Blocking.</summary>
    public static int? CountHid(GuardedDevice device)
    {
        var saved = new SavedDevice { Address = device.Address, Name = device.Name };
        var containerId = BatteryReader.TryGetContainerIdAsync(saved).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        return containerId is { } id
            ? HidNative.FindCollectionPathsAsync(id).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult().Count
            : null;
    }

    private static string Name(GuardedDevice device) =>
        string.IsNullOrWhiteSpace(device.Name) ? BluetoothAddress.Format(device.Address) : device.Name;

    /// <summary>How long to wait before the first knock, how often to knock and when to give up.</summary>
    /// <summary>
    /// How long to wait before the first knock, how often to knock, when to give up,
    /// and how long the device must keep working before the session ends.
    /// </summary>
    private sealed record Plan(TimeSpan InitialWait, TimeSpan Interval, TimeSpan Duration, TimeSpan StableFor)
    {
        public static Plan For(ServiceTrigger trigger) => trigger switch
        {
            // After boot or wake the keyboard reconnects by itself within seconds: do not disturb it.
            ServiceTrigger.Startup or ServiceTrigger.Resume =>
                new(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(60)),
            _ => new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60)),
        };

        /// <summary>The tray waits for the answer, so it only needs a short confirmation.</summary>
        public static Plan Request(TimeSpan duration) => new(TimeSpan.Zero, TimeSpan.FromSeconds(10), duration, TimeSpan.FromSeconds(5));
    }

    private sealed class Session(GuardedDevice device, Plan plan, Action<string>? progress)
    {
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<string>? _progress = progress;

        public GuardedDevice Device { get; } = device;
        public Plan Plan { get; private set; } = plan;
        public DateTime Deadline { get; private set; } = DateTime.UtcNow + plan.Duration;
        public DateTime NextKnock { get; set; } = DateTime.UtcNow + plan.InitialWait;

        /// <summary>Knock once even if Windows reports the device as working (the tray says it does not type).</summary>
        public bool ForceFirstKnock { get; set; }
        public Task<bool> Completion => _completion.Task;

        /// <summary>A request from the tray joins the session: knock now and keep going for at least <paramref name="duration"/>.</summary>
        public void Expedite(TimeSpan duration, Action<string>? progress)
        {
            Plan = Plan with { Interval = TimeSpan.FromSeconds(10), StableFor = TimeSpan.FromSeconds(5) };
            NextKnock = DateTime.UtcNow;
            var deadline = DateTime.UtcNow + duration;
            if (deadline > Deadline)
            {
                Deadline = deadline;
            }

            _progress = progress ?? _progress;
        }

        public void Report(string text) => _progress?.Invoke(text);

        public void Complete(bool working) => _completion.TrySetResult(working);
    }

    #region P/Invoke (message loop)

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    #endregion
}
