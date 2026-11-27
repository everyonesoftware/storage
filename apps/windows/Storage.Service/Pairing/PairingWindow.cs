using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ZXing.Common;
using static Storage.Service.TrayWin32;

namespace Storage.Service.Pairing;

// The tray STA owns this window and every control. Pairing callbacks run off-thread and
// must be posted back through the tray; generation checks retire callbacks on navigation.
[SupportedOSPlatform("windows")]
internal sealed class PairingWindow(PairingManager pairing, Action<Action> post, Func<bool> running) : IDisposable
{
    private const string ClassName = "Storage.Service.Devices";
    private const int Width = 536, Height = 460;
    private const int QrSize = 240, QrTop = 118;
    private const int DeviceList = 201, NearbyList = 202, Address = 203;
    private const string UncertainNotice = "Pairing could not be confirmed. Check the paired-device lists on both devices before trying again.";
    private readonly nint instance = GetModuleHandle(null);
    private readonly List<Control> controls = [];
    private WindowProc? procedure;
    private bool registered;
    private nint hwnd, bodyFont, headingFont, codeFont;
    private int generation;
    private double scale = 1;
    private CancellationTokenSource? discovery, attempt;
    private bool pairingUncertain; // Retained on the tray STA across Devices HWNDs and refreshes.
    private BitMatrix? qr;
    private IReadOnlyList<NearbyDevice> nearby = [];

    private sealed record Control(nint Handle, int X, int Y, int Width, int Height, int Font, Action? Click);

