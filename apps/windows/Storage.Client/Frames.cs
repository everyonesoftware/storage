using System.Buffers.Binary;
namespace Storage.Client;
public static class Frames
{
    public const int Limit = 1024 * 1024;
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        if (bytes.Length > Limit) throw new InvalidDataException("Metadata frame exceeds limit.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken token = default)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > Limit) throw new InvalidDataException("Invalid metadata frame length.");
        byte[] result = new byte[length]; await stream.ReadExactlyAsync(result, token); return result;
    }
}
