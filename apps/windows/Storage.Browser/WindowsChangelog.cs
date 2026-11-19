using System.Text.Json;
using System.Text.RegularExpressions;
using Velopack.Sources;

namespace Storage.Browser;

public sealed record WindowsUpdateOffer(Version Version, string SourceCommit, string PackageHash);
public sealed record WindowsChangeCommit(string Hash, string Subject);
public sealed record WindowsChangelog(string InstalledSource, string OfferedSource, IReadOnlyList<string> Improvements, IReadOnlyList<string> Fixes, IReadOnlyList<WindowsChangeCommit> Commits);
public interface IWindowsChangelogSource
{
    Task<WindowsChangelog> LoadAsync(string installedSource, string offeredSource, CancellationToken token);
}

/// <summary>Exact published-source comparison; release descriptions and version tags are not history.</summary>
public sealed class WindowsChangelogSource(IFileDownloader? downloader = null) : IWindowsChangelogSource
{
    private readonly IFileDownloader downloader = downloader ?? new PublicChangelogDownloader();
    private const string Api = "https://api.github.com/repos/everyonesoftware/storage";
    private static readonly Dictionary<string, string> Headers = new() { ["Accept"] = "application/vnd.github+json", ["User-Agent"] = "StorageWindows-Changelog" };
    internal static bool IsSource(string? source) => source is not null && Regex.IsMatch(source, @"\A[0-9a-f]{40}\z");
    public static string? InstalledSource(string informationalVersion)
    {
        string[] parts = informationalVersion.Split('+');
        return parts.Length == 2 && IsSource(parts[1]) ? parts[1] : null;
    }

    private async Task<JsonDocument> ReadAsync(string url, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        byte[] bytes = await downloader.DownloadBytes(url, Headers, timeout: .5).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Changelog response exceeds the safe limit.");
        return JsonDocument.Parse(bytes);
    }

    public async Task<WindowsChangelog> LoadAsync(string installedSource, string offeredSource, CancellationToken token)
    {
        if (!IsSource(installedSource) || !IsSource(offeredSource)) throw new InvalidDataException("Published source identity is unavailable.");
        var commits = new List<WindowsChangeCommit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? total = null;
        for (int page = 1; page <= 10; page++)
        {
            using var document = await ReadAsync($"{Api}/compare/{installedSource}...{offeredSource}?per_page=100&page={page}", token);
            var root = document.RootElement;
            int count = root.GetProperty("total_commits").GetInt32();
            string? status = root.GetProperty("status").GetString();
            if (count is < 1 or > 1000 || total is not null && total != count || status != "ahead" || root.GetProperty("base_commit").GetProperty("sha").GetString() != installedSource || root.GetProperty("merge_base_commit").GetProperty("sha").GetString() != installedSource)
                throw new InvalidDataException("Incomplete or divergent published source range.");
            total = count;
            var entries = root.GetProperty("commits").EnumerateArray().ToArray();
            if (entries.Length > 100) throw new InvalidDataException("Unexpected compare page size.");
            foreach (var entry in entries)
            {
                string hash = entry.GetProperty("sha").GetString() ?? "";
                string subject = (entry.GetProperty("commit").GetProperty("message").GetString() ?? "").Split('\n')[0].TrimEnd('\r');
                if (!IsSource(hash) || !seen.Add(hash) || string.IsNullOrWhiteSpace(subject)) throw new InvalidDataException("Invalid commit history.");
                commits.Add(new(hash, subject));
            }
            if (commits.Count == total) break;
            if (commits.Count > total || entries.Length < 100 || page == 10) throw new InvalidDataException("Incomplete commit history.");
        }
        if (commits.Count != total || commits.Count > 0 && commits[^1].Hash != offeredSource)
            throw new InvalidDataException("Offered source is not the end of the comparison.");
        var improvements = new List<string>();
        var fixes = new List<string>();
        foreach (var commit in commits)
        {
            var files = new List<string>();
            var seenFiles = new HashSet<string>(StringComparer.Ordinal);
            for (int page = 1; page <= 10; page++)
            {
                using var document = await ReadAsync($"{Api}/commits/{commit.Hash}?per_page=100&page={page}", token);
                var root = document.RootElement;
                if (root.GetProperty("sha").GetString() != commit.Hash) throw new InvalidDataException("Changed commit identity.");
                var entries = root.GetProperty("files").EnumerateArray().ToArray();
                if (entries.Length > 100) throw new InvalidDataException("Unexpected file page size.");
                foreach (var entry in entries)
                {
                    string file = entry.GetProperty("filename").GetString() ?? "";
                    if (file.Length == 0 || !seenFiles.Add(file)) throw new InvalidDataException("Invalid commit files.");
                    files.Add(file);
                    if (entry.TryGetProperty("previous_filename", out var previous)) files.Add(previous.GetString() ?? "");
                }
                if (entries.Length < 100) break;
                if (page == 10) throw new InvalidDataException("Commit file history exceeds the safe limit.");
            }
            // Fail open for unknown/shared paths, never discard a shared change just because
            // its subject mentions another platform. Original commits remain in details.
            if (files.Count > 0 && files.All(IsExcludedPath)) continue;
            string subject = Regex.Replace(commit.Subject, @"\A\[verified\]\s*", "", RegexOptions.IgnoreCase);
            bool fix = Regex.IsMatch(subject, @"\Afix(?:\([^)]*\))?!?:", RegexOptions.IgnoreCase);
            subject = Regex.Replace(subject, @"\A[a-z]+(?:\([^)]*\))?!?:\s*", "", RegexOptions.IgnoreCase);
            if (subject.Length == 0) continue;
            subject = char.ToUpperInvariant(subject[0]) + subject[1..];
            (fix ? fixes : improvements).Add(subject);
        }
        return new(installedSource, offeredSource, improvements, fixes, commits);
    }

