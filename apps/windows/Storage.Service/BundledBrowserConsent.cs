using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
namespace Storage.Service;

// Only the service constructs this target. No approval argument accepts a caller path.
// This per-user full-trust boundary is not a sandbox against same-user malware.
public sealed class BundledBrowserConsent(string serviceDirectory, string configDirectory)
{
    private string GrantFile => Path.Combine(configDirectory, "bundled-browser-grant.json");
    private string Browser
    {
        get
        {
            var service = new DirectoryInfo(Path.GetFullPath(serviceDirectory));
            var current = service.Parent;
            if (service.Name != "Service" || current?.Name != "current" || current.Parent is null || !File.Exists(Path.Combine(current.Parent.FullName, "Update.exe")))
                throw new IOException("Installed Velopack bundle required for Browser approval.");
            string browser = Path.Combine(current.FullName, "Storage.Browser.exe");
            foreach (string path in new[] { browser, Path.ChangeExtension(browser, ".dll"), service.FullName, current.FullName, current.Parent.FullName })
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked bundle targets are not supported.");
            return browser;
        }
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    public DevelopmentGrant? Load()
    {
        try
        {
            string browser = Browser;
            if ((File.GetAttributes(configDirectory) & FileAttributes.ReparsePoint) != 0) return null;
            if ((File.GetAttributes(GrantFile) & FileAttributes.ReparsePoint) != 0 || new FileInfo(GrantFile).Length > 4096) return null;
            var grant = JsonSerializer.Deserialize<DevelopmentGrant>(File.ReadAllText(GrantFile));
            return grant is not null && grant.ExecutablePath == browser && grant.Sha256 == Hash(browser) && grant.AssemblySha256 == Hash(Path.ChangeExtension(browser, ".dll")) ? grant : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }
    public bool RequestApproval(Func<string, bool> confirm)
    {
        string browser = Browser;
        var grant = new DevelopmentGrant(browser, Hash(browser), Hash(Path.ChangeExtension(browser, ".dll")));
        if (!confirm(browser)) return false;
        // If bytes changed while consent was open, do not approve the replacement.
        if (Hash(browser) != grant.Sha256 || Hash(Path.ChangeExtension(browser, ".dll")) != grant.AssemblySha256) return false;
        Directory.CreateDirectory(configDirectory);
        if ((File.GetAttributes(configDirectory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked grant configuration rejected.");
        if (OperatingSystem.IsWindows()) ProtectConfigurationDirectory(configDirectory);
        string temporary = Path.Combine(configDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(grant)); File.Move(temporary, GrantFile, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }
    [SupportedOSPlatform("windows")]
    private static void ProtectConfigurationDirectory(string path)
    {
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        System.IO.FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
    }
    [SupportedOSPlatform("windows")]
    public static bool ConfirmOnWindows(string browser) => MessageBox(IntPtr.Zero,
        "Allow the bundled Storage Browser to access your local Storage service?\n\n" + browser +
        "\n\nThis grants local repository operations to this exact Browser EXE and DLL. Changed versions require approval again. Only approve an installation you trust. Unsigned software is not publisher-verified.",
        "Storage Service — Browser approval", 0x00000004 | 0x00000100 | 0x00000020 | 0x00010000) == 6; // YESNO, DEFBUTTON2 (No), QUESTION, SETFOREGROUND
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
