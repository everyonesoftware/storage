using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Storage.Browser;

/// <summary>Music's paginated platform source pattern, bound to Storage's publication manifest.
/// GitHub Latest can contain only Android. Never use application/service credentials.</summary>
public sealed class WindowsGithubSource(IFileDownloader? downloader = null) : IUpdateSource
{
    public const string Repository = "https://github.com/everyonesoftware/storage";
    public const string Channel = "win-x64";
    public const string PackageId = "StorageWindows";
    private readonly IFileDownloader downloader = downloader ?? new PublicUpdateDownloader();
    private readonly Dictionary<VelopackAsset, string> downloads = [];
    private static Dictionary<string, string> Headers => new() { ["Accept"] = "application/vnd.github+json", ["User-Agent"] = "StorageWindows-Updater" };

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (channel != Channel || appId is not null && appId != PackageId) throw new InvalidDataException("Unexpected Windows update identity.");
        downloads.Clear();
        var candidates = new List<(Version Version, JsonElement Release)>();
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int page = 1; page <= 10; page++)
        {
            string json = await downloader.DownloadString($"https://api.github.com/repos/everyonesoftware/storage/releases?per_page=100&page={page}", Headers, timeout: 0.5);
            using var document = Parse(Encoding.UTF8.GetBytes(json));
            var releases = document.RootElement.EnumerateArray().ToArray();
            foreach (var release in releases)
            {
                string inventoryTag = release.GetProperty("tag_name").GetString() ?? "";
                if (!tags.Add(inventoryTag)) throw new InvalidDataException("Duplicate release inventory.");
                if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
                if (!release.GetProperty("assets").EnumerateArray().Any(a => string.Equals(a.GetProperty("name").GetString(), "storage-windows-release.json", StringComparison.OrdinalIgnoreCase))) continue;
                string tag = release.GetProperty("tag_name").GetString() ?? "";
                if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) || version.Major != 0 || version.Minor != 1 || version.Build is < 1 or > 65534 || version.Revision != -1 || tag != "v" + version) throw new InvalidDataException("Malformed Windows publication version.");
                candidates.Add((version, release.Clone()));
            }
            if (releases.Length < 100)
            {
                if (candidates.Count == 0) return new() { Assets = [] };
                var selected = candidates.OrderByDescending(c => c.Version).First();
                string version = selected.Version.ToString();
                string prefix = $"{Repository}/releases/download/v{version}/";
                var assets = selected.Release.GetProperty("assets").EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("browser_download_url").GetString()!, StringComparer.OrdinalIgnoreCase);
                foreach (var (name, url) in assets)
                    if (!Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z") || url != prefix + name) throw new InvalidDataException("Unsafe Windows release asset or origin.");
                if (!assets.Keys.Contains("storage-windows-release.json", StringComparer.Ordinal)) throw new InvalidDataException("Invalid Windows publication marker.");
                byte[] manifestBytes = await downloader.DownloadBytes(assets["storage-windows-release.json"], Headers, timeout: 0.5);
                using var manifest = Parse(manifestBytes);
                var root = manifest.RootElement;
                if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("platform").GetString() != "windows" || root.GetProperty("version").GetString() != version || root.GetProperty("tag").GetString() != "v" + version || root.GetProperty("count").GetInt32() != selected.Version.Build) throw new InvalidDataException("Mismatched Windows publication.");
                RequireProperties(root, "schemaVersion", "platform", "version", "count", "tag", "sourceCommit", "signing", "files");
                if (!Regex.IsMatch(root.GetProperty("sourceCommit").GetString() ?? "", @"\A[0-9a-f]{40}\z") || root.GetProperty("signing").GetString() != "unsigned") throw new InvalidDataException("Invalid Windows publication identity.");
                var files = root.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!, StringComparer.OrdinalIgnoreCase);
                string[] expected = ["StorageWindows-win-x64-Setup.exe", "StorageWindows-win-x64-Setup.exe.sha256", $"StorageWindows-{version}-{Channel}-full.nupkg", $"releases.{Channel}.json", $"assets.{Channel}.json", "RELEASES-win-x64", "Storage-windows-SHA256SUMS"];
                if (!files.Keys.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal))) throw new InvalidDataException("Incomplete Windows platform inventory.");
                foreach (var (name, file) in files)
                {
                    RequireProperties(file, "name", "bytes", "sha256");
                    if (!assets.ContainsKey(name) || file.GetProperty("bytes").GetInt64() <= 0 || !Regex.IsMatch(file.GetProperty("sha256").GetString() ?? "", @"\A[0-9a-f]{64}\z")) throw new InvalidDataException("Invalid Windows payload metadata.");
                }
                byte[] feedBytes = await downloader.DownloadBytes(assets[$"releases.{Channel}.json"], Headers, timeout: 0.5);
                Verify(feedBytes.LongLength, SHA256.HashData(feedBytes), files[$"releases.{Channel}.json"]);
                using var checkedFeed = Parse(feedBytes);
                var feed = VelopackAssetFeed.FromJson(Encoding.UTF8.GetString(feedBytes));
                if (feed.Assets.Length != 1) throw new InvalidDataException("Windows publication requires one full package.");
                var asset = feed.Assets[0];
                string package = $"{PackageId}-{version}-{Channel}-full.nupkg";
                if (asset.PackageId != PackageId || asset.Version.ToString() != version || asset.Type != VelopackAssetType.Full || asset.FileName != package || asset.Size != files[package].GetProperty("bytes").GetInt64() || !string.Equals(asset.SHA256, files[package].GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected Windows update package.");
                downloads.Add(asset, assets[package]);
                return feed;
            }
        }
        throw new InvalidDataException("GitHub release inventory exceeded the safe paging limit.");
    }

    private static JsonDocument Parse(byte[] bytes)
    {
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Windows update metadata exceeds the safe limit.");
        var document = JsonDocument.Parse(bytes);
        try { RejectDuplicates(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate update metadata field.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    private static void RequireProperties(JsonElement element, params string[] expected)
    {
        if (!element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal))) throw new InvalidDataException("Unexpected Windows publication fields.");
    }

    private static void Verify(long length, byte[] hash, JsonElement file)
    {
        if (length != file.GetProperty("bytes").GetInt64() || Convert.ToHexString(hash).ToLowerInvariant() != file.GetProperty("sha256").GetString()) throw new InvalidDataException("Windows release integrity check failed.");
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
    {
        if (!downloads.TryGetValue(releaseEntry, out var url)) throw new InvalidDataException("No matching Windows update offer.");
        await downloader.DownloadFile(url, localFile, progress, Headers, timeout: 10, cancelToken: cancelToken);
        cancelToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(localFile);
        if (stream.Length != releaseEntry.Size || !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancelToken)), releaseEntry.SHA256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Windows package integrity check failed.");
    }
}

