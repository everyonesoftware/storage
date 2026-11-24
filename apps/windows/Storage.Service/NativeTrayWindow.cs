using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Storage.Client;
using static Storage.Service.TrayWin32;

namespace Storage.Service;

[SupportedOSPlatform("windows")]
internal sealed class NativeTrayWindow : IDisposable
{
    // Automation contract: one service-owned hidden/popup HWND, real native Button HWNDs.
    // Shell v4: LOWORD(lParam)=notification, HIWORD(lParam)=icon ID; signed anchor in wParam.
    // https://learn.microsoft.com/windows/win32/api/shellapi/ns-shellapi-notifyicondataw
    private const uint Dispatch = 0x8002, WM_DPICHANGED = 0x02e0;
    private const int NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, WM_CONTEXTMENU = 0x007b;
    private const uint TTM_ADDTOOL = 0x0432;
    private const uint ICC_STANDARD_CLASSES = 0x4000, ICC_BAR_CLASSES = 0x0004;
    private const int StartupId = 107;
    private const uint PushButton = 0x00, CheckBox = 0x03, OwnerDraw = 0x0b;
    private const int NM_CUSTOMDRAW = -12;
    private const uint CDDS_PREPAINT = 1;
    private const int CDRF_SKIPDEFAULT = 4;
    private const string BrowseLabel = "Browse", StartupLabel = "Launch on Startup";
    private const uint BM_GETCHECK = 0x00f0, BM_SETCHECK = 0x00f1;
    private readonly TrayController controller;
    private readonly string executable = Environment.ProcessPath!;
    private readonly WindowProc procedure;
    private readonly ConcurrentQueue<Action> posted = new();
    private readonly Dictionary<int, nint> buttons = new();
    private readonly nint instance = GetModuleHandle(null);
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private nint hwnd, icon, tooltip, title, statusLabel, font, titleFont;
    private nint background, highlighted, divider;
    private NotifyIcon notification;
    private uint dpi = 96;
    private bool registered, iconAdded, quitting, confirming, operationPending, disposed, positioning;
    private bool startupAvailable;
    private int anchorX, anchorY;
    private string? uiError;
    private string? startupError;
    private static uint Color(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));
    private static readonly uint Slate = Color(32, 44, 53), Text = Color(234, 243, 248), Muted = Color(164, 183, 197);

    internal NativeTrayWindow(TrayController controller)
    {
        this.controller = controller;
        procedure = WindowMessage;
    }

    internal int Run()
    {
        SetThreadDpiAwarenessContext((nint)(-4)); // per-monitor-aware v2; only this STA
        var controls = new CommonControls { Size = (uint)Marshal.SizeOf<CommonControls>(), Classes = ICC_STANDARD_CLASSES | ICC_BAR_CLASSES };
        if (!InitCommonControlsEx(ref controls)) throw new Win32Exception();
        background = CreateSolidBrush(Slate);
        highlighted = CreateSolidBrush(Color(52, 67, 78));
        divider = CreateSolidBrush(Color(64, 80, 91));
        icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "Storage.ico"), 1, 32, 32, 0x10);
        if (icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Storage tray icon could not be loaded.");
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Proc = Marshal.GetFunctionPointerForDelegate(procedure),
            Instance = instance, Icon = icon, SmallIcon = icon, Cursor = LoadCursor(0, (nint)32512),
            Background = background, Name = TrayPolicy.WindowClass, Style = 0x00020000 // CS_DROPSHADOW
        };
        if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception();
        registered = true;
        hwnd = CreateWindowEx(0x00000080 | 0x00010000, TrayPolicy.WindowClass, TrayPolicy.WindowTitle,
            0x80000000 | 0x02000000, 0, 0, TrayPolicy.Width, TrayPolicy.Height, 0, 0, instance, 0); // tool window, control parent, popup; hidden
        if (hwnd == 0) throw new Win32Exception();
        int dark = 1, rounded = 2;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
        DwmSetWindowAttribute(hwnd, 33, ref rounded, 4);
        title = Label("Storage Service", 105);
        statusLabel = Label("Stopped", 106);
        AddButton(101, BrowseLabel, PushButton);
        AddButton(102, "Start service");
        AddButton(103, "Restart service");
        AddButton(StartupId, StartupLabel, CheckBox);
        AddButton(104, "Quit Storage Service");
        tooltip = CreateWindowEx(0x80, "tooltips_class32", "", 0x80000000 | 0x01, 0, 0, 0, 0, hwnd, 0, instance, 0);
        if (tooltip == 0) throw new Win32Exception();
        var tool = new ToolInfo { Size = (uint)Marshal.SizeOf<ToolInfo>(), Flags = 0x01 | 0x10, Hwnd = hwnd, Id = (nuint)buttons[101], Text = "Open Storage Browser" };
        AddTool(tooltip, TTM_ADDTOOL, 0, ref tool);
        dpi = GetDpiForWindow(hwnd);
        ApplyFonts();
        Layout();
        controller.Changed += ControllerChanged;
        AddIcon();
        ReadStartup();
        Refresh();
        Post(() => { if (!quitting) StartAction(() => controller.StartAsync()); });
        while (true)
        {
            int result = GetMessage(out var message, 0, 0, 0);
            if (result == 0) return 0;
            if (result < 0) throw new Win32Exception();
            if (message.Id == 0x0100 && IsWindowVisible(hwnd))
            {
                if (message.WParam == 0x1b) { Hide(); continue; }
                if (message.WParam == 0x0d && buttons.Values.Contains(message.Hwnd))
                { SendMessage(message.Hwnd, 0x00f5, 0, 0); continue; } // BM_CLICK the focused native button
            }
            if (IsWindowVisible(hwnd) && IsDialogMessage(hwnd, ref message)) continue;
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    internal void RequestQuit() => Post(BeginQuit);
    private void ControllerChanged() => Post(Refresh);
    private void Post(Action action)
    {
        if (Volatile.Read(ref disposed)) return;
        posted.Enqueue(action);
        nint target = Interlocked.CompareExchange(ref hwnd, 0, 0);
        if (target != 0) PostMessage(target, Dispatch, 0, 0);
    }

    private nint Label(string text, int id)
    {
        nint label = CreateWindowEx(0, "Static", text, 0x40000000 | 0x10000000 | 0x0000000c | 0x00004000, 0, 0, 0, 0, hwnd, (nint)id, instance, 0); // SS_LEFTNOWORDWRAP + SS_ENDELLIPSIS
        if (label == 0) throw new Win32Exception();
        return label;
    }
    private void AddButton(int id, string accessibleName, uint style = OwnerDraw)
    {
        nint button = CreateWindowEx(0, "Button", accessibleName, 0x40000000 | 0x10000000 | 0x00010000 | style, 0, 0, 0, 0, hwnd, (nint)id, instance, 0); // WS_TABSTOP
        if (button == 0) throw new Win32Exception();
        buttons.Add(id, button);
    }
    private int Px(int logical) => (int)((long)logical * dpi / 96);
    private void ApplyFonts()
    {
        nint oldFont = font, oldTitle = titleFont;
        font = CreateFont(-Px(13), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        titleFont = CreateFont(-Px(14), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        foreach (nint control in buttons.Values.Concat(new[] { statusLabel })) SendMessage(control, 0x0030, (nuint)font, 1);
        SendMessage(title, 0x0030, (nuint)titleFont, 1);
        if (oldFont != 0) DeleteObject(oldFont);
        if (oldTitle != 0) DeleteObject(oldTitle);
    }
    private void Move(nint control, int x, int y, int width, int height) => SetWindowPos(control, 0, Px(x), Px(y), Px(width), Px(height), 0x0014);
    private void Layout()
    {
        Move(title, 50, 17, 146, 20);
        Move(statusLabel, 64, 40, 132, 19);
        Move(buttons[101], 204, 22, 84, 30);
        Move(buttons[102], 6, 76, 292, 38);
        Move(buttons[103], 6, 114, 292, 38);
        Move(buttons[StartupId], 6, 152, 292, 38);
        Move(buttons[104], 6, 202, 292, 38);
    }

    private void Refresh()
    {
        var status = controller.Status;
        string text = status switch
        {
            TrayStatus.Running => "Running", TrayStatus.Starting => "Starting…", TrayStatus.Stopping => "Stopping…",
            TrayStatus.Error => "Error", _ => "Stopped"
        };
        string? failure = uiError ?? controller.Error ?? startupError;
        SetWindowText(statusLabel, failure is null ? text : text + " — " + failure.Replace('\r', ' ').Replace('\n', ' '));
        SetWindowText(buttons[102], status == TrayStatus.Running ? "Stop service" : "Start service");
        bool enabled = !quitting && !operationPending && status is not (TrayStatus.Starting or TrayStatus.Stopping);
        EnableWindow(buttons[102], enabled);
        EnableWindow(buttons[103], enabled && status == TrayStatus.Running);
        EnableWindow(buttons[104], !quitting);
        EnableWindow(buttons[101], !quitting);
        EnableWindow(buttons[StartupId], !quitting && startupAvailable);
        notification.Tip = "Storage — " + text;
        if (iconAdded) { notification.Flags = 0x04 | 0x80; Shell_NotifyIcon(1, ref notification); }
        InvalidateRect(hwnd, 0, true);
        foreach (nint button in buttons.Values) InvalidateRect(button, 0, true);
    }
    private void ReadStartup(string? writeError = null)
    {
        startupError = writeError;
        bool registeredForStartup = false;
        startupAvailable = WindowsStartup.IsInstalledService(executable);
        try { registeredForStartup = startupAvailable && WindowsStartup.IsRegistered(executable); }
        catch (Exception e) when (IsStartupError(e)) { startupError ??= "Cannot read startup setting."; }
        SendMessage(buttons[StartupId], BM_SETCHECK, registeredForStartup ? 1u : 0u, 0);
    }
    private bool ReadStartupCheck() => SendMessage(buttons[StartupId], BM_GETCHECK, 0, 0) == 1;
    private void SetStartup()
    {
        if (!startupAvailable) return;
        // BS_AUTOCHECKBOX changes state before notifying the parent of the click.
        bool enabled = ReadStartupCheck();
        string? writeError = null;
        try { WindowsStartup.SetEnabled(executable, enabled); }
        catch (Exception e) when (IsStartupError(e)) { writeError = "Cannot change startup setting: " + e.Message; }
        // Replace the control's tentative auto-check with the actual registration.
        ReadStartup(writeError);
        Refresh();
    }
    private static bool IsStartupError(Exception e) => e is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    private void AddIcon()
    {
        notification = new NotifyIcon
        {
            Size = (uint)Marshal.SizeOf<NotifyIcon>(), Hwnd = hwnd, Id = TrayPolicy.IconId,
            Flags = 0x01 | 0x02 | 0x04 | 0x80, Callback = TrayPolicy.CallbackMessage, Icon = icon,
            Tip = "Storage — " + controller.Status, Info = "", InfoTitle = ""
        };
        iconAdded = Shell_NotifyIcon(0, ref notification);
        if (iconAdded)
        {
            notification.Version = 4; // NIM_SETVERSION after EVERY add, including TaskbarCreated
            if (!Shell_NotifyIcon(4, ref notification)) { Shell_NotifyIcon(2, ref notification); iconAdded = false; }
        }
        if (iconAdded) KillTimer(hwnd, 1); else SetTimer(hwnd, 1, 1000, 0);
    }
    private void Show(int x, int y)
    {
        anchorX = x; anchorY = y;
        var point = new Point { X = x, Y = y };
        if (x == -1 && y == -1) { GetCursorPos(out point); anchorX = point.X; anchorY = point.Y; }
        nint monitor = MonitorFromPoint(point, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception();
        // Move the actual PMv2 HWND onto the destination monitor, then query its DPI.
        // GetDpiForMonitor is not suitable for a per-monitor-aware calling thread.
        positioning = true;
        try { SetWindowPos(hwnd, 0, info.Work.Left, info.Work.Top, 0, 0, 0x0015); }
        finally { positioning = false; }
        dpi = GetDpiForWindow(hwnd);
        var bounds = TrayPolicy.Place(anchorX, anchorY, dpi, new(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
        SetWindowPos(hwnd, (nint)(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010);
        ApplyFonts(); Layout(); ReadStartup(); Refresh();
        ShowWindow(hwnd, 5);
        // Shell callback grants foreground eligibility. HWND remains service-owned; no arbitrary target.
        SetForegroundWindow(hwnd);
        SetFocus(buttons[101]);
    }
    private void Hide() => ShowWindow(hwnd, 0);
    private void StartAction(Func<Task> action)
    {
        if (operationPending || quitting) return;
        operationPending = true; uiError = null; Refresh();
        _ = ObserveActionAsync(action);
    }
    private async Task ObserveActionAsync(Func<Task> action)
    {
        string? failure = null;
        try { await action().ConfigureAwait(false); }
        catch (Exception e) { failure = e.Message; }
        Post(() => { operationPending = false; uiError = failure; Refresh(); });
    }
    private void BeginQuit()
    {
        if (quitting) return;
        quitting = true; Refresh();
        _ = DrainAndExitAsync();
    }
    private async Task DrainAndExitAsync()
    {
        try { await controller.QuitAsync().ConfigureAwait(false); }
        catch (Exception e) { Post(() => { uiError = e.Message; }); }
        finally
        {
            Post(() => { if (iconAdded) { Shell_NotifyIcon(2, ref notification); iconAdded = false; } DestroyWindow(hwnd); });
        }
    }
    private void EndSession()
    {
        quitting = true;
        try
        {
            if (!controller.EndSession(TimeSpan.FromSeconds(4)))
                Console.Error.WriteLine("Windows session ended before Storage could confirm a complete service drain.");
        }
        finally
        {
            if (iconAdded) { Shell_NotifyIcon(2, ref notification); iconAdded = false; }
            DestroyWindow(hwnd);
        }
    }
    private void Command(int id)
    {
        if (quitting) return;
        switch (id)
        {
            case StartupId: SetStartup(); break;
            case 101:
                try
                {
                    string browser = TrayPolicy.BrowserPath(AppContext.BaseDirectory);
                    if (!File.Exists(browser)) throw new FileNotFoundException("The installed Storage Browser is unavailable.");
                    var start = new ProcessStartInfo(browser) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(browser)! };
                    start.ArgumentList.Add("--from-tray");
                    using var process = Process.Start(start) ?? throw new IOException("Storage Browser did not start.");
                    Hide();
                }
                catch (Exception e) when (e is IOException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
                { uiError = e.Message; Refresh(); }
                break;
            case 102:
                StartAction(async () => { if (controller.Status == TrayStatus.Running) await controller.StopAsync(); else await controller.StartAsync(); });
                break;
            case 103: StartAction(async () => { await controller.RestartAsync(); }); break;
            case 104:
                confirming = true;
                try
                {
                    if (MessageBox(hwnd, "Quit Storage Service?\n\nStorage will stop serving applications until you start it again.", "Quit Storage Service", 0x04 | 0x100 | 0x20) == 6) BeginQuit();
                }
                finally { confirming = false; if (!quitting) { SetForegroundWindow(hwnd); SetFocus(buttons[104]); } }
                break;
        }
    }

    private nint WindowMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == taskbarCreated) { iconAdded = false; AddIcon(); return 0; }
            if (message == TrayPolicy.CallbackMessage)
            {
                var callback = TrayPolicy.Decode(wParam, lParam);
                if (callback.Id == TrayPolicy.IconId && callback.Code is NIN_SELECT or NIN_KEYSELECT or WM_CONTEXTMENU)
                {
                    int x = callback.X, y = callback.Y;
                    if (callback.Code == WM_CONTEXTMENU)
                    {
                        // Version4 does not define wParam coordinates for WM_CONTEXTMENU.
                        var identity = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Hwnd = hwnd, Id = TrayPolicy.IconId };
                        if (Shell_NotifyIconGetRect(ref identity, out var rect) == 0) { x = rect.Right; y = rect.Bottom; }
                        else { GetCursorPos(out var point); x = point.X; y = point.Y; }
                    }
                    if (IsWindowVisible(hwnd) && callback.Code != WM_CONTEXTMENU) Hide(); else Show(x, y);
                }
                return 0;
            }
            switch (message)
            {
                case Dispatch: while (posted.TryDequeue(out var action)) action(); return 0;
                case 0x0111: // WM_COMMAND, accept only exact owned Button notifications
                    int id = (int)(wParam & 0xffff);
                    if ((wParam >> 16) == 0 && buttons.TryGetValue(id, out nint button) && lParam == button) Command(id);
                    return 0;
                case 0x004e when lParam != 0: // WM_NOTIFY
                    var header = Marshal.PtrToStructure<NotifyHeader>(lParam);
                    if (header.Code == NM_CUSTOMDRAW && header.Id is 101 or StartupId &&
                        buttons.TryGetValue((int)header.Id, out nint source) && header.From == source)
                        return PaintHeaderOrStartup(Marshal.PtrToStructure<CustomDraw>(lParam));
                    break;
                case 0x0113: if (wParam == 1) AddIcon(); return 0; // retry Explorer icon registration
                case 0x0006: if ((wParam & 0xffff) == 0 && !confirming) Hide(); break; // WM_ACTIVATE
                case 0x0010: Command(104); return 0; // Alt+F4 confirms quit
                case 0x0011: return 1; // WM_QUERYENDSESSION
                case 0x0016: if (wParam != 0) EndSession(); return 0;
                case 0x0002: PostQuitMessage(0); return 0;
                case WM_DPICHANGED:
                    dpi = (uint)(wParam & 0xffff);
                    ApplyFonts(); Layout();
                    if (!positioning && IsWindowVisible(window)) Show(anchorX, anchorY);
                    return 0;
                case 0x0014: return 1; // WM_ERASEBKGND, painted below
                case 0x000f: PaintWindow(window); return 0;
                case 0x002b: DrawButton(Marshal.PtrToStructure<DrawItem>(lParam)); return 1;
                case 0x0135 when buttons.TryGetValue(StartupId, out nint startup) && lParam == startup: // WM_CTLCOLORBTN
                    SetBkColor((nint)wParam, Slate);
                    SetTextColor((nint)wParam, !quitting && startupAvailable ? Text : Muted);
                    return background;
                case 0x0138: // WM_CTLCOLORSTATIC: native labels preserve their accessible text
                    SetBkMode((nint)wParam, 1); SetBkColor((nint)wParam, Slate);
                    SetTextColor((nint)wParam, lParam == statusLabel ? StatusColor() : lParam == title ||
                        (startupAvailable && !quitting && lParam == buttons.GetValueOrDefault(StartupId)) ? Text : Muted);
                    return background;
            }
        }
        catch (Exception e)
        {
            // Never allow a managed exception to cross the unmanaged WndProc boundary.
            uiError = e.Message;
            if (statusLabel != 0) SetWindowText(statusLabel, "Error — " + uiError);
        }
        return DefWindowProc(window, message, wParam, lParam);
    }
    private uint StatusColor() => controller.Status switch
    {
        TrayStatus.Running => Color(158, 207, 182), TrayStatus.Error => Color(239, 183, 121),
        TrayStatus.Starting or TrayStatus.Stopping => Color(181, 207, 226), _ => Muted
    };
    private void PaintWindow(nint window)
    {
        nint dc = BeginPaint(window, out var paint);
        try
        {
            GetClientRect(window, out var rect); FillRect(dc, ref rect, background);
            DrawIconEx(dc, Px(18), Px(27), icon, Px(22), Px(22), 0, 0, 3);
            var line = new Rect(Px(12), Px(70), Px(292), Px(71)); FillRect(dc, ref line, divider);
            line = new Rect(Px(12), Px(196), Px(292), Px(197)); FillRect(dc, ref line, divider);
            nint dot = CreateSolidBrush(StatusColor());
            nint oldBrush = SelectObject(dc, dot), oldPen = SelectObject(dc, GetStockObject(8)); // NULL_PEN
            try { Ellipse(dc, Px(51), Px(45), Px(59), Px(53)); }
            finally { SelectObject(dc, oldPen); SelectObject(dc, oldBrush); DeleteObject(dot); }
        }
        finally { EndPaint(window, ref paint); }
    }
    private void DrawButton(DrawItem item)
    {
        bool focus = (item.State & 0x10) != 0, pressed = (item.State & 1) != 0, disabled = (item.State & 4) != 0;
        FillRect(item.DC, ref item.Rect, focus || pressed ? highlighted : background);
        SetBkMode(item.DC, 1);
        SetTextColor(item.DC, disabled ? Muted : Text);
        nint previous = SelectObject(item.DC, font);
        try
        {
            string glyph = item.Id switch { 102 => controller.Status == TrayStatus.Running ? "□" : "▶", 103 => "↻", _ => "⏻" };
            var symbol = new Rect(Px(10), 0, Px(30), item.Rect.Bottom);
            DrawText(item.DC, glyph, -1, ref symbol, 0x01 | 0x04 | 0x20);
            string text = item.Id switch { 102 => controller.Status == TrayStatus.Running ? "Stop service" : "Start service", 103 => "Restart service", _ => "Quit Storage Service" };
            var label = new Rect(Px(44), 0, item.Rect.Right - Px(12), item.Rect.Bottom);
            DrawText(item.DC, text, -1, ref label, 0x04 | 0x20 | 0x8000);
            if (focus) { var outline = new Rect(2, 2, item.Rect.Right - 2, item.Rect.Bottom - 2); DrawFocusRect(item.DC, ref outline); }
        }
        finally { SelectObject(item.DC, previous); }
    }
    private nint PaintHeaderOrStartup(CustomDraw draw)
    {
        if (draw.Stage != CDDS_PREPAINT) return 0;
        // Paint only: the native push button and auto-checkbox still own input and accessibility.
        bool focus = GetFocus() == draw.Header.From;
        bool disabled = !IsWindowEnabled(draw.Header.From);
        const uint CDIS_SELECTED = 1, CDIS_HOT = 0x40;
        bool active = !disabled && (draw.State & (CDIS_SELECTED | CDIS_HOT)) != 0;
        FillRect(draw.DC, ref draw.Rect, background);
        SetBkMode(draw.DC, 1);
        nint previous = SelectObject(draw.DC, font);
        try
        {
            if (draw.Header.Id == 101) DrawBrowse(draw.DC, draw.Rect, focus, active, disabled);
            else DrawStartupSwitch(draw.DC, draw.Rect, ReadStartupCheck(), focus, active, disabled);
        }
        finally { SelectObject(draw.DC, previous); }
        return CDRF_SKIPDEFAULT;
    }

    private void DrawBrowse(nint dc, Rect bounds, bool focus, bool active, bool disabled)
    {
        uint ink = disabled ? Muted : Color(224, 247, 238);
        Rounded(dc, bounds, 10, active ? Color(54, 88, 85) : Color(42, 70, 69),
            focus ? Color(168, 223, 197) : Color(79, 114, 106));
        DrawFolder(dc, Px(12), (bounds.Bottom - Px(12)) / 2, ink);
        SetTextColor(dc, ink);
        var label = new Rect(Px(34), 0, bounds.Right - Px(7), bounds.Bottom);
        DrawText(dc, BrowseLabel, -1, ref label, 0x04 | 0x20 | 0x8000);
    }

    private void DrawStartupSwitch(nint dc, Rect bounds, bool check, bool focus, bool active, bool disabled)
    {
        uint accent = Color(168, 223, 197);
        if (active || focus)
        {
            var row = new Rect(bounds.Left, bounds.Top + Px(2), bounds.Right, bounds.Bottom - Px(2));
            Rounded(dc, row, 10, Color(52, 67, 78), focus ? accent : null);
        }
        uint ink = disabled ? Muted : Text;
        int iconTop = (bounds.Bottom - Px(16)) / 2;
        Stroke(dc, Px(12), iconTop, ink, [(3, 10), (3, 13), (15, 13), (15, 10)]);
        Stroke(dc, Px(12), iconTop, ink, [(9, 11), (9, 3), (6, 6), (9, 3), (12, 6)]);
        SetTextColor(dc, ink);
        var label = new Rect(Px(44), 0, bounds.Right - Px(58), bounds.Bottom);
        DrawText(dc, StartupLabel, -1, ref label, 0x04 | 0x20 | 0x8000);

        int right = bounds.Right - Px(12), left = right - Px(32);
        int top = (bounds.Bottom - Px(16)) / 2;
        uint edge = disabled ? Color(111, 130, 142) : check ? accent : Color(139, 163, 176);
        uint track = check && !disabled ? accent : Color(39, 55, 68);
        Rounded(dc, new Rect(left, top, right, top + Px(16)), 16, track, edge);
        int thumbLeft = check ? right - Px(13) : left + Px(3);
        Rounded(dc, new Rect(thumbLeft, top + Px(3), thumbLeft + Px(10), top + Px(13)), 10,
            disabled ? edge : check ? Color(24, 60, 49) : Color(178, 198, 208));
    }

    private void DrawFolder(nint dc, int x, int y, uint color)
    {
        Stroke(dc, x, y, color, [(1, 11), (1, 2), (6, 2), (8, 4), (14, 4), (14, 6)]);
        Stroke(dc, x, y, color, [(1, 11), (4, 6), (17, 6), (14, 11), (1, 11)]);
    }

    private void Stroke(nint dc, int x, int y, uint color, ReadOnlySpan<(int X, int Y)> points)
    {
        nint pen = CreatePen(0, Math.Max(1, Px(1)), color);
        nint previous = SelectObject(dc, pen);
        try
        {
            MoveToEx(dc, x + Px(points[0].X), y + Px(points[0].Y), 0);
            foreach (var point in points[1..]) LineTo(dc, x + Px(point.X), y + Px(point.Y));
        }
        finally { SelectObject(dc, previous); DeleteObject(pen); }
    }

    private void Rounded(nint dc, Rect rect, int diameter, uint fill, uint? stroke = null)
    {
        nint brush = CreateSolidBrush(fill);
        nint pen = stroke is uint edge ? CreatePen(0, Math.Max(1, Px(1)), edge) : GetStockObject(8); // NULL_PEN
        nint oldBrush = SelectObject(dc, brush), oldPen = SelectObject(dc, pen);
        try { RoundRect(dc, rect.Left, rect.Top, rect.Right, rect.Bottom, Px(diameter), Px(diameter)); }
        finally
        {
            SelectObject(dc, oldPen); SelectObject(dc, oldBrush);
            if (stroke.HasValue) DeleteObject(pen);
            DeleteObject(brush);
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        Volatile.Write(ref disposed, true);
        controller.Changed -= ControllerChanged;
        if (iconAdded) { Shell_NotifyIcon(2, ref notification); iconAdded = false; }
        if (hwnd != 0) { KillTimer(hwnd, 1); DestroyWindow(hwnd); hwnd = 0; }
        if (registered) UnregisterClass(TrayPolicy.WindowClass, instance);
        foreach (nint value in new[] { font, titleFont, background, highlighted, divider }) if (value != 0) DeleteObject(value);
        if (icon != 0) DestroyIcon(icon);
        GC.KeepAlive(procedure);
    }
}
