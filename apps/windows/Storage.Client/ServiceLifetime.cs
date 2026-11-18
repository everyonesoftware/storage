using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;
namespace Storage.Client;
[SupportedOSPlatform("windows")]
public static class ServiceLifetime
{
    public static string StopEventName(string executable) => "Local\\Storage.Stop." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executable).ToUpperInvariant())));
    // Same-user shutdown only; never grants repository access or accepts wire commands.
    public static void StopBundled(string executable)
    {
        try { using var signal = EventWaitHandle.OpenExisting(StopEventName(executable)); signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { return; }
        using var own = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("Storage.Service"))
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.SessionId != own.SessionId) continue;
                    using var handle = process.SafeHandle;
                    if (InspectPath(handle).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase) && !process.WaitForExit(10000))
                        throw new IOException("Bundled Storage Service did not stop gracefully.");
                }
                catch (InvalidOperationException) { } // Process exited during inspection.
                catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 5) { } // Another user's inaccessible process is not ours.
            }
        }
    }
    private static string InspectPath(Microsoft.Win32.SafeHandles.SafeProcessHandle handle) => WindowsPeers.Inspect(handle).ExecutablePath;
}
