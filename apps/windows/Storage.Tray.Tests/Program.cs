using Storage.Service;
using System.Runtime.InteropServices;

namespace Storage.Tray.Tests;

public static class TrayTestSuite
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static Task Main() => Run();
    public static async Task Run()
    {
        await StartReportsReadiness();
        Console.WriteLine("PASS StartReportsReadiness");
        await StartupFailureAllowsRetry();
        Console.WriteLine("PASS StartupFailureAllowsRetry");
        await StopDrainsBeforeRestart();
        Console.WriteLine("PASS StopDrainsBeforeRestart");
        await QuitCancelsStartupAndAwaitsDisposal();
        Console.WriteLine("PASS QuitCancelsStartupAndAwaitsDisposal");
        await SessionEndDrainsBeforeReturning();
        Console.WriteLine("PASS SessionEndDrainsBeforeReturning");
        await SessionEndHasABoundedDeadline();
        Console.WriteLine("PASS SessionEndHasABoundedDeadline");
        await RunnerFaultIsObservedWithoutAnotherAction();
        Console.WriteLine("PASS RunnerFaultIsObservedWithoutAnotherAction");
        VersionFourCallbackPreservesSignedAnchors();
        Console.WriteLine("PASS VersionFourCallbackPreservesSignedAnchors");
        PopupFitsNegativeMonitorAndDpi();
        Console.WriteLine("PASS PopupFitsNegativeMonitorAndDpi");
        BrowserLaunchHasFixedSiblingTarget();
        Console.WriteLine("PASS BrowserLaunchHasFixedSiblingTarget");
        await DisposalFailurePreventsReplacement();
        Console.WriteLine("PASS DisposalFailurePreventsReplacement");
        NativeStructuresMatchWindowsAbi();
        Console.WriteLine("PASS NativeStructuresMatchWindowsAbi");
    }
    private static async Task SessionEndDrainsBeforeReturning()
    {
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool disposed = false;
        var controller = new TrayController(async (ready, token) =>
        {
            ready();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { draining.SetResult(); await release.Task; disposed = true; }
        });
        await controller.StartAsync();
        var boundary = Task.Run(() => controller.EndSession(TimeSpan.FromSeconds(3)));
        await draining.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!boundary.IsCompleted && !disposed, "OS session handler must retain control until service disposal");
        release.SetResult();
        Check(await boundary && disposed && controller.Status == TrayStatus.Stopped, "OS session handler drains before returning success");
    }
    private static async Task SessionEndHasABoundedDeadline()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TrayController(async (ready, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            ready();
            await release.Task; // Simulate native work that cannot stop immediately.
        });
        await controller.StartAsync();
        Check(!controller.EndSession(TimeSpan.Zero), "expired shutdown budget must not claim a completed drain");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!await controller.StartAsync(), "session end remains terminal even when the drain budget expires");
        release.SetResult();
        await controller.QuitAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }
    private static void NativeStructuresMatchWindowsAbi()
    {
        // Win64 layout: DRAWITEMSTRUCT contains itemID between CtlID and itemAction.
        if (IntPtr.Size == 8)
        {
            Check(Marshal.SizeOf<TrayWin32.DrawItem>() == 64, "DRAWITEMSTRUCT size must match native Windows");
            Check(Marshal.OffsetOf<TrayWin32.DrawItem>("DC") == 32, "DRAWITEMSTRUCT hDC offset");
            Check(Marshal.SizeOf<TrayWin32.NotifyIcon>() == 976, "NOTIFYICONDATAW v4 size");
            Check(Marshal.SizeOf<TrayWin32.WindowClass>() == 80, "WNDCLASSEXW size");
            Check(Marshal.SizeOf<TrayWin32.ToolInfo>() == 72, "TOOLINFOW size");
            Check(Marshal.SizeOf<TrayWin32.Message>() == 48, "MSG size");
        }
    }
    private static async Task DisposalFailurePreventsReplacement()
    {
        int starts = 0;
        var controller = new TrayController(async (ready, token) =>
        {
            starts++; ready();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { throw new IOException("native disposal failed"); }
        });
        await controller.StartAsync();
        await controller.RestartAsync();
        Check(starts == 1 && controller.Status == TrayStatus.Error, "failed disposal must block a replacement core");
        Check(controller.Error == "native disposal failed", "retain cleanup failure");
        await controller.QuitAsync();
    }
    private static void BrowserLaunchHasFixedSiblingTarget()
    {
        string directory = Path.Combine(Path.GetTempPath(), "storage-tray-layout", "current", "Service");
        Check(TrayPolicy.BrowserPath(directory) == Path.Combine(Path.GetDirectoryName(directory)!, "Storage.Browser.exe"), "launch fixed production sibling only");
        bool denied = false;
        try { TrayPolicy.BrowserPath(Path.Combine(directory, "elsewhere")); } catch (IOException) { denied = true; }
        Check(denied, "non-Service layout rejected");
    }
    private static void PopupFitsNegativeMonitorAndDpi()
    {
        var work = new TrayBounds(-1920, -120, 1920, 1080);
        var bounds = TrayPolicy.Place(-5, 955, 144, work);
        Check(bounds.Width == 456 && bounds.Height == 319, "approved footer-free 304x213 popup scales at 150 percent DPI");
        Check(bounds.X >= work.X && bounds.Y >= work.Y && bounds.X + bounds.Width <= 0 && bounds.Y + bounds.Height <= 960, "popup remains inside workarea");
        var small = TrayPolicy.Place(0, 0, 192, new(0, 0, 200, 100));
        Check(small == new TrayBounds(0, 0, 200, 100), "small workarea clamps dimensions and origin");
    }
    private static void VersionFourCallbackPreservesSignedAnchors()
    {
        var decoded = TrayPolicy.Decode(unchecked((nuint)((uint)(ushort)-1400 | ((uint)(ushort)-80 << 16))), (nint)(0x0401 | (1 << 16)));
        Check(decoded == (0x0401, 1, -1400, -80), "version4 callback splits notification/id and signed coordinates");
    }
    private static async Task RunnerFaultIsObservedWithoutAnotherAction()
    {
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TrayController(async (ready, _) => { ready(); await fail.Task; throw new IOException("pipe listener failed"); });
        controller.Changed += () => { if (controller.Status == TrayStatus.Error) changed.TrySetResult(); };
        await controller.StartAsync();
        fail.SetResult();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(controller.Error == "pipe listener failed", "show the actual runner failure");
        await controller.QuitAsync();
    }
    private static async Task QuitCancelsStartupAndAwaitsDisposal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool disposed = false;
        var controller = new TrayController(async (_, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { draining.SetResult(); await release.Task; disposed = true; }
        });
        var start = controller.StartAsync();
        await entered.Task;
        Check(!await controller.RestartAsync(), "restart during startup rejected");
        Task quit = controller.QuitAsync();
        await draining.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!quit.IsCompleted && !disposed, "quit must wait for disposal");
        release.SetResult(); await quit; await start;
        Check(disposed && controller.Status == TrayStatus.Stopped, "quit completes after disposal");
        Check(!await controller.StartAsync(), "quit is terminal");
    }
    private static async Task StopDrainsBeforeRestart()
    {
        int starts = 0, disposed = 0;
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TrayController(async (ready, token) =>
        {
            Interlocked.Increment(ref starts); ready();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { draining.TrySetResult(); await release.Task; Interlocked.Increment(ref disposed); }
        });
        await controller.StartAsync();
        Task<bool> restart = controller.RestartAsync();
        await draining.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!restart.IsCompleted && starts == 1, "restart must await disposal of the previous core/session");
        Check(!await controller.StartAsync(), "overlapping start rejected");
        Check(!await controller.StopAsync(), "overlapping stop rejected");
        Check(!await controller.RestartAsync(), "overlapping restart rejected");
        release.SetResult(); await restart;
        Check(starts == 2 && disposed == 1, "fresh session only after old disposal");
        await controller.StopAsync();
        Check(controller.Status == TrayStatus.Stopped && disposed == 2, "stop drains and retains controller");
        await controller.StartAsync(); await controller.QuitAsync();
    }
    private static async Task StartupFailureAllowsRetry()
    {
        int attempts = 0;
        var controller = new TrayController(async (ready, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("health failed");
            ready(); await Task.Delay(Timeout.Infinite, token);
        });
        await controller.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(controller.Status == TrayStatus.Error, "startup fault must be reported");
        await controller.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(controller.Status == TrayStatus.Running && attempts == 2, "retry must initialize a fresh runner");
        await controller.QuitAsync();
    }
    private static async Task StartReportsReadiness()
    {
        var controller = new TrayController(async (ready, token) => { ready(); await Task.Delay(Timeout.Infinite, token); });
        Check(await controller.StartAsync(), "start accepted");
        Check(controller.Status == TrayStatus.Running, "real readiness must report Running");
        await controller.QuitAsync();
    }
}
