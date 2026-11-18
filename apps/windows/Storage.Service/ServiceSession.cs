using System.Text.Json;
using Storage.Client;
namespace Storage.Service;
// Transport-only session; the Windows host authenticates before invoking this.
public sealed class ServiceSession(NativeCore core, string stagingDirectory)
{
    private static readonly JsonElement Empty = WireJson.Element(new { });
    public async Task RunAsync(Stream connection, CancellationToken token)
    {
        string? staged = null;
        bool responseStarted = false;
        try
        {
            var request = WireJson.Read<WireRequest>(await Frames.ReadAsync(connection, token));
            bool upload = request.Op is "importItem" or "importVersion";
            if (upload) { Directory.CreateDirectory(stagingDirectory); staged = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".partial"); }
            var command = PublicCommands.Build(request, staged);
            if (upload)
            {
                await using (var file = new FileStream(staged!, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    await Frames.WriteAsync(connection, WireJson.Bytes(new WireResponse(true, WireJson.Element(new { ready = true }))), token);
                    await ContentStreams.CopyExactlyAsync(connection, file, request.BodyLength, token);
                    file.Flush(true);
                }
            }
            var response = core.Execute(command);
            if (!response.GetProperty("ok").GetBoolean())
            {
                var error = response.GetProperty("error");
                throw new StorageException(error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
            }
            var result = response.GetProperty("result");
            if (request.Op == "exportVersion")
            {
                // Core has authorized against current policy and verified immutable bytes.
                // Never return the service-only path to the client.
                var version = result.GetProperty("version");
                long length = version.GetProperty("byteLength").GetInt64();
                if (length < 0 || length > ContentStreams.MaxLength) throw new StorageException("InvalidArgument", "Export exceeds 1 GiB local slice limit.");
                await using var file = new FileStream(result.GetProperty("path").GetString()!, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                if (file.Length != length) throw new StorageException("IntegrityError", "Content length changed after verification.");
                responseStarted = true;
                await Frames.WriteAsync(connection, WireJson.Bytes(new WireResponse(true, version, BodyLength: length)), token);
                await ContentStreams.CopyExactlyAsync(file, connection, length, token);
            }
            else { responseStarted = true; await Frames.WriteAsync(connection, WireJson.Bytes(new WireResponse(true, result)), token); }
        }
        catch (Exception error) when (!responseStarted && error is not OperationCanceledException)
        {
            string code = error is StorageException storage ? storage.Code : error is JsonException or InvalidDataException or EndOfStreamException ? "InvalidArgument" : "InternalError";
            // Do not leak native staging paths or filesystem diagnostics across IPC.
            string message = code switch { "PermissionDenied" => "Access denied.", "RevisionConflict" => "This record changed. Refresh before retrying.", "NotFound" => "Record not found.", "IntegrityError" => "Stored content failed integrity verification.", "InvalidArgument" => "Invalid operation, fields, or incomplete transfer.", _ => "The local service could not complete the operation." };
            try { await Frames.WriteAsync(connection, WireJson.Bytes(new WireResponse(false, Empty, new(code, message))), token); }
            catch (IOException) { }
        }
        finally { if (staged is not null && File.Exists(staged)) File.Delete(staged); }
    }
}
