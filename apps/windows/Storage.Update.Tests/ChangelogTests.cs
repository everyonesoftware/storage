using System.Text;
using System.Text.Json;
using Storage.Browser;
using Velopack.Sources;

static class ChangelogTests
{
    internal const string Installed = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string Offered = "3333333333333333333333333333333333333333";
    public static async Task Run()
    {
        var transport = new NotesDownloader();
        var result = await new WindowsChangelogSource(transport).LoadAsync(Installed, Offered, CancellationToken.None);
        FeedTests.Require(result.InstalledSource == Installed && result.OfferedSource == Offered, "Exact assembly-to-manifest source range, not version tags");
        FeedTests.Require(result.Commits.Count == 3 && result.Improvements.SequenceEqual(new[] { "Add tray" }) && result.Fixes.Count == 0, "Retain originals but omit docs and Android-only changes from Windows groups");
        FeedTests.Require(transport.Requests.All(u => !u.Contains("v0.1") && !u.Contains("releases")), "Never use release body or version-as-tag");
        Console.WriteLine("PASS exact source-range changelog, original subjects and platform filtering");
        await PaginationAndFailures();
        await OnDemandAndStaleOffers();
    }

    private static async Task OnDemandAndStaleOffers()
    {
        var source = new DelayedSource();
        using var notes = new WindowsChangelogController(source, "0.1.10+" + Installed);
        var offer = new WindowsUpdateOffer(new Version(0, 1, 20), Offered, "package-one");
        notes.Select(offer);
        FeedTests.Require(source.Calls == 0 && notes.Status.Phase == WindowsNotesPhase.Idle, "Offer does not prefetch collapsed notes");
        var first = notes.LoadAsync();
        FeedTests.Require(notes.Status.Phase == WindowsNotesPhase.Loading, "Explicit loading state");
        var backend = new Backend();
        using var updates = new WindowsUpdateController(backend, () => Task.CompletedTask);
        await updates.CheckAsync(); await updates.DownloadAsync();
        FeedTests.Require(updates.Status.Phase == WindowsUpdatePhase.Ready && notes.Status.Phase == WindowsNotesPhase.Loading && backend.Applies == 0, "Valid update downloads while changelog is still loading, without automatic install");
        await notes.LoadAsync();
        FeedTests.Require(source.Calls == 1, "No duplicate in-flight notes request");
        var secondOffer = offer with { PackageHash = "replacement-package" };
        notes.Select(secondOffer);
        source.Pending[0].SetResult(new(Installed, Offered, ["Stale"], [], []));
        await first;
        FeedTests.Require(notes.Status.Phase == WindowsNotesPhase.Idle && notes.Status.Notes is null, "Late notes cannot describe replaced same-version offer");
        var second = notes.LoadAsync();
        source.Pending[1].SetException(new IOException("private error")); await second;
        FeedTests.Require(notes.Status.Phase == WindowsNotesPhase.Unavailable && notes.Status.Notes is null, "Notes failure is not zero changes");
        var retry = notes.LoadAsync();
        source.Pending[2].SetResult(new(Installed, Offered, [], ["Real fix"], [])); await retry;
        FeedTests.Require(notes.Status.Phase == WindowsNotesPhase.Ready && notes.Status.Notes!.Fixes.Count == 1, "Explicit retry succeeds independently");
        await notes.LoadAsync();
        FeedTests.Require(source.Calls == 3, "Collapse/reopen reuses exact-offer notes");
        notes.Select(null);
        FeedTests.Require(notes.Status.Phase == WindowsNotesPhase.Idle && notes.Status.Notes is null, "Current/checking/error clears notes");
        using var unknown = new WindowsChangelogController(source, "0.1.10");
        unknown.Select(offer); await unknown.LoadAsync();
        FeedTests.Require(unknown.Status.Phase == WindowsNotesPhase.Unavailable && source.Calls == 3, "Missing installed assembly source honestly unavailable, never a version tag");
        notes.Select(offer); var closing = notes.LoadAsync(); notes.Dispose();
        source.Pending[3].SetResult(new(Installed, Offered, ["Late"], [], [])); await closing;
        FeedTests.Require(notes.Status.Notes is null, "Close suppresses late notes");
        Console.WriteLine("PASS on-demand loading, replacement-offer stale results, failures/retry, missing installed source, collapse/reopen and close");
    }

    private sealed class DelayedSource : IWindowsChangelogSource
    {
        public int Calls;
        public readonly List<TaskCompletionSource<WindowsChangelog>> Pending = [];
        public Task<WindowsChangelog> LoadAsync(string installedSource, string offeredSource, CancellationToken token)
        {
            Calls++; var pending = new TaskCompletionSource<WindowsChangelog>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add(pending); return pending.Task;
        }
    }

