using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MacroHid.Control;

public static class ControlPipe
{
    public static string Name => $"MacroHID.Control.v1.{WindowsIdentity.GetCurrent().User!.Value}.{Process.GetCurrentProcess().SessionId}";
    // A medium label permits an ordinary terminal to talk to elevated Studio. The DACL
    // grants access only to this user; remote clients are rejected by CreateNamedPipe.
    internal static string SecurityDescriptor => $"D:P(A;;GA;;;{WindowsIdentity.GetCurrent().User!.Value})S:(ML;;NW;;;ME)";
    public static async Task<ControlResponse> SendAsync(ControlRequest request, CancellationToken cancellation = default, string? pipeName = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", pipeName ?? Name, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        await pipe.ConnectAsync(timeout.Token);
        await WriteAsync(pipe, request, timeout.Token);
        var response = await ReadAsync<ControlResponse>(pipe, timeout.Token);
        // The server must not disconnect until the full response has been consumed.
        await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        return response;
    }
    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size <= 0 || size > ControlProtocol.MaxMessageBytes) throw new IOException("Invalid control message length.");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, ControlProtocol.Json) ?? throw new IOException("Empty message.");
    }
    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ControlProtocol.Json);
        if (bytes.Length > ControlProtocol.MaxMessageBytes) throw new IOException("Control response too large.");
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }
    internal static NamedPipeServerStream CreateServer(string name)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(SecurityDescriptor, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, 0x40080003, 0x8, 1, 65536, 65536, 0, ref security);
            if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("Control endpoint already in use or unavailable.", new Win32Exception(error)); }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { LocalFree(descriptor); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    { public int Length; public IntPtr Descriptor; public int InheritHandle; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint access, uint mode, uint instances, uint output, uint input, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

public sealed class ControlPipeServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource cancellation = new();
    private Task? loop;
    public ControlPipeServer(string? name = null) => pipe = ControlPipe.CreateServer(name ?? ControlPipe.Name);
    public void Start(Func<ControlRequest, Task<ControlResponse>> handler) => loop = RunAsync(handler);
    private async Task RunAsync(Func<ControlRequest, Task<ControlResponse>> handler)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancellation.Token);
                using var clientTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                clientTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var request = await ControlPipe.ReadAsync<ControlRequest>(pipe, clientTimeout.Token);
                var response = await handler(request);
                await ControlPipe.WriteAsync(pipe, response, clientTimeout.Token);
                var acknowledgement = new byte[1];
                await pipe.ReadExactlyAsync(acknowledgement, clientTimeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or ObjectDisposedException) { }
            finally { try { if (pipe.IsConnected) pipe.Disconnect(); } catch (IOException) { } }
        }
    }
    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        if (loop is not null) await loop;
        pipe.Dispose(); cancellation.Dispose();
    }
}
