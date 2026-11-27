using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Storage.Service.Pairing;

[SupportedOSPlatform("windows")]
internal static class PairingIdentity
{
    internal static X509Certificate2 Open(string directory, bool allowCreate)
    {
        string path = Path.Combine(directory, "identity.dpapi");
        byte[] protectedPfx;
        try { protectedPfx = PairingFiles.Read(path, 64 * 1024); }
        catch (IOException ex) when (allowCreate && ex is FileNotFoundException or DirectoryNotFoundException)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Storage device", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            var now = DateTimeOffset.UtcNow;
            using var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
            byte[] pfx = cert.Export(X509ContentType.Pfx);
            try { protectedPfx = Transform(pfx, protect: true); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            PairingFiles.Write(path, protectedPfx, replace: false);
        }

        byte[] clear = Transform(protectedPfx, protect: false);
        try
        {
            // Schannel cannot use EphemeralKeySet. Use current-user key storage and let
            // the certificate context own the temporary imported key, without PersistKeySet.
            var cert = X509CertificateLoader.LoadPkcs12(clear, (string?)null, X509KeyStorageFlags.UserKeySet);
            if (cert.HasPrivateKey) return cert;
            cert.Dispose();
            throw new IOException("The Storage device identity has no private key.");
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static unsafe byte[] Transform(byte[] input, bool protect)
    {
        fixed (byte* pointer = input)
        {
            var source = new DataBlob { Size = input.Length, Data = (nint)pointer };
            const uint NoUi = 1;
            bool ok = protect
                ? CryptProtectData(ref source, 0, 0, 0, 0, NoUi, out var result)
                : CryptUnprotectData(ref source, 0, 0, 0, 0, NoUi, out result);
            if (!ok) throw new IOException("Windows could not access the Storage device identity.", new Win32Exception(Marshal.GetLastWin32Error()));
            try { return new ReadOnlySpan<byte>((void*)result.Data, result.Size).ToArray(); }
            finally
            {
                CryptographicOperations.ZeroMemory(new Span<byte>((void*)result.Data, result.Size));
                LocalFree(result.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public nint Data; }

    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LocalFree(nint memory);
}
