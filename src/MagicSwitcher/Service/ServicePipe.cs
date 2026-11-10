using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MagicSwitcher.Infrastructure;

namespace MagicSwitcher.Service;

/// <summary>Commands the tray app sends to the service.</summary>
internal static class ServiceCommands
{
    public const string Ping = "ping";
    /// <summary>Replaces the list of devices the service restores (<see cref="ServiceRequest.Devices"/>).</summary>
    public const string Configure = "configure";
    /// <summary>Reinstalls services of a Classic device (same as NativeBluetooth.ReinstallServices).</summary>
    public const string Reinstall = "reinstall";
    /// <summary>Brings a device to this PC now: pairs again if needed (no Windows dialog). Waits up to <see cref="ServiceRequest.Seconds"/>.</summary>
    public const string Pull = "pull";
    public const string AppleInstall = "apple-install";
    public const string AppleFn = "apple-fn";
    /// <summary>Restarts the Windows audio services.</summary>
    public const string RestartAudio = "restart-audio";
}

/// <summary>A device the service keeps working: restored at startup, sign-in, unlock, wake and on request.</summary>
public sealed record GuardedDevice
{
    public ulong Address { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Services enabled after pairing; null means HID.</summary>
    public List<Guid>? Services { get; init; }
}

internal sealed class ServiceRequest
{
    public string Command { get; set; } = "";
    public ulong Address { get; set; }
    public List<Guid>? Services { get; set; }
    public bool Full { get; set; }
    public int Seconds { get; set; }
    public bool Media { get; set; }
    /// <summary>Pull: knock at least once even if Windows reports the device as connected (it may be stale).</summary>
    public bool Force { get; set; }
    public List<GuardedDevice>? Devices { get; set; }
    /// <summary>Configure: the Apple keyboard top row behavior the user chose (null: unknown, keep the service's).</summary>
    public bool? AppleMediaKeys { get; set; }
}

internal sealed class ServiceResponse
{
    public bool Ok { get; set; }
    /// <summary>Win32 error code or a command-specific result code.</summary>
    public int Code { get; set; }
    public string? Message { get; set; }
    /// <summary>Intermediate progress line (the final response has <see cref="Done"/> = true).</summary>
    public string? Progress { get; set; }
    public bool Done { get; set; } = true;
    public string? Version { get; set; }

    public static ServiceResponse Success(int code = 0, string? message = null) => new() { Ok = true, Code = code, Message = message };

    public static ServiceResponse Failure(string message, int code = -1) => new() { Ok = false, Code = code, Message = message };
}

/// <summary>
/// Named pipe between the tray app and the service: one JSON request line per connection,
/// answered by zero or more progress lines and a final line.
/// Only local interactive users (and administrators/SYSTEM) may connect; network clients are denied.
/// </summary>
internal static class ServicePipe
{
    public const string PipeName = "MagicSwitcher.Service";

    private static readonly JsonSerializerOptions Json = new();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? Deserialize<T>(string line) => JsonSerializer.Deserialize<T>(line, Json);

    /// <summary>Accepts connections until <paramref name="ct"/> is cancelled.</summary>
    public static async Task ServeAsync(Func<ServiceRequest, Action<string>, CancellationToken, Task<ServiceResponse>> handler, CancellationToken ct)
    {
        var security = CreateSecurity();
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Pipe: failed to accept a connection", ex);
                await Task.Delay(1000, ct).ContinueWith(_ => { }, TaskScheduler.Default);
                continue;
            }

            _ = HandleAsync(server, handler, ct);
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream server,
        Func<ServiceRequest, Action<string>, CancellationToken, Task<ServiceResponse>> handler, CancellationToken ct)
    {
        await using (server)
        {
            try
            {
                using var reader = new StreamReader(server, new UTF8Encoding(false), leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                string? line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
                var request = line is null ? null : Deserialize<ServiceRequest>(line);
                if (request is null)
                {
                    return;
                }

                var writeLock = new SemaphoreSlim(1, 1);
                void Progress(string text)
                {
                    writeLock.Wait(ct);
                    try
                    {
                        writer.WriteLine(Serialize(new ServiceResponse { Ok = true, Done = false, Progress = text }));
                    }
                    catch (IOException)
                    {
                        // The client went away; the command still finishes.
                    }
                    finally
                    {
                        writeLock.Release();
                    }
                }

                ServiceResponse response;
                try
                {
                    response = await handler(request, Progress, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error($"Pipe: command '{request.Command}' failed", ex);
                    response = ServiceResponse.Failure(ex.Message);
                }

                await writeLock.WaitAsync(ct);
                await writer.WriteLineAsync(Serialize(response));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or JsonException)
            {
                // Client disconnected, sent garbage, or the service is stopping.
            }
        }
    }

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    /// <summary>
    /// Sends a request and waits for the final response. Returns null if the service is not running
    /// (no connection within <paramref name="connectTimeout"/>).
    /// </summary>
    public static async Task<ServiceResponse?> SendAsync(ServiceRequest request, TimeSpan timeout, IProgress<string>? progress = null,
        TimeSpan? connectTimeout = null, CancellationToken ct = default)
    {
        await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(connectTimeout ?? TimeSpan.FromSeconds(2), ct);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var reader = new StreamReader(client, new UTF8Encoding(false), leaveOpen: true);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(Serialize(request).AsMemory(), cts.Token);

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"The service did not answer '{request.Command}' in time.");
            }

            if (line is null)
            {
                throw new IOException("The service closed the connection.");
            }

            var response = Deserialize<ServiceResponse>(line) ?? throw new IOException("Invalid answer from the service.");
            if (response.Done)
            {
                return response;
            }

            if (response.Progress is not null)
            {
                progress?.Report(response.Progress);
            }
        }
    }
}
