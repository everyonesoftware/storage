using System.Text.Json;
using Storage.Client;
namespace Storage.Service;
public static class PublicCommands
{
    public const long MaxContentLength = 1024L * 1024 * 1024;
    public static JsonElement Build(WireRequest request, string? stagedPath = null)
    {
        if (request.Args.ValueKind != JsonValueKind.Object || request.BodyLength < 0 || request.BodyLength > MaxContentLength) throw Invalid();
        string[] fields = request.Op switch
        {
            "health" or "listItems" or "listCollections" => [],
            "getItem" or "listVersions" => ["itemId"],
            "exportVersion" => ["versionId"],
            "importItem" => ["name", "contentType"],
            "importVersion" => ["itemId", "expectedRevision"],
            "renameItem" => ["itemId", "expectedRevision", "name"],
            "setAccessPolicy" => ["itemId", "expectedRevision", "readers"],
            "createCollection" => ["name"],
            "listCollectionItems" => ["collectionId"],
            "addToCollection" or "removeFromCollection" => ["collectionId", "itemId", "expectedRevision"],
            "deleteCollection" => ["collectionId", "expectedRevision"],
            _ => throw Invalid()
        };
        var supplied = request.Args.EnumerateObject().ToArray();
        if (supplied.Length != fields.Length || supplied.Select(p => p.Name).Distinct().Count() != fields.Length || supplied.Any(p => !fields.Contains(p.Name))) throw Invalid();
        foreach (var field in fields)
        {
            var value = request.Args.GetProperty(field);
            if (field == "expectedRevision") { if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long revision) || revision < 1) throw Invalid(); }
            else if (field == "readers") { if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 100 || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()) || v.GetString()!.Length > 256)) throw Invalid(); }
            else if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 1024 || value.GetString()!.Contains('\0')) throw Invalid();
        }
        bool upload = request.Op is "importItem" or "importVersion";
        if (!upload && request.BodyLength != 0) throw Invalid();
        if (upload && string.IsNullOrEmpty(stagedPath)) throw Invalid();
        var args = supplied.ToDictionary(p => p.Name, p => (object)p.Value);
        if (upload) args["sourcePath"] = stagedPath!;
        return WireJson.Element(new { op = request.Op switch { "importItem" => "createItem", "importVersion" => "createVersion", "exportVersion" => "readVersion", _ => request.Op }, args });
    }
    private static StorageException Invalid() => new("InvalidArgument", "Unsupported public operation, fields, or transfer length.");
}
