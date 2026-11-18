using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace Storage.Client;
public sealed record WindowsProcessIdentity(string PackageFamily, string ExecutablePath);
[SupportedOSPlatform("windows")]
public static class WindowsPeers
{
    public static string PipeName => "LOCAL\\Everyonesoftware.Storage.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value)))[..24];
    public static WindowsProcessIdentity Inspect(SafeProcessHandle process)
    {
        var path = new StringBuilder(32768); uint count = (uint)path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref count)) throw new Win32Exception();
        uint size = 0; int status = GetPackageFamilyName(process, ref size, null);
        if (status == 15700) return new("", path.ToString());
        if (status != 122 || size > 1024) throw new Win32Exception(status);
        var family = new StringBuilder((int)size);
        status = GetPackageFamilyName(process, ref size, family);
        if (status != 0) throw new Win32Exception(status);
        return new(family.ToString(), path.ToString());
    }
    public static SafeProcessHandle OpenPipePeer(SafePipeHandle pipe, bool server)
    {
        uint pid;
        bool success = server ? GetNamedPipeServerProcessId(pipe, out pid) : GetNamedPipeClientProcessId(pipe, out pid);
        if (!success) throw new Win32Exception();
        var handle = OpenProcess(0x1000, false, pid);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
        return handle;
    }
    public static async Task<Stream> ConnectAsync(string expectedServiceExecutable, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(2500, token);
            using var peer = OpenPipePeer(pipe.SafePipeHandle, true);
            var identity = Inspect(peer);
            using var self = Process.GetCurrentProcess();
            var own = Inspect(self.SafeHandle);
            if (!Path.GetFullPath(identity.ExecutablePath).Equals(Path.GetFullPath(expectedServiceExecutable), StringComparison.OrdinalIgnoreCase) || (own.PackageFamily.Length > 0 && own.PackageFamily != identity.PackageFamily))
                throw new StorageException("PermissionDenied", "Pipe server is not the expected installed Storage Service.");
            return pipe;
        }
        catch { await pipe.DisposeAsync(); throw; }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, StringBuilder? name);
}
