using Storage.Browser;
using Velopack;
using Velopack.Locators;

static class CachedPackageTests
{
    public static async Task Run()
    {
        foreach (byte[] corrupt in new[] { new byte[] { 1, 2, 3 }, WrongHash() })
        {
            using var fixture = new Fixture();
            File.WriteAllBytes(fixture.PackagePath, corrupt);
            await fixture.Controller.CheckAsync();
            await fixture.Controller.DownloadAsync();
            FeedTests.Require(fixture.Controller.Status.Phase == WindowsUpdatePhase.Error,
                "Preexisting corrupt/wrong-hash cached nupkg must not reach Ready");
            bool rejected = false;
            try { fixture.Backend.ApplyAndRestart(new Version(0, 1, 20)); }
            catch (InvalidOperationException) { rejected = true; }
            FeedTests.Require(rejected && fixture.Locator.Starts == 0 && fixture.Locator.Exits == 0,
                "Corrupt cached package must not reach native apply or exit");
            FeedTests.Require(!fixture.Downloader.Requests.Any(u => u.EndsWith(".nupkg")),
                "Exercise real pinned SDK cached-package fast path without source download");
            await fixture.Controller.CheckAsync();
            await fixture.Controller.DownloadAsync();
            FeedTests.Require(fixture.Controller.Status.Phase == WindowsUpdatePhase.Ready &&
                fixture.Downloader.Requests.Any(u => u.EndsWith(".nupkg")),
                "Invalid cached bytes must not trap user-initiated retries behind the SDK cache fast path");
        }
        Console.WriteLine("PASS real backend rejects truncated and same-length wrong-hash cached nupkg before Ready/apply");
        await ValidPackage(cached: true);
        await ValidPackage(cached: false);
        await RetamperedPackage();
        await RetamperedController();
    }

    private static async Task RetamperedController()
    {
        foreach (byte[]? bytes in new byte[]?[] { WrongHash(), [1, 2, 3], null })
        {
            using var fixture = new Fixture();
            File.WriteAllBytes(fixture.PackagePath, FixtureDownloader.Package);
            await fixture.Controller.CheckAsync();
            await fixture.Controller.DownloadAsync();
            if (bytes is null) File.Delete(fixture.PackagePath);
            else File.WriteAllBytes(fixture.PackagePath, bytes);
            await fixture.Controller.RestartAsync();
            FeedTests.Require(fixture.Controller.Status.Phase == WindowsUpdatePhase.Error && fixture.Locator.Starts == 0 && fixture.Locator.Exits == 0,
                "Changed or missing cache on restart must clear Ready and require a new check/download");
        }
        Console.WriteLine("PASS restart integrity failure clears Ready without native apply/exit");
    }

    private static byte[] WrongHash()
    {
        var bytes = FixtureDownloader.Package.ToArray();
        bytes[0] ^= 1;
        return bytes;
    }

    private static async Task ValidPackage(bool cached)
    {
        using var fixture = new Fixture();
        if (cached) File.WriteAllBytes(fixture.PackagePath, FixtureDownloader.Package);
        await fixture.Controller.CheckAsync();
        await fixture.Controller.DownloadAsync();
        FeedTests.Require(fixture.Controller.Status.Phase == WindowsUpdatePhase.Ready && fixture.Locator.Starts == 0,
            "Verified package reaches Ready without autoapply");
        FeedTests.Require(fixture.Downloader.Requests.Any(u => u.EndsWith(".nupkg")) != cached,
            "Cached bytes skip transport; missing cache uses real SDK download path");
        await fixture.Controller.RestartAsync();
        FeedTests.Require(fixture.Locator.Starts == 1 && fixture.Locator.Exits == 1 && fixture.Locator.Arguments.Contains(fixture.PackagePath),
            "Explicit restart applies the exact verified SDK package path");
        Console.WriteLine($"PASS valid {(cached ? "cached" : "downloaded")} nupkg reaches Ready then explicit native apply/exit seam");
    }

    private static async Task RetamperedPackage()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.PackagePath, FixtureDownloader.Package);
        await fixture.Controller.CheckAsync();
        await fixture.Controller.DownloadAsync();
        FeedTests.Require(fixture.Controller.Status.Phase == WindowsUpdatePhase.Ready,
            "Verified cached package reaches Ready");
        var tampered = FixtureDownloader.Package.ToArray();
        tampered[0] ^= 1;
        File.WriteAllBytes(fixture.PackagePath, tampered);
        bool rejected = false;
        try { fixture.Backend.ApplyAndRestart(new Version(0, 1, 20)); }
        catch (InvalidDataException) { rejected = true; }
        FeedTests.Require(rejected && fixture.Locator.Starts == 0 && fixture.Locator.Exits == 0,
            "Same-length re-tamper after verified download must fail before ApplyUpdatesAndRestart");
        Console.WriteLine("PASS re-tampered cached nupkg rejected before native apply/exit");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "storage-update-cache-" + Guid.NewGuid().ToString("N"));
        public FixtureDownloader Downloader { get; } = new();
        public RecordingLocator Locator { get; }
        public WindowsUpdateBackend Backend { get; }
        public WindowsUpdateController Controller { get; }
        public string PackagePath { get; }
        public Fixture()
        {
            Directory.CreateDirectory(root);
            Locator = new RecordingLocator(root);
            PackagePath = Path.Combine(Locator.PackagesDir!, "StorageWindows-0.1.20-win-x64-full.nupkg");
            Backend = new WindowsUpdateBackend(new WindowsGithubSource(Downloader), Locator);
            Controller = new WindowsUpdateController(Backend, () => Task.CompletedTask);
        }
        public void Dispose() { Controller.Dispose(); Directory.Delete(root, true); }
    }

    private sealed class RecordingLocator : TestVelopackLocator, IProcessImpl
    {
        public int Starts, Exits;
        public string[] Arguments = [];
        public override IProcessImpl Process => this;
        public RecordingLocator(string root) : base("StorageWindows", "0.1.10", Path.Combine(root, "sdk-package-cache"),
            root, root, Path.Combine(root, "Update.exe"), "win-x64")
        {
            Directory.CreateDirectory(PackagesDir!);
            File.WriteAllText(UpdateExePath!, "test-only updater sentinel; never executed");
        }
        void IProcessImpl.StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow)
        { Starts++; Arguments = args.ToArray(); }
        void IProcessImpl.Exit(int exitCode) { Exits++; }
        uint IProcessImpl.GetCurrentProcessId() => 123;
    }
}
