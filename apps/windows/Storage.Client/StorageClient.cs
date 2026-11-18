using System.Text.Json;
namespace Storage.Client;
public sealed record ItemPage(Item[] Items, bool HasMore = false);
public sealed record VersionPage(ItemVersion[] Versions, bool HasMore = false);
public sealed record CollectionPage(Collection[] Collections, bool HasMore = false);
public sealed class StorageClient(Func<CancellationToken, Task<Stream>> connect)
{
    private static WireResponse ReadResponse(byte[] bytes)
    {
        var response = WireJson.Read<WireResponse>(bytes);
        if (!response.Ok) throw new StorageException(response.Error?.Code ?? "InternalError", response.Error?.Message ?? "Service failed.");
        return response;
    }
    private async Task<T> Call<T>(string op, object args, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await using var stream = await connect(timeout.Token);
        await Frames.WriteAsync(stream, WireJson.Bytes(new WireRequest(op, WireJson.Element(args))), timeout.Token);
        var response = ReadResponse(await Frames.ReadAsync(stream, timeout.Token));
        return response.Result.Deserialize<T>(WireJson.Options) ?? throw new InvalidDataException("Missing result.");
    }
    public Task<Health> HealthAsync(CancellationToken token = default) => Call<Health>("health", new { }, token);
    public Task<ItemPage> ListItemsAsync(CancellationToken token = default) => Call<ItemPage>("listItems", new { }, token);
    public Task<Item> GetItemAsync(string itemId, CancellationToken token = default) => Call<Item>("getItem", new { itemId }, token);
    public Task<VersionPage> ListVersionsPageAsync(string itemId, CancellationToken token = default) => Call<VersionPage>("listVersions", new { itemId }, token);
    public async Task<ItemVersion[]> ListVersionsAsync(string itemId, CancellationToken token = default)
    {
        var page = await ListVersionsPageAsync(itemId, token);
        if (page.HasMore) throw new StorageException("ListingTruncated", "More versions exist. Use the page API to inspect the explicit truncation state.");
        return page.Versions;
    }
    public Task<Item> RenameAsync(string itemId, long expectedRevision, string name, CancellationToken token = default) => Call<Item>("renameItem", new { itemId, expectedRevision, name }, token);
    public Task<AccessPolicy> SetAccessPolicyAsync(string itemId, long expectedRevision, string[] readers, CancellationToken token = default) => Call<AccessPolicy>("setAccessPolicy", new { itemId, expectedRevision, readers }, token);
    public Task<Collection> RemoveFromCollectionAsync(string collectionId, string itemId, long expectedRevision, CancellationToken token = default) => Call<Collection>("removeFromCollection", new { collectionId, itemId, expectedRevision }, token);
    public async Task DeleteCollectionAsync(string collectionId, long expectedRevision, CancellationToken token = default) => _ = await Call<JsonElement>("deleteCollection", new { collectionId, expectedRevision }, token);
    public Task<Collection> CreateCollectionAsync(string name, CancellationToken token = default) => Call<Collection>("createCollection", new { name }, token);
    public Task<CollectionPage> ListCollectionsPageAsync(CancellationToken token = default) => Call<CollectionPage>("listCollections", new { }, token);
    public async Task<Collection[]> ListCollectionsAsync(CancellationToken token = default)
    {
        var page = await ListCollectionsPageAsync(token);
        if (page.HasMore) throw new StorageException("ListingTruncated", "More collections exist. Use the page API to inspect the explicit truncation state.");
        return page.Collections;
    }
    public Task<Collection> AddToCollectionAsync(string collectionId, string itemId, long expectedRevision, CancellationToken token = default) => Call<Collection>("addToCollection", new { collectionId, itemId, expectedRevision }, token);
    public Task<ItemPage> ListCollectionItemsAsync(string collectionId, CancellationToken token = default) => Call<ItemPage>("listCollectionItems", new { collectionId }, token);
    public Task<Item> ImportAsync(string name, string contentType, Stream source, long length, CancellationToken token = default) => Upload("importItem", new { name, contentType }, source, length, token);
    public Task<Item> ImportVersionAsync(string itemId, long expectedRevision, Stream source, long length, CancellationToken token = default) => Upload("importVersion", new { itemId, expectedRevision }, source, length, token);
    private async Task<Item> Upload(string op, object args, Stream source, long length, CancellationToken token)
    {
        if (length < 0 || length > ContentStreams.MaxLength) throw new ArgumentOutOfRangeException(nameof(length));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await using var stream = await connect(timeout.Token);
        await Frames.WriteAsync(stream, WireJson.Bytes(new WireRequest(op, WireJson.Element(args), length)), timeout.Token);
        _ = ReadResponse(await Frames.ReadAsync(stream, timeout.Token));
        await ContentStreams.CopyExactlyAsync(source, stream, length, timeout.Token);
        return ReadResponse(await Frames.ReadAsync(stream, timeout.Token)).Result.Deserialize<Item>(WireJson.Options) ?? throw new InvalidDataException("Missing Item.");
    }
    public async Task<ItemVersion> ExportAsync(string versionId, Stream destination, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await using var stream = await connect(timeout.Token);
        await Frames.WriteAsync(stream, WireJson.Bytes(new WireRequest("exportVersion", WireJson.Element(new { versionId }))), timeout.Token);
        var response = ReadResponse(await Frames.ReadAsync(stream, timeout.Token));
        var version = response.Result.Deserialize<ItemVersion>(WireJson.Options) ?? throw new InvalidDataException("Missing version.");
        if (response.BodyLength != version.ByteLength) throw new InvalidDataException("Export length mismatch.");
        await ContentStreams.CopyVerifiedAsync(stream, destination, response.BodyLength, version.Sha256, timeout.Token);
        return version;
    }
}
