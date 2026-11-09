using System.Runtime.InteropServices;
using MagicSwitcher.Infrastructure;

namespace MagicSwitcher.Bluetooth;

/// <summary>Device state as Windows sees it (Win32 BLUETOOTH_DEVICE_INFO).</summary>
internal readonly record struct DeviceState(bool Known, bool Connected, bool Remembered, bool Paired, string Name)
{
    /// <summary>Connected and paired: the keyboard can type (Windows keeps HID up by itself).</summary>
    public bool IsWorking => Known && Connected && Paired;

    public override string ToString() => Known
        ? $"connected {Connected}, remembered {Remembered}, paired {Paired}"
        : "unknown to Windows";
}

/// <summary>
/// Pairing without the Windows dialog (used by the service):
/// - <see cref="RegisterForPairing"/> subscribes to pairing requests from one device; the callback answers them,
///   so Windows does not show its own dialog. The registering thread must run a message loop.
/// - <see cref="PairWithBonding"/> starts a pairing from this computer and asks to store the key
///   (a pairing the device starts itself may come without bonding, and then the key is not kept).
/// - <see cref="EnableServices"/> installs the profile drivers: pairing alone does not do that,
///   and without the HID service a freshly paired keyboard cannot connect.
/// </summary>
internal static partial class NativeBluetooth
{
    public const int ErrorTimeout = 258; // WAIT_TIMEOUT: the device did not answer (asleep or connected elsewhere)

    public static DeviceState GetState(ulong address)
    {
        var radio = OpenFirstRadio();
        if (radio == IntPtr.Zero)
        {
            return default;
        }

        try
        {
            var info = CreateDeviceInfo(address);
            return BluetoothGetDeviceInfo(radio, ref info) == ErrorSuccess
                ? new DeviceState(true, info.fConnected, info.fRemembered, info.fAuthenticated, info.szName)
                : default;
        }
        finally
        {
            CloseHandle(radio);
        }
    }

    /// <summary>Pairs with the device and stores the key. Works even if Windows has forgotten the device. Returns a Win32 error.</summary>
    public static int PairWithBonding(ulong address)
    {
        var radio = OpenFirstRadio();
        if (radio == IntPtr.Zero)
        {
            return ErrorNoRadio;
        }

        try
        {
            var info = CreateDeviceInfo(address);
            BluetoothGetDeviceInfo(radio, ref info); // fills the name if Windows knows the device; the address alone is enough
            return BluetoothAuthenticateDeviceEx(IntPtr.Zero, radio, ref info, IntPtr.Zero, AuthRequirements.MitmNotRequiredGeneralBonding);
        }
        finally
        {
            CloseHandle(radio);
        }
    }

    /// <summary>Disables and enables the given services (default: HID). Returns the last Win32 error, 0 on success.</summary>
    public static int EnableServices(ulong address, IReadOnlyCollection<Guid>? services)
    {
        var radio = OpenFirstRadio();
        if (radio == IntPtr.Zero)
        {
            return ErrorNoRadio;
        }

        try
        {
            var info = CreateDeviceInfo(address);
            int error = BluetoothGetDeviceInfo(radio, ref info);
            if (error != ErrorSuccess)
            {
                return error;
            }

            Guid[] targets = services is { Count: > 0 } ? services.ToArray() : [HidServiceClass];
            int result = ErrorSuccess;
            foreach (var service in targets)
            {
                var guid = service;
                BluetoothSetServiceState(radio, ref info, ref guid, BluetoothServiceDisable); // may already be disabled
            }

            Thread.Sleep(1000);
            foreach (var service in targets)
            {
                var guid = service;
                error = BluetoothSetServiceState(radio, ref info, ref guid, BluetoothServiceEnable);
                if (error != ErrorSuccess)
                {
                    result = error;
                }
            }

            return result;
        }
        finally
        {
            CloseHandle(radio);
        }
    }

    /// <summary>
    /// Subscribes to pairing requests from the device; every request is passed to <paramref name="onRequest"/>,
    /// which must answer it with <see cref="AnswerPairingRequest"/>. Must be called on a thread with a message loop.
    /// Returns the registration handle (dispose to unsubscribe) or throws on failure.
    /// </summary>
    public static PairingRegistration RegisterForPairing(ulong address, Action<PairingRequest> onRequest)
    {
        var info = CreateDeviceInfo(address);
        AuthenticationCallback callback = (IntPtr _, ref BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS p) =>
        {
            try
            {
                onRequest(new PairingRequest(
                    p.deviceInfo.Address, p.deviceInfo.szName, p.authenticationMethod, p.ioCapability,
                    p.authenticationRequirements, p.NumericValueOrPasskey));
            }
            catch (Exception ex)
            {
                Log.Error("Pairing callback failed", ex);
            }

            return true;
        };

        int error = BluetoothRegisterForAuthenticationEx(ref info, out var handle, callback, IntPtr.Zero);
        if (error != ErrorSuccess)
        {
            throw new System.ComponentModel.Win32Exception(error);
        }

        return new PairingRegistration(handle, callback);
    }