    private static async Task PaginationAndFailures()
    {
        var transport = new NotesDownloader();
        var commits = Enumerable.Range(1, 301).Select(i => new { sha = i == 301 ? Offered : i.ToString("x40"), commit = new { message = i == 1 ? "[verified] fix(core): preserve bytes" : "docs: refresh" } }).ToArray();
        for (int page = 1; page <= 4; page++) transport.Bodies[NotesDownloader.Compare(page)] = JsonSerializer.SerializeToUtf8Bytes(new { status = "ahead", total_commits = 301, base_commit = new { sha = Installed }, merge_base_commit = new { sha = Installed }, commits = commits.Skip((page - 1) * 100).Take(100) });
        foreach (var commit in commits) transport.Bodies[NotesDownloader.Commit(commit.sha, 1)] = JsonSerializer.SerializeToUtf8Bytes(new { sha = commit.sha, files = new[] { new { filename = commit == commits[0] ? "core/src/storage.rs" : "docs/releasing.md" } } });
        var source = new WindowsChangelogSource(transport);
        var result = await source.LoadAsync(Installed, Offered, CancellationToken.None);
        FeedTests.Require(result.Commits.Count == 301 && result.Fixes.SequenceEqual(new[] { "Preserve bytes" }) && result.Improvements.Count == 0 && transport.Requests.Contains(NotesDownloader.Compare(4)), "Skipped-release multi-page comparison retains shared fixes and omits empty improvements");
        // A 100-file first page must not hide a relevant shared file on page two.
        transport.Bodies[NotesDownloader.Commit(commits[1].sha, 1)] = JsonSerializer.SerializeToUtf8Bytes(new { sha = commits[1].sha, files = Enumerable.Range(1, 100).Select(i => new { filename = $"docs/{i}.md" }) });
        transport.Bodies[NotesDownloader.Commit(commits[1].sha, 2)] = JsonSerializer.SerializeToUtf8Bytes(new { sha = commits[1].sha, files = new[] { new { filename = "core/src/shared.rs" } } });
        result = await source.LoadAsync(Installed, Offered, CancellationToken.None);
        FeedTests.Require(result.Improvements.SequenceEqual(new[] { "Refresh" }), "File pagination retains unknown/shared relevance even with docs subject");
        foreach (string fault in new[] { "short", "duplicate", "diverged", "identical", "base", "total", "empty", "network", "oversize", "bad-json", "wrong-end" })
        {
            var bad = new NotesDownloader();
            string json = Encoding.UTF8.GetString(bad.Bodies[NotesDownloader.Compare(1)]);
            if (fault == "short") json = json.Replace("\"total_commits\":3", "\"total_commits\":4");
            if (fault == "duplicate") json = json.Replace("2222222222222222222222222222222222222222", "1111111111111111111111111111111111111111");
            if (fault == "identical") json = json.Replace("ahead", "identical");
            if (fault == "diverged") json = json.Replace("ahead", "diverged");
            if (fault == "base") json = json.Replace(Installed, new string('c', 40));
            if (fault == "total") json = json.Replace("\"total_commits\":3", "\"total_commits\":1001");
            if (fault == "wrong-end") json = json.Replace(Offered, new string('d', 40));
            if (fault == "empty") json = JsonSerializer.Serialize(new { status = "ahead", total_commits = 0, base_commit = new { sha = Installed }, merge_base_commit = new { sha = Installed }, commits = Array.Empty<object>() });
            bad.Bodies[NotesDownloader.Compare(1)] = fault == "oversize" ? new byte[4 * 1024 * 1024 + 1] : Encoding.UTF8.GetBytes(fault == "bad-json" ? "{" : json);
            if (fault == "network") bad.Bodies.Clear();
            bool rejected = false;
            try { await new WindowsChangelogSource(bad).LoadAsync(Installed, Offered, CancellationToken.None); }
            catch (Exception e) when (e is InvalidDataException or JsonException or KeyNotFoundException) { rejected = true; }
            FeedTests.Require(rejected, "Reject incomplete/unavailable history: " + fault);
        }
        foreach (string identity in new[] { "v0.1.10", "0.1.10", Installed + "." })
        {
            bool rejected = false;
            try { await source.LoadAsync(identity, Offered, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
            FeedTests.Require(rejected, "Never repair source identities");
        }
        FeedTests.Require(WindowsChangelogSource.InstalledSource("0.1.10+" + Installed) == Installed && WindowsChangelogSource.InstalledSource("0.1.10+" + Installed + "." + Installed) is null, "Strict assembly source metadata");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        int requests = transport.Requests.Count;
        try { await source.LoadAsync(Installed, Offered, cancelled.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        FeedTests.Require(transport.Requests.Count == requests, "Cancelled notes do not start another request");
        Console.WriteLine("PASS paginated compare and commit files, shared fixes, strict identities, cancellation, incomplete/divergent/oversize/network failure");
    }
    internal sealed class NotesDownloader : IFileDownloader
    {
        public readonly List<string> Requests = [];
        public readonly Dictionary<string, byte[]> Bodies = [];
        public NotesDownloader()
        {
            string[] shas = ["1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222", "3333333333333333333333333333333333333333"];
            Bodies[Compare(1)] = JsonSerializer.SerializeToUtf8Bytes(new { status = "ahead", total_commits = 3, base_commit = new { sha = Installed }, merge_base_commit = new { sha = Installed }, commits = shas.Select((sha, i) => new { sha, commit = new { message = new[] { "[verified] feat(windows): Add tray", "docs: refresh downloads", "fix(android): Fix install" }[i] } }) });
            string[][] files = [["apps/windows/Storage.Service/Tray.cs"], ["README.md", ".github/workflows/ci.yml"], ["apps/android/browser/Updates.kt", "tests/android-updates.test.mjs", "scripts/package-android.mjs"]];
            for (int i = 0; i < shas.Length; i++) Bodies[Commit(shas[i], 1)] = JsonSerializer.SerializeToUtf8Bytes(new { sha = shas[i], files = files[i].Select(filename => new { filename, status = "modified" }) });
        }
        internal static string Compare(int page) => $"https://api.github.com/repos/everyonesoftware/storage/compare/{Installed}...{Offered}?per_page=100&page={page}";
        internal static string Commit(string sha, int page) => $"https://api.github.com/repos/everyonesoftware/storage/commits/{sha}?per_page=100&page={page}";
        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        { Requests.Add(url); FeedTests.Require(timeout <= .5 && headers?.ContainsKey("Authorization") != true, "Bounded public notes transport"); return Task.FromResult(Bodies[url]); }
        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) => throw new NotSupportedException();
        public Task DownloadFile(string url, string targetFile, Action<int>? progress = null, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) => throw new NotSupportedException();
    }
}
