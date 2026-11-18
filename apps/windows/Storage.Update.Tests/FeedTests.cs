using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Storage.Browser;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

static class FeedTests
{
    public static async Task Run()
    {
        var download = new FixtureDownloader();
        var source = new WindowsGithubSource(download);
        var feed = await source.GetReleaseFeed(NullVelopackLogger.Instance, "StorageWindows", "win-x64");
        Require(feed.Assets.Length == 1 && feed.Assets[0].Version.ToString() == "0.1.20", "Select highest numeric platform publication across pages");
        Require(download.Requests.Any(u => u.EndsWith("page=2")) && !download.Requests.Any(u => u.Contains("latest")), "Explicit pagination, never latest");
        string target = Path.Combine(Path.GetTempPath(), "storage-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await source.DownloadReleaseEntry(NullVelopackLogger.Instance, feed.Assets[0], target, _ => { });
            Require(File.ReadAllBytes(target).SequenceEqual(FixtureDownloader.Package), "Original bound package downloaded");
        }
        finally { File.Delete(target); }
        Console.WriteLine("PASS manifest-backed paginated Windows selection and original download");
        var legacy = new FixtureDownloader();
        legacy.Pages[2] = [legacy.Release("0.1.20", legacyInstaller: true)];
        var legacyFeed = await new WindowsGithubSource(legacy).GetReleaseFeed(NullVelopackLogger.Instance, "StorageWindows", "win-x64");
        Require(legacyFeed.Assets.Length == 1, "Retain legacy unversioned installer publications");
        foreach (string fault in new[] { "source", "signing", "missing-installer", "extra-file", "duplicate-property", "duplicate-tag", "unsafe-name", "feed-hash", "package-hash" })
        {
            var bad = new FixtureDownloader();
            const string prefix = WindowsGithubSource.Repository + "/releases/download/v0.1.20/";
            string manifest = Encoding.UTF8.GetString(bad.Bodies[prefix + "storage-windows-release.json"]);
            if (fault == "source") manifest = manifest.Replace(new string('a', 40), "not-a-source");
            if (fault == "signing") manifest = manifest.Replace("unsigned", "trusted");
            if (fault == "missing-installer") bad.Pages[2] = [bad.Release("0.1.20")];
            if (fault == "missing-installer" || fault == "extra-file")
            {
                using var doc = JsonDocument.Parse(manifest);
                var fields = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone());
                var files = doc.RootElement.GetProperty("files").EnumerateArray().Select(f => (object)f.Clone()).ToList();
                if (fault == "missing-installer") files.RemoveAt(0);
                else files.Add(new { name = "unexpected.exe", bytes = 1, sha256 = new string('a', 64) });
                fields["files"] = files;
                manifest = JsonSerializer.Serialize(fields);
            }
            if (fault == "duplicate-property") manifest = manifest.Replace("\"schemaVersion\":1", "\"schemaVersion\":9,\"schemaVersion\":1");
            if (fault == "duplicate-tag") bad.Pages[2] = [bad.Release("0.1.20"), bad.Release("0.1.20")];
            if (fault == "unsafe-name") bad.Pages[2] = [new { tag_name = "v0.1.20", draft = false, prerelease = false, assets = new[] { new { name = "storage-windows-release.json", browser_download_url = prefix + "storage-windows-release.json" }, new { name = "../escape", browser_download_url = prefix + "../escape" } } }];
            bad.Bodies[prefix + "storage-windows-release.json"] = Encoding.UTF8.GetBytes(manifest);
            if (fault == "feed-hash") bad.Bodies[prefix + "releases.win-x64.json"] = Encoding.UTF8.GetBytes("{}");
            var unsafeSource = new WindowsGithubSource(bad);
            bool rejected = false;
            try
            {
                var unsafeFeed = await unsafeSource.GetReleaseFeed(NullVelopackLogger.Instance, "StorageWindows", "win-x64");
                if (fault == "package-hash")
                {
                    bad.Bodies[prefix + "StorageWindows-0.1.20-win-x64-full.nupkg"] = [1, 2, 3];
                    try { await unsafeSource.DownloadReleaseEntry(NullVelopackLogger.Instance, unsafeFeed.Assets[0], target, _ => { }); }
                    finally { File.Delete(target); }
                }
            }
            catch (Exception e) when (e is InvalidDataException or JsonException or ArgumentException or KeyNotFoundException) { rejected = true; }
            Require(rejected, "Reject invalid publication: " + fault);
        }
        Console.WriteLine("PASS malformed manifests, duplicate inventories and integrity failures rejected");
    }
    public static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}

