using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Storage.Service;
using Storage.Service.Pairing;

// The Win32 calls are in-memory stand-ins; execute the real window handlers and posted callbacks.
[assembly: SupportedOSPlatform("windows")]

const string Warning = "Pairing could not be confirmed. Check the paired-device lists on both devices before trying again.";
var failures = new List<string>();
foreach (string path in new[] { "Cancel", "close before completion", "Escape and reopen before completion" })
{
    var manager = new PairingManager();
    using var queue = new BlockingCollection<Action>();
    using var window = new PairingWindow(manager, queue.Add, () => true);
    void Show() => window.Show(new TrayWin32.Point(), 0);
    void Dispatch()
    {
        if (!queue.TryTake(out var next, TimeSpan.FromSeconds(5))) throw new Exception("Missing window callback");
        next();
    }
    void Check(bool condition, string message)
    {
        if (!condition) failures.Add($"{path}: {message}");
    }

    Show();
    Dispatch(); // Initial device list.
    TrayWin32.Click("Add device");
    Dispatch(); // Local addresses.
    switch (path)
    {
        case "Cancel": TrayWin32.Click("Cancel"); break;
        case "close before completion": TrayWin32.Close(); break;
        default:
            var escape = new TrayWin32.Message { Hwnd = TrayWin32.Window, Id = 0x0100, WParam = 0x1b };
            Check(window.HandleMessage(ref escape), "Escape should close Devices");
            Show();
            break;
    }
    Check(manager.Token.IsCancellationRequested, "the window should request cancellation");
    if (TrayWin32.Window != 0)
    {
        Dispatch(); // Device list while the old pairing attempt is still pending.
        Check(new[] { "Add device", "Add Android phone", "Join a network" }.All(TrayWin32.Disabled),
            "new pairing must be blocked until the previous attempt completes");
    }

    manager.Completion.SetException(new PairingUncertainException("Test fixture: remote save may have happened", new IOException()));
    Dispatch(); // Pairing completion, deliberately after cancellation/navigation.
    if (TrayWin32.Window == 0) Show();
    Dispatch(); // Post-completion device list.
    Check(TrayWin32.HasText(Warning), "both-device warning lost on completion");

    manager.FailRead = true;
    TrayWin32.Click("Refresh");
    Dispatch();
    Check(TrayWin32.HasText(Warning) && TrayWin32.HasText("Couldn't read"), "a failed refresh must not erase the warning or hide the read error");

    manager.FailRead = false;
    TrayWin32.Close();
    Show();
    Dispatch();
    Check(TrayWin32.HasText(Warning), "both-device warning lost on reopen");
}

// A confirmed pre-save cancellation is not an uncertain enrollment.
{
    var manager = new PairingManager();
    using var queue = new BlockingCollection<Action>();
    using var window = new PairingWindow(manager, queue.Add, () => true);
    void Dispatch()
    {
        if (!queue.TryTake(out var next, TimeSpan.FromSeconds(5))) throw new Exception("Missing window callback");
        next();
    }
    window.Show(new TrayWin32.Point(), 0);
    Dispatch();
    TrayWin32.Click("Add device");
    Dispatch();
    TrayWin32.Click("Cancel");
    Dispatch();
    manager.Completion.SetCanceled(manager.Token);
    Dispatch();
    Dispatch();
    if (TrayWin32.HasText(Warning) || !TrayWin32.HasText("Pairing stopped."))
        failures.Add("pre-save cancellation should show the ordinary cancellation status, not the both-device warning");
}

if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
Console.WriteLine("Pairing window warning lifecycle passed.");
