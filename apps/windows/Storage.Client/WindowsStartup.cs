using Microsoft.Win32;
using System.Runtime.Versioning;

namespace Storage.Client;

public static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "Everyonesoftware.Storage.Service";

    public static bool IsInstalledService(string executable)
    {
        var service = new FileInfo(executable);
        var directory = service.Directory;
        var current = directory?.Parent;
        return service.Name.Equals("Storage.Service.exe", StringComparison.OrdinalIgnoreCase)
            && directory?.Name.Equals("Service", StringComparison.OrdinalIgnoreCase) == true
            && current?.Name.Equals("current", StringComparison.OrdinalIgnoreCase) == true
            && current.Parent is { } root && File.Exists(Path.Combine(root.FullName, "Update.exe"))
            && service.Exists;
    }

    internal static string Command(string executable) => '"' + Path.GetFullPath(executable) + '"';

    [SupportedOSPlatform("windows")]
    public static bool IsRegistered(string executable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return IsRegistered(key, executable);
    }

    [SupportedOSPlatform("windows")]
    internal static bool IsRegistered(RegistryKey? key, string executable) => Owns(Read(key), Command(executable));

    [SupportedOSPlatform("windows")]
    public static void SetEnabled(string executable, bool enabled)
    {
        if (!IsInstalledService(executable)) throw new IOException("Install Storage to use Launch on Startup.");
        using var key = enabled ? Registry.CurrentUser.CreateSubKey(RunKey) : Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is not null) SetEnabled(key, executable, enabled);
    }

    [SupportedOSPlatform("windows")]
    internal static void SetEnabled(RegistryKey key, string executable, bool enabled) =>
        SetEnabled(executable, enabled, () => Read(key), value => key.SetValue(ValueName, value, RegistryValueKind.String),
            () => key.DeleteValue(ValueName, throwOnMissingValue: false));

    internal static void SetEnabled(string executable, bool enabled, Func<object?> read, Action<string> write, Action delete)
    {
        string command = Command(executable);
        object? existing = read();
        bool owned = Owns(existing, command);
        if (!enabled)
        {
            if (owned) delete();
            return;
        }
        if (owned) return;
        if (existing is not null) throw new IOException("A different Storage startup entry already exists.");
        if (command.Length > 260) throw new IOException("The Storage installation path is too long for Windows startup.");
        write(command);
    }

    [SupportedOSPlatform("windows")]
    public static void Remove(string executable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is not null) Remove(key, executable);
    }

    [SupportedOSPlatform("windows")]
    internal static void Remove(RegistryKey key, string executable)
    {
        if (IsRegistered(key, executable)) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    [SupportedOSPlatform("windows")]
    private static object? Read(RegistryKey? key) => key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

    private static bool Owns(object? existing, string command) => existing is string value && value.Equals(command, StringComparison.OrdinalIgnoreCase);
}