    private static bool IsExcludedPath(string path) => path.StartsWith("apps/android/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("apps/web/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(".github/", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(path, @"\A(?:scripts|tests)/[^/]*android[^/]*\z", RegexOptions.IgnoreCase) && !path.Contains("windows", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Bound the actual response stream, not only the JSON after allocation.</summary>
internal sealed class PublicChangelogDownloader(HttpClient? client = null) : IFileDownloader
{
    private const int Limit = 4 * 1024 * 1024;
    private static readonly HttpClient Shared = new(CreateHandler());
    private readonly HttpClient client = client ?? Shared;
    internal static HttpClientHandler CreateHandler() => new()
    {
        UseCookies = false, UseDefaultCredentials = false, Credentials = null, AllowAutoRedirect = false
    };

    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "api.github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !uri.AbsolutePath.StartsWith("/repos/everyonesoftware/storage/", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected changelog origin.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("User-Agent", "StorageWindows-Changelog");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > Limit) throw new InvalidDataException("Changelog response exceeds the safe limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, deadline.Token)) != 0)
        {
            if (output.Length + read > Limit) throw new InvalidDataException("Changelog response exceeds the safe limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) => throw new NotSupportedException();
    public Task DownloadFile(string url, string targetFile, Action<int>? progress = null, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) => throw new NotSupportedException();
}

public enum WindowsNotesPhase { Idle, Loading, Ready, Unavailable }
public sealed record WindowsNotesStatus(WindowsNotesPhase Phase, WindowsChangelog? Notes = null);

/// <summary>Independent from the download gate. An epoch binds every result to one exact offer.</summary>
public sealed class WindowsChangelogController(IWindowsChangelogSource source, string informationalVersion) : IDisposable
{
    private readonly string? installedSource = WindowsChangelogSource.InstalledSource(informationalVersion);
    private WindowsUpdateOffer? offer;
    private CancellationTokenSource? request;
    private long generation;
    private bool disposed;
    public WindowsNotesStatus Status { get; private set; } = new(WindowsNotesPhase.Idle);
    public event Action? Changed;

    public void Select(WindowsUpdateOffer? value)
    {
        if (disposed || offer == value) return;
        generation++;
        request?.Cancel();
        offer = value;
        Set(new(WindowsNotesPhase.Idle));
    }

    private void Set(WindowsNotesStatus state)
    {
        if (disposed) return;
        Status = state;
        Changed?.Invoke();
    }

    public async Task LoadAsync()
    {
        if (disposed || offer is null || Status.Phase is WindowsNotesPhase.Loading or WindowsNotesPhase.Ready) return;
        long epoch = generation;
        var selected = offer;
        if (installedSource is null || !WindowsChangelogSource.IsSource(selected.SourceCommit))
        {
            Set(new(WindowsNotesPhase.Unavailable));
            return;
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        request = cancellation;
        Set(new(WindowsNotesPhase.Loading));
        try
        {
            var notes = await source.LoadAsync(installedSource, selected.SourceCommit, cancellation.Token);
            if (!disposed && generation == epoch)
            {
                if (notes.InstalledSource != installedSource || notes.OfferedSource != selected.SourceCommit) throw new InvalidDataException("Changed notes range.");
                Set(new(WindowsNotesPhase.Ready, notes));
            }
        }
        catch
        {
            if (!disposed && generation == epoch) Set(new(WindowsNotesPhase.Unavailable));
        }
        finally { if (ReferenceEquals(request, cancellation)) request = null; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        generation++;
        request?.Cancel();
    }
}
