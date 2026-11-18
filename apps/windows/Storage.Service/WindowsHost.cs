using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Storage.Client;
namespace Storage.Service;
[SupportedOSPlatform("windows")]
internal static class WindowsHost
{
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    public static NamedPipeServerStream CreateLocalPipe()
    {
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;{sid})", 1, out var descriptor, out _)) throw new Win32Exception();
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            // Overlapped + first-instance; byte mode + PIPE_REJECT_REMOTE_CLIENTS.
            var handle = CreateNamedPipe("\\\\.\\pipe\\" + WindowsPeers.PipeName, 3 | 0x40000000 | 0x00080000, 8, 1, 65536, 65536, 0, ref security);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { LocalFree(descriptor); }
    }
    public static async Task RunAsync(ServiceSession session, DevelopmentGrant? development, CancellationToken token)
    {
        using var selfProcess = Process.GetCurrentProcess();
        var self = WindowsPeers.Inspect(selfProcess.SafeHandle);
        while (!token.IsCancellationRequested)
        {
            await using var pipe = CreateLocalPipe();
            await pipe.WaitForConnectionAsync(token);
            try
            {
                using var process = WindowsPeers.OpenPipePeer(pipe.SafePipeHandle, false);
                var caller = WindowsPeers.Inspect(process);
                if (!AppAuthorization.IsAllowed(new(self.PackageFamily, self.ExecutablePath), new(caller.PackageFamily, caller.ExecutablePath), development))
                {
                    // No untrusted JSON is read before OS-derived authorization succeeds.
                    await Frames.WriteAsync(pipe, WireJson.Bytes(new WireResponse(false, WireJson.Element(new { }), new("PermissionDenied", "Application approval required. Use the installed Storage Service --approve-bundled-browser consent dialog, or explicit development enrollment."))), token);
                    continue;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                await session.RunAsync(pipe, timeout.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or Win32Exception)
            { Console.Error.WriteLine("Connection closed: " + error.GetType().Name); }
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint mode, uint maxInstances, uint outputSize, uint inputSize, uint timeout, ref SecurityAttributes security);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
