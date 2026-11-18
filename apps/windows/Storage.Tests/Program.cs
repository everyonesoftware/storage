using Storage.Client;
using Storage.Service;
using System.Text;
using System.Text.Json;

await Storage.Update.Tests.UpdateTestSuite.Run();
await Storage.Tray.Tests.TrayTestSuite.Run();
await Storage.Browser.Activation.Tests.ActivationTestSuite.Run();

static void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
string xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "BrowserSource/Shell.xaml"));
var shell = System.Xml.Linq.XDocument.Parse(xaml);
var x = System.Xml.Linq.XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
var controls = shell.Descendants().Where(e => e.Attribute(x + "Name") is not null).ToDictionary(e => e.Attribute(x + "Name")!.Value, e => e.Name.LocalName);
string windowSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "BrowserSource/MainWindow.cs.txt"));
var lookups = System.Text.RegularExpressions.Regex.Matches(windowSource, "C<([A-Za-z]+)>\\(\"([^\"]+)\"\\)");
Check(lookups.Count > 0 && lookups.All(m => controls.TryGetValue(m.Groups[2].Value, out var type) && type == m.Groups[1].Value), "runtime XAML typed named-control lookups match shell");
Check(!xaml.Contains("x:Class") && !xaml.Contains("x:Bind") && !xaml.Contains("Click="), "runtime XAML does not require generated code");
var bytes = Encoding.UTF8.GetBytes("framed metadata — no line delimiters\n");
using var wire = new MemoryStream();
await Frames.WriteAsync(wire, bytes);
wire.Position = 0;
Check((await Frames.ReadAsync(wire)).SequenceEqual(bytes), "bounded frame round trip");
try { await ContentStreams.CopyVerifiedAsync(new MemoryStream(bytes), new MemoryStream(), bytes.Length, new string('0', 64)); throw new Exception("Accepted content hash mismatch"); }
catch (InvalidDataException) { Check(true, "SDK detects corrupted export bytes"); }
// A peer's explicit truncation flag must not be discarded by array conveniences.
async Task<T> WithWireResult<T>(object result, Func<StorageClient, Task<T>> action)
{
    string pipeName = "storage-page-fixture-" + Guid.NewGuid().ToString("N");
    async Task ServeFixture()
    {
        await using var server = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync();
        _ = await Frames.ReadAsync(server);
        await Frames.WriteAsync(server, WireJson.Bytes(new WireResponse(true, WireJson.Element(result))));
    }
    var servingFixture = ServeFixture();
    var sdk = new StorageClient(async token => { var connection = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous); await connection.ConnectAsync(5000, token); return connection; });
    try { return await action(sdk); }
    finally { await servingFixture; }
}
try { await WithWireResult(new { versions = Array.Empty<ItemVersion>(), hasMore = true }, c => c.ListVersionsAsync("item")); throw new Exception("SDK silently discarded version truncation"); }
catch (StorageException ex) { Check(ex.Code == "ListingTruncated", "SDK does not silently discard version truncation"); }
try { await WithWireResult(new { collections = Array.Empty<Collection>(), hasMore = true }, c => c.ListCollectionsAsync()); throw new Exception("SDK silently discarded collection truncation"); }
catch (StorageException ex) { Check(ex.Code == "ListingTruncated", "SDK does not silently discard collection truncation"); }
try { PublicCommands.Build(new WireRequest("renameItem", WireJson.Element(new { itemId = "id", expectedRevision = "1", name = "x" }))); throw new Exception("Accepted string revision"); }
catch (StorageException ex) { Check(ex.Code == "InvalidArgument", "wrong revision type is a public argument error"); }
var unsafeRequest = new WireRequest("importItem", JsonSerializer.SerializeToElement(new { name = "x", contentType = "text/plain", sourcePath = "C:/private", actorJson = "owner" }), 0);
try { PublicCommands.Build(unsafeRequest, "service-staged"); throw new Exception("Accepted internal arguments"); }
catch (StorageException ex) { Check(ex.Code == "InvalidArgument", "public API rejects sourcePath and actorJson"); }
var import = new WireRequest("importItem", JsonSerializer.SerializeToElement(new { name = "x", contentType = "text/plain" }), 3);
Check(!AppAuthorization.IsAllowed(new("", ""), new("", ""), null), "unpackaged applications default denied");
Check(!AppAuthorization.IsAllowed(new("Storage_Family", "service"), new("Other_Family", "Storage.Browser.exe"), null), "unregistered package denied");
Check(!AppAuthorization.IsAllowed(new("Storage_Family", "service"), new("Storage_Family", "Other.exe"), null), "same package does not authorize unrelated executable");
Check(AppAuthorization.IsAllowed(new("Storage_Family", "service"), new("Storage_Family", "Storage.Browser.exe"), null), "bundled packaged Browser allowed by family and executable");
string authScratch = Path.Combine(Path.GetTempPath(), "storage-grant-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(authScratch);
try
{
    string exe = Path.Combine(authScratch, "Storage.Browser.exe"), dll = Path.Combine(authScratch, "Storage.Browser.dll");
    File.WriteAllText(exe, "test policy fixture executable"); File.WriteAllText(dll, "test policy fixture assembly");
    string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    var grant = new DevelopmentGrant(exe, Hash(exe), Hash(dll));
    Check(AppAuthorization.IsAllowed(new("", ""), new("", exe), grant), "explicit development decision accepts both pinned file hashes");
    File.AppendAllText(dll, "changed");
    Check(!AppAuthorization.IsAllowed(new("", ""), new("", exe), grant), "development managed assembly change invalidates grant");
}
finally { Directory.Delete(authScratch, true); }
string installedScratch = Path.Combine(Path.GetTempPath(), "storage-consent-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(installedScratch, "current", "Service"));
try
{
    string serviceDirectory = Path.Combine(installedScratch, "current", "Service");
    string browser = Path.Combine(installedScratch, "current", "Storage.Browser.exe");
    string config = Path.Combine(installedScratch, "private-config");
    File.WriteAllText(browser, "bundled exe fixture"); File.WriteAllText(Path.ChangeExtension(browser, ".dll"), "bundled dll fixture");
    File.WriteAllText(Path.Combine(installedScratch, "Update.exe"), "installed layout fixture");
    var grants = new BundledBrowserConsent(serviceDirectory, config);
    Check(grants.Load() is null, "installed Browser requires approval initially");
    Check(!grants.RequestApproval(_ => false) && grants.Load() is null, "denied service consent persists no grant");
    Check(grants.RequestApproval(_ => true), "explicit service consent saves bundled grant");
    var restored = new BundledBrowserConsent(serviceDirectory, config).Load();
    Check(restored is not null && AppAuthorization.IsAllowed(new("", "service"), new("", browser), restored), "approved installed grant survives service restart");
    Check(!AppAuthorization.IsAllowed(new("", "service"), new("", Path.Combine(installedScratch, "Other.exe")), restored), "bundled grant denies other caller location");
    File.AppendAllText(Path.ChangeExtension(browser, ".dll"), "tampered");
    Check(grants.Load() is null, "stale managed assembly hash denies persisted grant");
    Check(grants.RequestApproval(_ => true), "changed installed bundle requires new approval");
    File.AppendAllText(browser, "replaced exe");
    Check(grants.Load() is null, "stale executable hash denies persisted grant");
    Check(grants.RequestApproval(_ => true), "replaced executable requires explicit new consent");
    var approved = grants.Load()!;
    File.WriteAllText(Path.Combine(config, "bundled-browser-grant.json"), JsonSerializer.Serialize(approved with { ExecutablePath = Path.Combine(installedScratch, "Other.exe") }));
    Check(grants.Load() is null, "tampered persisted caller location denied");
    File.WriteAllText(Path.Combine(config, "bundled-browser-grant.json"), "{malformed");
    Check(grants.Load() is null, "tampered configuration fails closed");
    Directory.Delete(Path.Combine(installedScratch, "current"), true);
    Check(grants.Load() is null, "missing bundled payload fails closed");
}
finally { Directory.Delete(installedScratch, true); }
var command = PublicCommands.Build(import, "private-stage");
Check(command.GetProperty("op").GetString() == "createItem" && command.GetProperty("args").GetProperty("sourcePath").GetString() == "private-stage", "host constructs internal staged command");
// These harness connections exercise framing/dispatch only. They do NOT bypass or
// impersonate Windows authorization in the production server.
string scratch = Path.Combine(Path.GetTempPath(), "storage-csharp-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    using var engine = new NativeCore(Path.Combine(scratch, "repository"), args.FirstOrDefault() ?? "missing-native-library");
    var session = new ServiceSession(engine, Path.Combine(scratch, "staging"));
    string pipe = "storage-test-" + Guid.NewGuid().ToString("N");
    async Task ServeOnce()
    {
        await using var server = new System.IO.Pipes.NamedPipeServerStream(pipe, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await session.RunAsync(server, timeout.Token);
    }
    var serving = ServeOnce();
    await using (var conn = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
    {
        await conn.ConnectAsync(5000);
        await Frames.WriteAsync(conn, WireJson.Bytes(unsafeRequest));
        var rejected = WireJson.Read<WireResponse>(await Frames.ReadAsync(conn));
        Check(!rejected.Ok && rejected.Error?.Code == "InvalidArgument", "real pipe rejects internal argument tunneling before core load");
    }
    await serving;
    var interrupted = ServeOnce();
    await using (var conn = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
    {
        await conn.ConnectAsync(5000);
        await Frames.WriteAsync(conn, WireJson.Bytes(new WireRequest("importItem", WireJson.Element(new { name = "incomplete", contentType = "text/plain" }), 100)));
        Check(WireJson.Read<WireResponse>(await Frames.ReadAsync(conn)).Ok, "upload readiness handshake");
        await conn.WriteAsync(new byte[] { 42 });
    }
    await interrupted;
    Check(!Directory.EnumerateFiles(Path.Combine(scratch, "staging")).Any(), "interrupted upload removes staged bytes before native commit");
    if (args.Length > 0)
    {
        async Task<T> WithClient<T>(Func<StorageClient, Task<T>> action)
        {
            var serve = ServeOnce();
            var client = new StorageClient(async token => { var stream = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous); await stream.ConnectAsync(5000, token); return stream; });
            try { return await action(client); }
            finally { await serve; }
        }
        byte[] content = Encoding.UTF8.GetBytes("Storage native IPC roundtrip\n");
        var item = await WithClient(c => c.ImportAsync("roundtrip.txt", "text/plain", new MemoryStream(content), content.Length));
        Check(item.Attributes.ByteLength == content.Length && item.Revision == 1, "native import metadata");
        var listed = await WithClient(c => c.ListItemsAsync());
        Check(listed.Items.Single().Id == item.Id, "native list through SDK");
        byte[] changed = Encoding.UTF8.GetBytes("Second immutable version\n");
        var updated = await WithClient(c => c.ImportVersionAsync(item.Id, item.Revision, new MemoryStream(changed), changed.Length));
        Check(updated.Revision == 2, "native version revision");
        var history = await WithClient(c => c.ListVersionsAsync(item.Id));
        Check(history.Length == 2 && history.Select(v => v.Id).Distinct().Count() == 2, "immutable version history");
        using var output = new MemoryStream();
        await WithClient(async c => { await c.ExportAsync(item.CurrentVersionId, output); return true; });
        Check(output.ToArray().SequenceEqual(content), "historical export bytes preserved");
        var policy = await WithClient(c => c.SetAccessPolicyAsync(item.Id, item.AccessPolicyRevision, ["reader-fixture"]));
        Check(policy.Revision == item.AccessPolicyRevision + 1 && policy.OwnerPrincipalId == "local-owner", "independent access policy revision via SDK");
        var policyItem = await WithClient(c => c.GetItemAsync(item.Id));
        Check(policyItem.Revision == updated.Revision && policyItem.AccessPolicyRevision == policy.Revision, "policy edits do not mutate content revision");
        var collection = await WithClient(c => c.CreateCollectionAsync("Project"));
        collection = await WithClient(c => c.AddToCollectionAsync(collection.Id, item.Id, collection.Revision));
        Check((await WithClient(c => c.ListCollectionItemsAsync(collection.Id))).Items.Single().Id == item.Id, "collection membership through SDK");
        collection = await WithClient(c => c.RemoveFromCollectionAsync(collection.Id, item.Id, collection.Revision));
        Check((await WithClient(c => c.ListCollectionItemsAsync(collection.Id))).Items.Length == 0, "collection removal preserves independent item");
        await WithClient(async c => { await c.DeleteCollectionAsync(collection.Id, collection.Revision); return true; });
        Check((await WithClient(c => c.ListItemsAsync())).Items.Length == 1, "collection deletion does not delete item");
        try { await WithClient(c => c.RenameAsync(item.Id, 1, "stale")); throw new Exception("Accepted stale revision"); }
        catch (StorageException ex) { Check(ex.Code == "RevisionConflict", "revision conflict crosses IPC"); }
        Check(!Directory.EnumerateFiles(Path.Combine(scratch, "staging")).Any(), "staging cleaned after transfers");
        using var reopened = new NativeCore(Path.Combine(scratch, "repository"), args[0]);
        Check(reopened.Execute(WireJson.Element(new { op = "health", args = new { } })).GetProperty("result").GetProperty("itemCount").GetInt64() == 1, "native repository restart persistence");
    }
    else Console.WriteLine("SKIP native integration: pass absolute storage_core library path");
}
finally { Directory.Delete(scratch, true); }
