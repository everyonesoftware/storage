using System.Security.Cryptography;
using System.Text;

namespace Storage.Browser;

internal static class ActivationIdentity
{
    public static string Name(string executable, string userSid) => "Local\\Storage.Browser." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userSid + "\0" + Path.GetFullPath(executable).ToUpperInvariant())));
}
