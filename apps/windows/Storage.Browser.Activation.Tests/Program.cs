using Storage.Browser;

namespace Storage.Browser.Activation.Tests;

public static class ActivationTestSuite
{
    public static Task Run()
    {
        int count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS " + name); count++; }
        var tray = new ActivationServicePolicy(true);
        Check(!tray.CanAutoStart(false, false, true), "tray launch never autostarts");
        var normal = new ActivationServicePolicy(false);
        Check(!normal.CanAutoStart(false, true, true), "stopped or starting host prevents autostart");
        Check(normal.CanAutoStart(false, false, true), "normal absent-host launch may start bundled service");
        Check(!normal.CanAutoStart(true, false, true), "service starts at most once");
        Check(!normal.CanAutoStart(false, false, false), "missing executable cannot start");
        normal.SuppressAutoStart();
        Check(!normal.CanAutoStart(false, false, true), "forwarded opening disables future autostart");

        var os = new FakeActivation(true);
        var queue = new Queue<Action>();
        using (var owner = new ActivationCoordinator(os))
        {
            Check(owner.IsOwner && os.Listening && os.Signals == 0, "owner listens without routing elsewhere");
            os.Receive(); // Arrives before WinUI and its DispatcherQueue exist.
            int activated = 0;
            owner.Attach(action => queue.Enqueue(action), () => activated++);
            Check(queue.Count == 1 && activated == 0, "early activation retained and queued to UI");
            queue.Dequeue()();
            Check(activated == 1, "early activation delivered after window attachment");
            os.Receive(); queue.Dequeue()();
            Check(activated == 2, "later activation restores existing window");
            os.Receive();
            owner.Dispose();
            queue.Dequeue()();
            os.Receive();
            Check(activated == 2 && os.Disposed && !os.Listening, "close unregisters listener and ignores queued or late callbacks");
        }
        Check(os.DisposeCount == 1, "close cleanup is idempotent");
        var secondaryOs = new FakeActivation(false);
        using (var secondary = new ActivationCoordinator(secondaryOs))
            Check(!secondary.IsOwner && secondaryOs.Signals == 1 && !secondaryOs.Listening, "secondary signals owner and creates no GUI listener");
        Check(secondaryOs.Disposed, "secondary releases all OS handles");
        var synchronousOs = new FakeActivation(true) { SignalDuringListen = true };
        using (var synchronousOwner = new ActivationCoordinator(synchronousOs))
        {
            int activated = 0;
            synchronousOwner.Attach(action => action(), () => activated++);
            Check(activated == 1, "signal during listener registration is not lost");
        }
        var failingOs = new FakeActivation(true) { FailListen = true };
        try { _ = new ActivationCoordinator(failingOs); throw new Exception("FAIL: listener setup failure must propagate"); }
        catch (IOException) { Check(failingOs.Disposed, "failed listener startup releases ownership handles"); }
        Check(ActivationIdentity.Name("/deploy/Storage.Browser.exe", "user-a") == ActivationIdentity.Name("/DEPLOY/storage.browser.EXE", "user-a"), "deployed path identity ignores Windows casing");
        Check(ActivationIdentity.Name("/deploy/Storage.Browser.exe", "user-a") != ActivationIdentity.Name("/other/Storage.Browser.exe", "user-a"), "different deployed executables do not share instance");
        Check(ActivationIdentity.Name("/deploy/Storage.Browser.exe", "user-a") != ActivationIdentity.Name("/deploy/Storage.Browser.exe", "user-b"), "different users do not share instance");
        void BringForward(bool minimized, Action restore, Action activate, Action foreground) =>
            ActivationWindowPolicy.BringForward(minimized, restore, activate, foreground);
        var windowActions = new List<string>();
        BringForward(true, () => windowActions.Add("restore"), () => windowActions.Add("activate"), () => windowActions.Add("foreground"));
        Check(windowActions.SequenceEqual(new[] { "restore", "activate", "foreground" }), "minimized owner restores before activation and foreground");
        windowActions.Clear();
        BringForward(false, () => windowActions.Add("restore"), () => windowActions.Add("activate"), () => windowActions.Add("foreground"));
        Check(windowActions.SequenceEqual(new[] { "activate", "foreground" }), "normal or maximized owner activates without restore");
        Console.WriteLine($"{count} activation behavior checks passed");
        return Task.CompletedTask;
    }
    public static Task Main() => Run();
}

sealed class FakeActivation(bool owner) : IActivationPlatform
{
    private Action? receive;
    public bool Listening, Disposed, SignalDuringListen, FailListen;
    public int Signals, DisposeCount;
    public bool TryOwn() => owner;
    public IDisposable Listen(Action callback)
    {
        if (FailListen) throw new IOException("listener registration failed");
        receive = callback;
        Listening = true;
        if (SignalDuringListen) callback();
        return new Registration(() => Listening = false);
    }
    public void Receive() => receive?.Invoke(); // Also models a native callback already in flight at unregister.
    public void SignalExisting() => Signals++;
    public void Dispose() { Disposed = true; DisposeCount++; }
    private sealed class Registration(Action close) : IDisposable { public void Dispose() => close(); }
}