    internal void Show(Point anchor, nint icon)
    {
        if (!running()) return;
        if (hwnd != 0)
        {
            ShowWindow(hwnd, 9); // restore if minimized
            SetForegroundWindow(hwnd);
            if (!IsChild(hwnd, GetFocus())) FocusFirstButton();
            return;
        }
        if (!registered)
        {
            procedure = WindowMessage;
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Proc = Marshal.GetFunctionPointerForDelegate(procedure),
                Instance = instance, Cursor = LoadCursor(0, (nint)32512), Icon = icon, SmallIcon = icon,
                Background = 6, Name = ClassName // COLOR_WINDOW + 1: preserve system/high contrast colors
            };
            if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception();
            registered = true;
        }
        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(anchor, 2), ref monitor)) throw new Win32Exception();
        hwnd = CreateWindowEx(0x00010000, ClassName, "Devices — Storage Service",
            0x00c00000 | 0x00080000 | 0x00020000 | 0x02000000, monitor.Work.Left, monitor.Work.Top,
            Width, Height, 0, 0, instance, 0); // caption, system menu, minimize, clip children
        if (hwnd == 0) throw new Win32Exception();
        try
        {
            int rounded = 2;
            DwmSetWindowAttribute(hwnd, 33, ref rounded, 4);
            FitWindow(monitor.Work, GetDpiForWindow(hwnd));
            ShowDevices();
            ShowWindow(hwnd, 5);
            SetForegroundWindow(hwnd);
            FocusFirstButton();
        }
        catch { Close(); throw; }
    }

    internal bool HandleMessage(ref Message message)
    {
        if (hwnd == 0 || (message.Hwnd != hwnd && !IsChild(hwnd, message.Hwnd))) return false;
        if (message.Id == 0x0100)
        {
            if (message.WParam == 0x1b) { Close(); return true; }
            if (message.WParam == 0x0d)
            {
                // Do not give Approve a default action. Enter activates only a focused button.
                nint target = message.Hwnd;
                if (controls.Exists(c => c.Handle == target && c.Click is not null))
                    SendMessage(message.Hwnd, 0x00f5, 0, 0);
                return true;
            }
        }
        return IsDialogMessage(hwnd, ref message);
    }

    private nint WindowMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case 0x0010: Close(); return 0;
                // This secondary top-level window must not abort the tray's session-end vote.
                case 0x0011: return 1;
                case 0x0016 when wParam != 0: Close(); return 0;
                case 0x000f when window == hwnd && qr is not null: // WM_PAINT
                    PaintQr(window, qr);
                    return 0;
                case 0x0138: // WM_CTLCOLORSTATIC: use the same system palette as the window.
                    SetTextColor((nint)wParam, GetSysColor(8)); // COLOR_WINDOWTEXT
                    SetBkColor((nint)wParam, GetSysColor(5)); // COLOR_WINDOW
                    return GetSysColorBrush(5);
                case 0x0111 when wParam >> 16 == 0 && lParam != 0:
                    if (running() && IsWindowEnabled(lParam))
                        controls.Find(c => c.Handle == lParam)?.Click?.Invoke();
                    return 0;
                case 0x02e0 when window == hwnd && lParam != 0: // WM_DPICHANGED
                    var suggested = Marshal.PtrToStructure<Rect>(lParam);
                    var target = new Point { X = suggested.Left, Y = suggested.Top };
                    var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                    if (GetMonitorInfo(MonitorFromPoint(target, 2), ref monitor))
                        FitWindow(monitor.Work, (uint)(wParam & 0xffff), suggested);
                    return 0;
            }
        }
        catch
        {
            // Neither pairing secrets nor peer-supplied exception detail belong in the UI or logs.
            Close();
            MessageBox(0, "Storage Service couldn't open the pairing screen. Open Devices to try again.",
                "Storage Service", 0x10);
            return 0;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private void FitWindow(Rect work, uint dpi, Rect? position = null)
    {
        int workWidth = work.Right - work.Left, workHeight = work.Bottom - work.Top;
        scale = Math.Min(dpi / 96.0, Math.Min((double)workWidth / Width, (double)workHeight / Height));
        int width = Px(Width), height = Px(Height);
        int x = Math.Clamp(position?.Left ?? work.Left + (workWidth - width) / 2, work.Left, work.Right - width);
        int y = Math.Clamp(position?.Top ?? work.Top + (workHeight - height) / 2, work.Top, work.Bottom - height);
        SetWindowPos(hwnd, 0, x, y, width, height, 0x0014);
        nint oldBody = bodyFont, oldHeading = headingFont, oldCode = codeFont;
        bodyFont = Font(15, 400, "Segoe UI");
        headingFont = Font(29, 600, "Segoe UI");
        codeFont = Font(38, 600, "Consolas");
        foreach (var control in controls) Layout(control);
        FreeFonts(oldBody, oldHeading, oldCode);
        InvalidateRect(hwnd, 0, true);
    }

    private int Px(int value) => (int)Math.Round(value * scale);
    private nint Font(int size, int weight, string face) =>
        CreateFont(-Px(size), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, face);

    private static void FreeFonts(params nint[] fonts)
    {
        foreach (var font in fonts) if (font != 0) DeleteObject(font);
    }

    private void Layout(Control control)
    {
        nint font = control.Font switch { 1 => headingFont, 2 => codeFont, _ => bodyFont };
        SendMessage(control.Handle, 0x0030, (nuint)font, 1); // WM_SETFONT
        SetWindowPos(control.Handle, 0, Px(control.X), Px(control.Y), Px(control.Width), Px(control.Height), 0x0014);
    }

    private nint Add(string kind, string text, int x, int y, int width, int height, uint style = 0,
        int id = 0, int font = 0, Action? click = null, uint extended = 0)
    {
        nint handle = CreateWindowEx(extended, kind, text, 0x50000000 | style, 0, 0, 0, 0, hwnd,
            id != 0 ? id : controls.Count + 100, instance, 0);
        if (handle == 0) throw new Win32Exception();
        var control = new Control(handle, x, y, width, height, font, click);
        controls.Add(control);
        Layout(control);
        return handle;
    }

    private nint Label(string text, int y, int height, int font = 0, uint style = 0) =>
        Add("Static", text, 24, y, 470, height, style | 0x80, font: font); // SS_NOPREFIX

    private nint Button(string text, int x, int y, int width, Action action) =>
        Add("Button", text, x, y, width, 34, 0x00010000, click: action);

    private nint ListBox(int y, int height, int id) => Add("ListBox", "", 24, y, 470, height,
        0x00210000 | 0x00000100, id, extended: 0x00000200); // WS_VSCROLL | WS_TABSTOP | LBS_NOINTEGRALHEIGHT

    private void FocusFirstButton()
    {
        if (controls.Find(c => c.Click is not null && IsWindowEnabled(c.Handle)) is { } button) Focus(button.Handle);
    }

    private void Focus(nint control)
    {
        if (GetForegroundWindow() == hwnd) SetFocus(control);
    }

    private void Page(string title, string help)
    {
        generation++;
        qr = null;
        discovery?.Cancel();
        discovery = null;
        nearby = [];
        foreach (var control in controls) DestroyWindow(control.Handle);
        controls.Clear();
        Label(title, 20, 40, 1);
        Label(help, 68, 48);
        InvalidateRect(hwnd, 0, true);
    }

    private bool Current(int page) => hwnd != 0 && page == generation && running();

    private void ShowDevices(string? notice = null)
    {
        if (pairingUncertain) notice = UncertainNotice;
        Page("Your devices", "Manage trusted devices in Storage Service. Pairing does not transfer files.");
        Label($"{Environment.MachineName} · This device", 121, 25);
        Label("Paired devices", 154, 25);
        nint devices = ListBox(182, 128, DeviceList);
        nint status = Label(notice ?? "Loading paired devices…", 312, 54);
        var add = Button("Add device", 24, 370, 108, () => StartPairing(null));
        var android = Button("Add Android phone", 142, 370, 144, ShowQrAddresses);
        var join = Button("Join a network", 296, 370, 122, ShowJoin);
        Button("Refresh", 428, 370, 66, () => ShowDevices());
        EnableWindow(android, false); // Role must be resolved before creating a QR offer.
        if (attempt is not null)
        {
            EnableWindow(add, false);
            EnableWindow(join, false);
            SetWindowText(status, "Finishing the previous pairing attempt…");
        }
        FocusFirstButton();
        _ = LoadDevicesAsync(generation, devices, status, add, android, join, notice);
    }

    private async Task LoadDevicesAsync(int page, nint list, nint label, nint add, nint android, nint join, string? notice)
    {
        try
        {
            var devices = await Task.Run(pairing.List).ConfigureAwait(false);
            post(() =>
            {
                if (!Current(page)) return;
                foreach (var device in devices)
                    SendText(list, 0x0180, 0, device.Name + (device.IsCoordinator ? " · Coordinator" : " · Paired"));
                if (attempt is not null) return;
                bool joined = devices.Any(d => d.IsCoordinator);
                EnableWindow(android, !joined);
                if (devices.Count > 0)
                {
                    // Correspond to the manager's roles, not an authorization grant.
                    nint unavailable = joined ? add : join;
                    bool hadFocus = GetFocus() == unavailable;
                    EnableWindow(unavailable, false);
                    if (hadFocus) FocusFirstButton();
                }
                SetWindowText(label, notice ?? (devices.Count == 0 ? "No paired devices yet." : joined
                    ? "Add devices from your network's coordinator. Paired devices may be offline."
                    : "Paired devices may be offline."));
            });
        }
        catch
        {
            post(() =>
            {
                if (Current(page) && attempt is null)
                    SetWindowText(label, pairingUncertain ? $"Couldn't read paired devices.\r\n{UncertainNotice}"
                        : "Couldn't read the paired devices. Refresh to try again.");
            });
        }
    }

    private void ShowQrAddresses()
    {
        if (attempt is not null || !running()) return;
        Page("Add Android phone", "Select this PC’s IPv4 address on the same Wi-Fi or local network as your phone.");
        Label("Local IPv4 addresses", 121, 25);
        nint list = ListBox(150, 150, NearbyList);
        nint status = Label("Checking local network addresses…", 310, 48);
        var addresses = new List<IPAddress>();
        nint show = Button("Show pairing QR", 24, 370, 142, () =>
        {
            int index = (int)SendMessage(list, 0x0188, 0, 0); // LB_GETCURSEL
            if (index >= 0 && index < addresses.Count) StartPairing(null, addresses[index]);
        });
        EnableWindow(show, false);
        Focus(Button("Back", 178, 370, 100, () => ShowDevices()));
        _ = LoadQrAddressesAsync(generation, list, status, show, addresses);
    }

    private async Task LoadQrAddressesAsync(int page, nint list, nint status, nint show, List<IPAddress> displayed)
    {
        IReadOnlyList<IPAddress> addresses;
        try { addresses = await Task.Run(PairingDiscovery.LocalAddresses).ConfigureAwait(false); }
        catch { addresses = []; }
        post(() =>
        {
            if (!Current(page)) return;
            foreach (var address in addresses)
                if (SendText(list, 0x0180, 0, address.ToString()) >= 0) displayed.Add(address);
            if (displayed.Count != 0)
            {
                SendMessage(list, 0x0186, 0, 0); // LB_SETCURSEL
                EnableWindow(show, true);
            }
            SetWindowText(status, displayed.Count == 0 ? "No LAN IPv4 address is available. Check your network and try again."
                : "You can try a different address if the phone can't connect.");
        });
    }

    private void PaintQr(nint window, BitMatrix modules)
    {
        nint dc = BeginPaint(window, out var paint);
        try
        {
            int pitch = Px(QrSize) / modules.Width;
            if (pitch == 0 || !GetClientRect(window, out var client)) return;
            int size = pitch * modules.Width;
            int left = (client.Right - size) / 2;
            int top = Px(QrTop) + (Px(QrSize) - size) / 2;
            var paper = new Rect(left, top, left + size, top + size);
            FillRect(dc, ref paper, GetStockObject(0)); // WHITE_BRUSH, including the quiet zone
            nint ink = GetStockObject(4); // BLACK_BRUSH
            for (int y = 0; y < modules.Height; y++)
            {
                for (int x = 0; x < modules.Width;)
                {
                    if (!modules[x, y]) { x++; continue; }
                    int start = x++;
                    while (x < modules.Width && modules[x, y]) x++;
                    var run = new Rect(left + start * pitch, top + y * pitch, left + x * pitch, top + (y + 1) * pitch);
                    FillRect(dc, ref run, ink);
                }
            }
        }
        finally { EndPaint(window, ref paint); }
    }

    private void ShowJoin()
    {
        Page("Join a network", "On the other device, select Add device. Keep both devices on the same local network.");
        Label("Nearby devices", 121, 24);
        nint list = ListBox(148, 92, NearbyList);
        nint notice = Label("Looking for nearby devices…", 246, 50);
        Label("Enter address manually (host name or IPv4)", 301, 23);
        nint address = Add("Edit", "", 24, 326, 470, 30, 0x00010080, Address, extended: 0x00000200);
        SendMessage(address, 0x00c5, 253, 0); // EM_SETLIMITTEXT
        Button("Connect", 24, 370, 112, () =>
        {
            var text = new StringBuilder(254);
            GetWindowText(address, text, text.Capacity);
            string input = text.ToString().Trim();
            int selected = (int)SendMessage(list, 0x0188, 0, 0); // LB_GETCURSEL
            if (input.Length == 0 && selected >= 0 && selected < nearby.Count)
                input = nearby[selected].Address.ToString();
            if (input.Length == 0)
            {
                SetWindowText(notice, "Select a nearby device or enter its host name or IP address.");
                Focus(address);
                return;
            }
            StartPairing(input);
        });
        Button("Scan again", 148, 370, 112, () => FindNearby(list, notice));
        Button("Back", 272, 370, 100, () => ShowDevices());
        Focus(address);
        FindNearby(list, notice);
    }

    private void FindNearby(nint list, nint notice)
    {
        if (discovery is not null) return;
        discovery = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        nearby = [];
        SendMessage(list, 0x0184, 0, 0); // LB_RESETCONTENT
        SetWindowText(notice, "Looking for nearby devices…");
        _ = FindNearbyAsync(generation, discovery, list, notice);
    }

    private async Task FindNearbyAsync(int page, CancellationTokenSource search, nint list, nint notice)
    {
        IReadOnlyList<NearbyDevice>? results = null;
        try { results = await Task.Run(() => PairingDiscovery.FindAsync(search.Token)).ConfigureAwait(false); }
        catch { /* Manual connection is still available if discovery fails. */ }
        post(() =>
        {
            search.Dispose();
            if (!Current(page) || !ReferenceEquals(discovery, search)) return;
            discovery = null;
            const string manual = "Enter the host name or IP shown on the other device. Check Wi-Fi and firewall settings if needed.";
            if (results is null || results.Count == 0)
            {
                SetWindowText(notice, (results is null ? "Discovery unavailable. " : "No nearby devices found. ") + manual);
                return;
            }
            var displayed = new List<NearbyDevice>();
            foreach (var device in results)
            {
                if (SendText(list, 0x0180, 0, $"{device.Name} · {device.Address}") >= 0) displayed.Add(device);
            }
            nearby = displayed;
            SetWindowText(notice, "Select a nearby device, or enter an address to connect manually.");
        });
    }

    private void StartPairing(string? address, IPAddress? qrAddress = null)
    {
        if (attempt is not null || !running()) return;
        pairingUncertain = false;
        bool host = address is null;
        nint qrStatus = 0;
        if (qrAddress is not null)
        {
            Page("Scan pairing QR", $"Open Storage Service on your phone and scan here. Both devices must be on the same local network ({qrAddress}).");
            qrStatus = Label("Preparing a one-time pairing code…", 130, 58);
        }
        else Page(host ? "Add device" : "Connecting…", host
            ? "On Windows, choose Join a network and select this PC. For manual pairing, enter an address below on the other device."
            : "Keep both devices open. Compare the confirmation codes when they appear.");
        if (host && qrAddress is null)
        {
            Label($"Host name: {Environment.MachineName}", 123, 26);
            Label("Local IPv4 addresses", 158, 24);
            nint addresses = ListBox(184, 110, NearbyList);
            var notice = Label("Checking local network addresses…", 306, 52);
            _ = LoadAddressesAsync(generation, addresses, notice);
        }
        else if (!host) Label("Waiting for the other device…", 124, 50);
        Focus(Button("Cancel", 24, 370, 112, CancelPairing));
        attempt = new CancellationTokenSource(); // The manager owns the protocol deadline.
        _ = PairAsync(generation, attempt, address, qrAddress, qrStatus);
    }

    private async Task LoadAddressesAsync(int page, nint list, nint notice)
    {
        try
        {
            var addresses = await Task.Run(() => PairingDiscovery.LocalAddresses().ToArray()).ConfigureAwait(false);
            post(() =>
            {
                if (!Current(page)) return;
                foreach (var address in addresses) SendText(list, 0x0180, 0, address.ToString());
                SetWindowText(notice, addresses.Length == 0
                    ? "No LAN IPv4 address detected. Check your local network and try again."
                    : "Waiting for a connection. If this PC isn't reachable, check your local network and firewall.");
            });
        }
        catch
        {
            post(() => { if (Current(page)) SetWindowText(notice, "Couldn't read local addresses. Check your network settings."); });
        }
    }

    private void CancelPairing()
    {
        if (attempt is null) return;
        attempt.Cancel();
        ShowDevices("Pairing cancelled. Refresh paired devices to check the current trust list.");
    }

    private async Task PairAsync(int startPage, CancellationTokenSource operation, string? address, IPAddress? qrAddress, nint qrStatus)
    {
        bool host = address is null;
        bool rejected = false;
        bool uncertain = false;
        string outcome;
        try
        {
            void DisplayQr(BitMatrix? modules) => post(() =>
            {
                if (!Current(startPage) || !ReferenceEquals(attempt, operation) || operation.IsCancellationRequested) return;
                qr = modules;
                // The parent clips child windows when drawing the QR. An empty but
                // visible status label would still hide part of the image.
                ShowWindow(qrStatus, modules is null ? 5 : 0);
                if (modules is null) SetWindowText(qrStatus, "A device connected. Waiting to verify the connection…");
                InvalidateRect(hwnd, 0, true);
            });

            async Task<bool> Confirm(PairingChallenge challenge, CancellationToken ct)
            {
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, operation.Token);
                var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                post(() =>
                {
                    if (!Current(startPage) || !ReferenceEquals(attempt, operation) || waiting.IsCancellationRequested)
                    {
                        decision.TrySetResult(false);
                        return;
                    }
                    ShowChallenge(challenge, host, waiting.Token, decision);
                });
                bool approved = await decision.Task.WaitAsync(waiting.Token).ConfigureAwait(false);
                rejected = !approved;
                return approved;
            }

            await (qrAddress is not null ? pairing.HostQrAsync(qrAddress, DisplayQr, Confirm, operation.Token)
                : host ? pairing.HostAsync(Confirm, operation.Token)
                : pairing.JoinAsync(address!, Confirm, operation.Token)).ConfigureAwait(false);
            outcome = rejected ? "Codes didn't match. No device was approved." : "Device paired.";
        }
        catch (Exception error)
        {
            uncertain = error is PairingUncertainException;
            outcome = error switch
            {
                PairingUncertainException => UncertainNotice,
                _ when rejected => "Codes didn't match. No device was approved.",
                TimeoutException => "Pairing timed out. Check both devices and try again.",
                OperationCanceledException => "Pairing stopped. Check the paired list before trying again.",
                _ => "Couldn't complete pairing. Check the local network and firewall, then try again."
            };
        }
        post(() =>
        {
            operation.Dispose();
            if (!ReferenceEquals(attempt, operation)) return;
            attempt = null;
            // Cancellation only requests a stop; the exchange may already have saved trust.
            // Keep its result even if Close destroyed the window before this callback arrived.
            pairingUncertain = uncertain;
            if (hwnd != 0 && running()) ShowDevices(outcome);
        });
    }

    private void ShowChallenge(PairingChallenge challenge, bool host, CancellationToken token, TaskCompletionSource<bool> decision)
    {
        Page("Do these codes match?", "Compare this code with the one on the other device. Only continue if they match.");
        Label(challenge.PeerName, 130, 46);
        Label(challenge.Code, 188, 64, 2);
        nint status = Label("Both devices must confirm before pairing can finish.", 282, 64);
        nint approve = 0, reject = 0;
        void Decide(bool approved)
        {
            if (token.IsCancellationRequested || !decision.TrySetResult(approved)) return;
            EnableWindow(approve, false);
            EnableWindow(reject, false);
            SetWindowText(status, approved ? "Waiting for the other device to confirm…" : "Stopping pairing…");
            FocusFirstButton();
        }
        approve = Button(host ? "Approve device" : "Codes match", 24, 370, 143, () => Decide(true));
        reject = Button("Codes don't match", 179, 370, 168, () => Decide(false));
        Focus(Button("Cancel", 359, 370, 112, CancelPairing));
    }

    internal void Close()
    {
        generation++;
        qr = null;
        discovery?.Cancel();
        discovery = null;
        attempt?.Cancel();
        if (hwnd != 0)
        {
            nint window = hwnd;
            hwnd = 0;
            DestroyWindow(window);
        }
        controls.Clear();
        nearby = [];
        FreeFonts(bodyFont, headingFont, codeFont);
        bodyFont = headingFont = codeFont = 0;
    }

    public void Dispose()
    {
        Close();
        if (registered) UnregisterClass(ClassName, instance);
        registered = false;
        GC.KeepAlive(procedure);
    }
}