sealed class FixtureDownloader : IFileDownloader
{
    public static readonly byte[] Package = Encoding.UTF8.GetBytes("test-only full package bytes, not a production nupkg");
    public readonly List<string> Requests = [];
    public readonly Dictionary<string, byte[]> Bodies = [];
    public Dictionary<int, object[]> Pages = [];
    public FixtureDownloader()
    {
        Pages[1] = Enumerable.Range(100, 99).Select(n => Release($"0.1.{n}", false)).Prepend(Release("0.1.9")).ToArray();
        Pages[2] = [Release("0.1.20"), Release("0.1.19"), Release("0.1.99", true, true)];
    }
    public object Release(string version, bool windows = true, bool prerelease = false, bool legacyInstaller = false)
    {
        string tag = "v" + version;
        string package = $"StorageWindows-{version}-win-x64-full.nupkg";
        string prefix = WindowsGithubSource.Repository + "/releases/download/" + tag + "/";
        string installer = legacyInstaller ? "StorageWindows-win-x64-Setup.exe" : $"StorageWindows-{version}-win-x64-Setup.exe";
        var names = windows ? new[] { installer, installer + ".sha256", package, "releases.win-x64.json", "assets.win-x64.json", "RELEASES-win-x64", "Storage-windows-SHA256SUMS" } : ["Storage-Browser-" + version + ".apk"];
        if (windows)
        {
            var feed = new { Assets = new[] { new { PackageId = "StorageWindows", Version = version, Type = "Full", FileName = package, Size = Package.Length, SHA256 = Hash(Package).ToUpperInvariant() } } };
            foreach (string name in names) Bodies[prefix + name] = name == "releases.win-x64.json" ? JsonSerializer.SerializeToUtf8Bytes(feed) : name == package ? Package : Encoding.UTF8.GetBytes("test-only " + name);
            var manifest = new { schemaVersion = 1, platform = "windows", version, count = int.Parse(version.Split('.')[2]), tag, sourceCommit = new string('a', 40), signing = "unsigned", files = names.Select(name => new { name, bytes = Bodies[prefix + name].Length, sha256 = Hash(Bodies[prefix + name]) }) };
            Bodies[prefix + "storage-windows-release.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest);
        }
        return new { tag_name = tag, draft = false, prerelease, assets = (windows ? names.Append("storage-windows-release.json") : names).Select(name => new { name, browser_download_url = prefix + name }).ToArray() };
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        Requests.Add(url);
        CheckHeaders(headers);
        FeedTests.Require(timeout <= 0.5, "Metadata requests have a 30-second deadline, not SDK default 30 minutes");
        return Task.FromResult(JsonSerializer.Serialize(Pages.GetValueOrDefault(int.Parse(url.Split("&page=")[1]), [])));
    }
    public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    { Requests.Add(url); CheckHeaders(headers); return Task.FromResult(Bodies[url]); }
    public Task DownloadFile(string url, string targetFile, Action<int>? progress = null, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
    { Requests.Add(url); CheckHeaders(headers); File.WriteAllBytes(targetFile, Bodies[url]); progress?.Invoke(100); return Task.CompletedTask; }
    private static void CheckHeaders(IDictionary<string, string>? headers) => FeedTests.Require(headers is null || !headers.Keys.Any(k => k.Equals("Authorization", StringComparison.OrdinalIgnoreCase)), "Public credential-independent transport");
}
