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
        using var notesHandler = PublicChangelogDownloader.CreateHandler();
        FeedTests.Require(!notesHandler.AllowAutoRedirect && !notesHandler.UseCookies && !notesHandler.UseDefaultCredentials && notesHandler.Credentials is null, "Changelog transport rejects redirects and credentials");
        using var http = new HttpClient(new NotesHandler());
        var notes = new PublicChangelogDownloader(http);
        foreach (string failure in new[] { "oversize-header", "oversize-stream", "rate-limit", "redirect" })
        {
            bool rejected = false;
            try { await notes.DownloadBytes("https://api.github.com/repos/everyonesoftware/storage/" + failure); }
            catch (Exception error) when (error is InvalidDataException or HttpRequestException) { rejected = true; }
            FeedTests.Require(rejected, "Bounded/fail-closed notes HTTP: " + failure);
        }
        var bytes = await notes.DownloadBytes("https://api.github.com/repos/everyonesoftware/storage/ok");
        FeedTests.Require(bytes.SequenceEqual(new byte[] { 1, 2, 3 }), "Notes reads actual HTTP stream");
        Console.WriteLine("PASS changelog HTTP bounds before allocation and during streaming, rate-limit/redirect failures and isolated credentials");
    }
    private sealed class Probe : PublicUpdateDownloader
    {
        public Task<string> Request(Func<string, Task<string>> action) => TryDownloadThenLowercase(action, "https://github.com/everyonesoftware/storage/releases/download/v0.1.20/StorageWindows.nupkg");
        public HttpClientHandler Handler() => CreateHttpClientHandler();
    }
    private sealed class NotesHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            var response = new HttpResponseMessage(path.EndsWith("rate-limit") ? System.Net.HttpStatusCode.Forbidden : path.EndsWith("redirect") ? System.Net.HttpStatusCode.Redirect : System.Net.HttpStatusCode.OK);
            response.Content = path.Contains("oversize") ? new StreamContent(new MemoryStream(new byte[4 * 1024 * 1024 + 1])) : new ByteArrayContent([1, 2, 3]);
            if (path.EndsWith("oversize-header")) response.Content.Headers.ContentLength = 4 * 1024 * 1024 + 1;
            if (path.EndsWith("oversize-stream"))
            {
                response.Content.Dispose();
                response.Content = new StreamContent(new UnknownLengthStream(new byte[4 * 1024 * 1024 + 1]));
                FeedTests.Require(response.Content.Headers.ContentLength is null, "Exercise stream bound without a content-length header");
            }
            FeedTests.Require(request.Headers.Authorization is null, "No application auth on notes request");
            return Task.FromResult(response);
        }
    }
    private sealed class UnknownLengthStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
