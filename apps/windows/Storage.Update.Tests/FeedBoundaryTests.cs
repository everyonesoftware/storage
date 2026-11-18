using System.Text;
using System.Text.Json;
using Storage.Browser;
using Velopack;
using Velopack.Logging;

static class FeedBoundaryTests
{
    public static async Task Run()
    {
        var none = new FixtureDownloader();
        none.Pages = new() { [1] = [none.Release("0.1.99", false), none.Release("0.1.98", true, true)] };
        FeedTests.Require((await Read(none)).Assets.Length == 0, "Other platform and prereleases are not Windows updates");
        var wrongChannel = new FixtureDownloader();
        await Reject(() => new WindowsGithubSource(wrongChannel).GetReleaseFeed(NullVelopackLogger.Instance, "StorageWindows", "android"));
        FeedTests.Require(wrongChannel.Requests.Count == 0, "Reject wrong identity before network");
        var capped = new FixtureDownloader();
        capped.Pages = Enumerable.Range(1, 10).ToDictionary(page => page, page => Enumerable.Range(page * 100, 100).Select(n => capped.Release("0.1." + n, false)).ToArray());
        await Reject(() => Read(capped));
        FeedTests.Require(capped.Requests.Count == 10, "Full tenth page never reports up to date");
        foreach (string fault in new[] { "package", "version", "architecture", "empty", "duplicate", "malformed", "origin", "marker-case" })
        {
            var bad = new FixtureDownloader();
            const string prefix = WindowsGithubSource.Repository + "/releases/download/v0.1.20/";
            string feed = Encoding.UTF8.GetString(bad.Bodies[prefix + "releases.win-x64.json"]);
            feed = fault switch
            {
                "package" => feed.Replace("StorageWindows", "OtherApp"),
                "version" => feed.Replace("0.1.20", "0.1.21"),
                "architecture" => feed.Replace("win-x64", "win-arm64"),
                "empty" => "{\"Assets\":[]}",
                "malformed" => "not JSON",
                _ => feed
            };
            if (fault == "duplicate") { using var doc = JsonDocument.Parse(feed); string entry = doc.RootElement.GetProperty("Assets")[0].GetRawText(); feed = "{\"Assets\":[" + entry + "," + entry + "]}"; }
            bad.Bodies[prefix + "releases.win-x64.json"] = Encoding.UTF8.GetBytes(feed);
            using (var doc = JsonDocument.Parse(bad.Bodies[prefix + "storage-windows-release.json"]))
            {
                var properties = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone());
                properties["files"] = doc.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("name").GetString() == "releases.win-x64.json" ? (object)new { name = "releases.win-x64.json", bytes = Encoding.UTF8.GetByteCount(feed), sha256 = FixtureDownloader.Hash(Encoding.UTF8.GetBytes(feed)) } : f.Clone()).ToArray();
                bad.Bodies[prefix + "storage-windows-release.json"] = JsonSerializer.SerializeToUtf8Bytes(properties);
            }
            if (fault is "origin" or "marker-case")
            {
                string release = JsonSerializer.Serialize(bad.Release("0.1.20"));
                release = fault == "origin" ? release.Replace(prefix, "https://untrusted.example/") : release.Replace("storage-windows-release.json", "STORAGE-WINDOWS-RELEASE.JSON");
                bad.Pages = new() { [1] = [JsonSerializer.Deserialize<JsonElement>(release)] };
            }
            await Reject(() => Read(bad));
            FeedTests.Require(!bad.Requests.Any(u => u.Contains("/v0.1.19/")), "Invalid selected publication must not fall back");
        }
        Console.WriteLine("PASS other-platform/no-update, paging cap, identity, malformed feed and original-origin binding");
    }
    private static Task<VelopackAssetFeed> Read(FixtureDownloader fixture) => new WindowsGithubSource(fixture).GetReleaseFeed(NullVelopackLogger.Instance, "StorageWindows", "win-x64");
    private static async Task Reject(Func<Task<VelopackAssetFeed>> action)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidDataException or JsonException or KeyNotFoundException or ArgumentException) { return; }
        throw new Exception("Unsafe publication was accepted");
    }
}
