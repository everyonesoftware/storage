using System.Net.Http;
using Storage.Browser;

static class TransportTests
{
    public static async Task Run()
    {
        var probe = new Probe();
        int calls = 0;
        try { await probe.Request(url => { calls++; throw new IOException("fixture failure"); }); }
        catch (IOException) { }
        FeedTests.Require(calls == 1, "Never retry a repaired/lowercased release URL");
        using var handler = probe.Handler();
        FeedTests.Require(!handler.UseCookies && !handler.UseDefaultCredentials && handler.Credentials is null, "Public updater has no cookie or OS credentials");
        Console.WriteLine("PASS updater transport has no credentials/cookies or URL rewriting fallback");
    }
    private sealed class Probe : PublicUpdateDownloader
    {
        public Task<string> Request(Func<string, Task<string>> action) => TryDownloadThenLowercase(action, "https://github.com/everyonesoftware/storage/releases/download/v0.1.20/StorageWindows.nupkg");
        public HttpClientHandler Handler() => CreateHttpClientHandler();
    }
}