/// <summary>Standalone public HTTP transport, not the Storage SDK or an authenticated app client.</summary>
internal class PublicUpdateDownloader : HttpClientFileDownloader
{
    protected override Task<T> TryDownloadThenLowercase<T>(Func<string, Task<T>> downloadFunc, string url) => downloadFunc(url);

    protected override System.Net.Http.HttpClientHandler CreateHttpClientHandler()
    {
        var handler = base.CreateHttpClientHandler();
        handler.UseCookies = false;
        handler.UseDefaultCredentials = false;
        handler.Credentials = null;
        return handler;
    }
}

/// <summary>Lazy locator discovery; downloading never applies. Cached bytes are verified before readiness and apply.</summary>
public sealed class WindowsUpdateBackend : IWindowsUpdateBackend
{
    internal static UpdateOptions Options => new()
    {
        ExplicitChannel = WindowsGithubSource.Channel,
        AllowVersionDowngrade = false,
        MaximumDeltasBeforeFallback = -1
    };
    private readonly Lazy<Velopack.Locators.IVelopackLocator> locator;
    private readonly Lazy<UpdateManager> manager;

    public WindowsUpdateBackend() : this(new WindowsGithubSource(), null) { }

    internal WindowsUpdateBackend(IUpdateSource source, Velopack.Locators.IVelopackLocator? locator)
    {
        this.locator = new(() => locator ?? Velopack.Locators.VelopackLocator.Current);
        manager = new(() => new UpdateManager(source, Options, this.locator.Value));
    }
    private UpdateInfo? offered;
    private bool downloaded;
    public bool IsInstalled => manager.Value.IsInstalled;
    public Version CurrentVersion => Version.Parse(manager.Value.CurrentVersion?.ToString() ?? throw new InvalidOperationException("Storage is not installed."));

    public async Task<Version?> CheckAsync()
    {
        offered = null;
        downloaded = false;
        if (!IsInstalled) throw new InvalidOperationException("Install Storage to use in-app updates.");
        var update = await manager.Value.CheckForUpdatesAsync();
        if (update is null) return null;
        var version = Version.Parse(update.TargetFullRelease.Version.ToString());
        if (version <= CurrentVersion || update.IsDowngrade || update.TargetFullRelease.PackageId != WindowsGithubSource.PackageId)
            throw new InvalidDataException("Unsafe Windows update target.");
        offered = update;
        return version;
    }

    private UpdateInfo RequireOffer(Version version)
    {
        if (offered is null || offered.TargetFullRelease.Version.ToString() != version.ToString() || version <= CurrentVersion)
            throw new InvalidOperationException("No matching newer Windows update.");
        return offered;
    }

    private void VerifyCachedPackage(VelopackAsset release)
    {
        // Use the same SDK locator as UpdateManager, never an assumed installation/cache path.
        string packages = locator.Value.PackagesDir ?? throw new InvalidDataException("Missing Windows package cache.");
        if (release.FileName != $"{WindowsGithubSource.PackageId}-{release.Version}-{WindowsGithubSource.Channel}-full.nupkg")
            throw new InvalidDataException("Unexpected Windows package filename.");
        // Velopack 1.2.161's internal GetLocalPackagePath combines PackagesDir with the safe filename.
        // The exact bound filename above needs no SDK sanitization or guessed installation path.
        string path = Path.Combine(packages, release.FileName);
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length != release.Size || !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), release.SHA256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Windows package integrity check failed.");
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // The SDK trusts an existing final filename. Discard only this offered package so a
            // subsequent explicit check/download can recover instead of retrying corrupt bytes forever.
            try { File.Delete(path); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw new InvalidDataException("Windows package integrity check failed.", error);
        }
    }

    public async Task DownloadAsync(Version version, Action<int> progress, CancellationToken token)
    {
        downloaded = false;
        var update = RequireOffer(version);
        await manager.Value.DownloadUpdatesAsync(update, progress, token);
        token.ThrowIfCancellationRequested();
        VerifyCachedPackage(update.TargetFullRelease);
        token.ThrowIfCancellationRequested();
        downloaded = true;
    }

    public void ApplyAndRestart(Version version)
    {
        var update = RequireOffer(version);
        if (!downloaded) throw new InvalidOperationException("Download the Windows update first.");
        try { VerifyCachedPackage(update.TargetFullRelease); }
        catch { downloaded = false; throw; }
        manager.Value.ApplyUpdatesAndRestart(update.TargetFullRelease, Array.Empty<string>());
    }
}
