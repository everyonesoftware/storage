using System.Text;

namespace Storage.Service;

// Only the native boundary is substituted. Send the same WM_COMMAND/WM_CLOSE messages to the real window procedure.
internal static class TrayWin32
{
    internal struct Point { public int X, Y; }
    internal readonly record struct Rect(int Left, int Top, int Right, int Bottom);
    internal struct MonitorInfo { public uint Size; public Rect Work; }
    internal struct WindowClass
    {
        public uint Size;
        public nint Proc, Instance, Cursor, Icon, SmallIcon, Background;
        public string Name;
    }
    internal struct Message { public nint Hwnd; public uint Id; public nuint WParam; }
    internal delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);
    private sealed record Control(string Kind, string Text, bool Enabled = true);
    private static readonly Dictionary<nint, Control> Controls = [];
    private static int next;
    private static WindowProc? dispatch;
    private static nint focus;
    internal static nint Window { get; private set; }
    internal static void Click(string text)
    {
        nint handle = Controls.Single(x => x.Value.Kind == "Button" && x.Value.Text == text).Key;
        dispatch!(Window, 0x0111, 0, handle);
    }
    internal static void Close() => dispatch!(Window, 0x0010, 0, 0);
    internal static bool HasText(string text) => Controls.Values.Any(c => c.Text.Contains(text, StringComparison.Ordinal));
    internal static bool Disabled(string text) => !Controls.Values.Single(c => c.Text == text).Enabled;
    internal static nint GetModuleHandle(string? name) => 1;
    internal static nint LoadCursor(nint module, nint id) => 1;
    internal static int RegisterClassEx(ref WindowClass windowClass)
    {
        dispatch = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<WindowProc>(windowClass.Proc);
        return 1;
    }
    internal static void UnregisterClass(string name, nint instance) => dispatch = null;
    internal static nint MonitorFromPoint(Point point, int flags) => 1;
    internal static bool GetMonitorInfo(nint monitor, ref MonitorInfo info)
    {
        info.Work = new Rect(0, 0, 1920, 1080);
        return true;
    }
    internal static nint CreateWindowEx(uint extended, string kind, string text, uint style, int x, int y,
        int width, int height, nint parent, nint id, nint instance, nint parameters)
    {
        nint handle = ++next;
        if (parent == 0) Window = handle;
        else Controls[handle] = new Control(kind, text);
        return handle;
    }
    internal static void DestroyWindow(nint handle)
    {
        if (handle == Window) { Window = focus = 0; Controls.Clear(); }
        else Controls.Remove(handle);
    }
    internal static bool IsChild(nint parent, nint child) => parent == Window && Controls.ContainsKey(child);
    internal static void EnableWindow(nint handle, bool enabled) => Controls[handle] = Controls[handle] with { Enabled = enabled };
    internal static bool IsWindowEnabled(nint handle) => Controls[handle].Enabled;
    internal static void SetWindowText(nint handle, string text) => Controls[handle] = Controls[handle] with { Text = text };
    internal static void GetWindowText(nint handle, StringBuilder text, int capacity) => text.Append(Controls[handle].Text);
    internal static nint GetFocus() => focus;
    internal static void SetFocus(nint handle) => focus = handle;
    internal static nint GetForegroundWindow() => Window;
    internal static void SetForegroundWindow(nint handle) { }
    internal static void ShowWindow(nint handle, int command) { }
    internal static void DwmSetWindowAttribute(nint handle, int attr, ref int value, int size) { }
    internal static uint GetDpiForWindow(nint handle) => 96;
    internal static bool IsDialogMessage(nint handle, ref Message message) => false;
    internal static void SetTextColor(nint dc, uint color) { }
    internal static void SetBkColor(nint dc, uint color) { }
    internal static uint GetSysColor(int index) => 0;
    internal static nint GetSysColorBrush(int index) => 0;
    internal static void MessageBox(nint handle, string text, string title, uint type) => throw new Exception(text);
    internal static nint DefWindowProc(nint handle, uint message, nuint wParam, nint lParam) => 0;
    internal static void SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags) { }
    internal static void InvalidateRect(nint handle, nint rect, bool erase) { }
    internal static nint CreateFont(int height, int width, int esc, int orientation, int weight, uint italic,
        uint underline, uint strike, uint charset, uint output, uint clip, uint quality, uint family, string face) => 1;
    internal static void DeleteObject(nint handle) { }
    internal static nint SendMessage(nint handle, uint message, nuint wParam, nint lParam) => 0;
    internal static nint SendText(nint handle, uint message, nuint wParam, string text) => 0;
    internal static nint BeginPaint(nint handle, out int paint) { paint = 0; return 0; }
    internal static void EndPaint(nint handle, ref int paint) { }
    internal static bool GetClientRect(nint handle, out Rect rect) { rect = default; return false; }
    internal static void FillRect(nint dc, ref Rect rect, nint brush) { }
    internal static nint GetStockObject(int index) => 0;
}
