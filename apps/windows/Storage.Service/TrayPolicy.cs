namespace Storage.Service;

internal readonly record struct TrayBounds(int X, int Y, int Width, int Height);
internal static class TrayPolicy
{
    internal const string WindowClass = "Storage.Service.TrayHost";
    internal const string WindowTitle = "Storage Service";
    internal const uint CallbackMessage = 0x8001;
    internal const uint IconId = 1;
    internal static (int Code, int Id, int X, int Y) Decode(nuint wParam, nint lParam) =>
        ((int)((nuint)lParam & 0xffff), (int)(((nuint)lParam >> 16) & 0xffff), unchecked((short)(wParam & 0xffff)), unchecked((short)((wParam >> 16) & 0xffff)));
    internal static TrayBounds Place(int anchorX, int anchorY, uint dpi, TrayBounds work)
    {
        int width = Math.Min(work.Width, (int)(304 * dpi / 96));
        int height = Math.Min(work.Height, (int)(213 * dpi / 96));
        return new(Math.Clamp(anchorX - width, work.X, work.X + work.Width - width),
            Math.Clamp(anchorY - height, work.Y, work.Y + work.Height - height), width, height);
    }
    internal static string BrowserPath(string serviceDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(serviceDirectory));
        if (!directory.Name.Equals("Service", StringComparison.OrdinalIgnoreCase) || directory.Parent is null)
            throw new IOException("The installed Service directory is required to open Storage.");
        return Path.Combine(directory.Parent.FullName, "Storage.Browser.exe");
    }
}