    /// <summary>Confirms (or rejects) a pairing request. Returns a Win32 error.</summary>
    public static int AnswerPairingRequest(PairingRequest request, bool accept)
    {
        var response = new BLUETOOTH_AUTHENTICATE_RESPONSE
        {
            bthAddressRemote = request.Address,
            authMethod = request.Method,
            negativeResponse = (byte)(accept ? 0 : 1),
        };

        switch (request.Method)
        {
            case AuthMethod.NumericComparison: // "Just Works" also comes as numeric comparison
            case AuthMethod.PasskeyNotification:
                response.numericOrPasskey = request.NumericValueOrPasskey;
                break;
            case AuthMethod.Legacy:
                response.SetPin("0000");
                break;
            default:
                response.negativeResponse = 1; // OOB / passkey entry: cannot be answered without a user
                break;
        }

        return BluetoothSendAuthenticationResponseEx(IntPtr.Zero, ref response);
    }

    public sealed class PairingRegistration : IDisposable
    {
        private IntPtr _handle;

        // Keeps the delegate alive while Windows holds a pointer to it.
        private readonly object _callback;

        internal PairingRegistration(IntPtr handle, object callback)
        {
            _handle = handle;
            _callback = callback;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                BluetoothUnregisterAuthentication(_handle);
                _handle = IntPtr.Zero;
                GC.KeepAlive(_callback);
            }
        }
    }

    public readonly record struct PairingRequest(
        ulong Address,
        string Name,
        AuthMethod Method,
        IoCapability IoCapability,
        AuthRequirements Requirements,
        uint NumericValueOrPasskey)
    {
        public bool WithoutBonding => Requirements is AuthRequirements.MitmNotRequiredNoBonding or AuthRequirements.MitmRequiredNoBonding;
    }

    #region P/Invoke (pairing)

    public enum AuthMethod
    {
        Legacy = 1,
        Oob = 2,
        NumericComparison = 3,
        PasskeyNotification = 4,
        Passkey = 5,
    }

    public enum IoCapability
    {
        DisplayOnly = 0,
        DisplayYesNo = 1,
        KeyboardOnly = 2,
        NoInputNoOutput = 3,
        Undefined = 0xFF,
    }

    public enum AuthRequirements
    {
        MitmNotRequired = 0,
        MitmRequired = 1,
        MitmNotRequiredNoBonding = 2,
        MitmRequiredNoBonding = 3,
        MitmNotRequiredGeneralBonding = 4,
        MitmRequiredGeneralBonding = 5,
        MitmNotRequiredDedicatedBonding = 6,
        MitmRequiredDedicatedBonding = 7,
        NotDefined = 0xFF,
    }

    // BLUETOOTH_DEVICE_INFO deviceInfo; three enums; union { ULONG Numeric_Value; ULONG Passkey; } — 576 bytes on x64.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS
    {
        public BLUETOOTH_DEVICE_INFO deviceInfo;
        public AuthMethod authenticationMethod;
        public IoCapability ioCapability;
        public AuthRequirements authenticationRequirements;
        public uint NumericValueOrPasskey;
    }

    // BLUETOOTH_ADDRESS (8) + method (4) + union (max 32: OOB C[16] R[16]) + UCHAR negativeResponse, padded to 48.
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private unsafe struct BLUETOOTH_AUTHENTICATE_RESPONSE
    {
        [FieldOffset(0)] public ulong bthAddressRemote;
        [FieldOffset(8)] public AuthMethod authMethod;
        [FieldOffset(12)] public uint numericOrPasskey; // numericCompInfo.NumericValue / passkeyInfo.passkey
        [FieldOffset(12)] public fixed byte pin[16];     // pinInfo.pin
        [FieldOffset(28)] public byte pinLength;         // pinInfo.pinLength
        [FieldOffset(44)] public byte negativeResponse;

        public void SetPin(string value)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(value);
            for (int i = 0; i < bytes.Length && i < 16; i++)
            {
                pin[i] = bytes[i];
            }

            pinLength = (byte)Math.Min(bytes.Length, 16);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool AuthenticationCallback(IntPtr pvParam, ref BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS pAuthCallbackParams);

    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)]
    private static extern int BluetoothRegisterForAuthenticationEx(
        ref BLUETOOTH_DEVICE_INFO pbtdiIn, out IntPtr phRegHandleOut, AuthenticationCallback pfnCallbackIn, IntPtr pvParam);

    [DllImport("bthprops.cpl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothUnregisterAuthentication(IntPtr hRegHandle);

    [DllImport("bthprops.cpl")]
    private static extern int BluetoothSendAuthenticationResponseEx(IntPtr hRadioIn, ref BLUETOOTH_AUTHENTICATE_RESPONSE pauthResponse);

    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)]
    private static extern int BluetoothAuthenticateDeviceEx(
        IntPtr hwndParentIn, IntPtr hRadioIn, ref BLUETOOTH_DEVICE_INFO pbtdiInout, IntPtr pbtOobData, AuthRequirements authenticationRequirement);

    #endregion
}
