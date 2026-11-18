using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Storage.Browser;

[SupportedOSPlatform("windows")]
internal sealed class ActivationWindowsPlatform : IActivationPlatform
{
    private readonly string executable;
    private readonly EventWaitHandle signal;
    private readonly Mutex mutex;
    private bool owned, disposed;

    public ActivationWindowsPlatform()
    {
        executable = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Browser executable path unavailable."));
        using var identity = WindowsIdentity.GetCurrent();
        string name = ActivationIdentity.Name(executable, identity.User!.Value);
        var options = new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true };
        // Create/open the event BEFORE publishing mutex ownership. Auto-reset retains an
        // early signal until the owner installs its wait; no readiness polling or reset race.
        signal = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate", options);
        try { mutex = new Mutex(false, name, options); }
        catch { signal.Dispose(); throw; }
    }

    public bool TryOwn()
    {
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        return owned;
    }

    public IDisposable Listen(Action activated) => new WaitRegistration(
        ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => activated(), null, Timeout.Infinite, false));

    public void SignalExisting()
    {
        // A foreground-launched secondary may transfer its permission to the exact
        // deployed Browser, even before that process has created its first window.
        // This grants no service capabilities and never uses ASFW_ANY.
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(self.ProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.Id != self.Id && process.SessionId == self.SessionId &&
                        string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        _ = AllowSetForegroundWindow((uint)process.Id);
                }
                catch (InvalidOperationException) { } // Exited during inspection.
                catch (Win32Exception) { } // Inaccessible processes are not activation targets.
            }
        }
        signal.Set();
    }

    public static void BringForward(nint window, Action activate)
    {
        ActivationWindowPolicy.BringForward(IsIconic(window),
            () => { _ = ShowWindow(window, 9); }, // SW_RESTORE; leave maximized windows maximized.
            activate,
            () => { _ = SetForegroundWindow(window); }); // Windows retains final foreground-stealing policy.
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Called on Program.Main's STA, the same thread that acquired ownership.
        if (owned) mutex.ReleaseMutex();
        mutex.Dispose();
        signal.Dispose();
    }

    private sealed class WaitRegistration(RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose() => registration.Unregister(null);
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
