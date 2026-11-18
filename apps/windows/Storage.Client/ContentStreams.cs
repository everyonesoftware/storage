namespace Storage.Client;
public static class ContentStreams
{
    public static async Task CopyVerifiedAsync(Stream source, Stream target, long length, string expectedSha256, CancellationToken token = default)
    {
        if (length < 0 || length > MaxLength || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid export metadata.");
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        while (length > 0)
        {
            int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), token);
            if (count == 0) throw new EndOfStreamException("Transfer interrupted.");
            hash.AppendData(buffer, 0, count);
            await target.WriteAsync(buffer.AsMemory(0, count), token); length -= count;
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Export content failed SHA-256 verification.");
        await target.FlushAsync(token);
    }
    public const long MaxLength = 1024L * 1024 * 1024;
    public static async Task CopyExactlyAsync(Stream source, Stream target, long length, CancellationToken token = default)
    {
        if (length < 0 || length > MaxLength) throw new InvalidDataException("Transfer exceeds local slice limit (1 GiB).");
        byte[] buffer = new byte[65536];
        while (length > 0)
        {
            int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), token);
            if (count == 0) throw new EndOfStreamException("Transfer interrupted.");
            await target.WriteAsync(buffer.AsMemory(0, count), token); length -= count;
        }
        await target.FlushAsync(token);
    }
}
