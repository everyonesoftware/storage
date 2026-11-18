using System.Security.Cryptography;
namespace Storage.Service;
public sealed record ProcessIdentity(string PackageFamily, string ExecutablePath);
public sealed record DevelopmentGrant(string ExecutablePath, string Sha256, string AssemblySha256);
public static class AppAuthorization
{
    // Identity values MUST come from a held OS process handle, never a request.
    public static bool IsAllowed(ProcessIdentity service, ProcessIdentity caller, DevelopmentGrant? development)
    {
        if (!string.IsNullOrEmpty(service.PackageFamily) && service.PackageFamily == caller.PackageFamily && Path.GetFileName(caller.ExecutablePath).Equals("Storage.Browser.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (development is null || caller.PackageFamily.Length != 0 || !Path.GetFullPath(caller.ExecutablePath).Equals(Path.GetFullPath(development.ExecutablePath), StringComparison.OrdinalIgnoreCase)) return false;
        using var file = File.OpenRead(caller.ExecutablePath);
        using var assembly = File.OpenRead(Path.ChangeExtension(caller.ExecutablePath, ".dll"));
        return Convert.ToHexString(SHA256.HashData(file)).Equals(development.Sha256, StringComparison.OrdinalIgnoreCase)
            && Convert.ToHexString(SHA256.HashData(assembly)).Equals(development.AssemblySha256, StringComparison.OrdinalIgnoreCase);
    }
}
