using System.Text.Json;
namespace Storage.Client;
public sealed record WireRequest(string Op, JsonElement Args, long BodyLength = 0);
public sealed record StorageError(string Code, string Message);
public sealed record WireResponse(bool Ok, JsonElement Result, StorageError? Error = null, long BodyLength = 0);
public sealed class StorageException(string code, string message) : Exception(message) { public string Code { get; } = code; }
public sealed record ItemAttributes(long ByteLength, string Sha256);
public sealed record Item(string Id, string Name, string ContentType, long Revision, string CurrentVersionId, string AccessPolicyId, long AccessPolicyRevision, string CreatedAt, string ModifiedAt, ItemAttributes Attributes, JsonElement Properties);
public sealed record ItemVersion(string Id, string ItemId, string Sha256, long ByteLength, string CreatedAt);
public sealed record AccessPolicy(string Id, long Revision, string OwnerPrincipalId, string[] Readers);
public sealed record Collection(string Id, string Name, long Revision, string CreatedAt);
public sealed record Health(string DeviceId, string DataStoreId, long ItemCount, long VersionCount, string Status);
public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Options);
    public static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static T Read<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException("Empty message.");
}
