using Storage.Browser;

await Storage.Update.Tests.UpdateTestSuite.Run();
if (args.Contains("--live", StringComparer.Ordinal))
{
    var source = new WindowsGithubSource();
    var feed = await source.GetReleaseFeed(Velopack.Logging.NullVelopackLogger.Instance, WindowsGithubSource.PackageId, WindowsGithubSource.Channel);
    foreach (var asset in feed.Assets) Console.WriteLine($"LIVE READ-ONLY {asset.PackageId} {asset.Version} {asset.FileName} {asset.Size} bytes");
}

namespace Storage.Update.Tests
{
    public static class UpdateTestSuite
    {
        public static async Task Run()
        {
            var backend = new Backend();
            using var controller = new WindowsUpdateController(backend, () => Task.CompletedTask);
            Equal(0, backend.Checks);
            await controller.CheckAsync();
            Equal(WindowsUpdatePhase.Available, controller.Status.Phase);
            Equal(new Version(0, 1, 20), controller.Status.Version);
            Equal(1, backend.Checks);
            Console.WriteLine("PASS user initiated check offers a newer Windows version");
            var statuses = new List<WindowsUpdateStatus>();
            controller.Changed += statuses.Add;
            await controller.DownloadAsync();
            Equal(WindowsUpdatePhase.Ready, controller.Status.Phase);
            Equal(1, backend.Downloads);
            Equal(0, backend.Applies);
            Equal(true, statuses.Any(s => s.Progress == 51 && s.Phase == WindowsUpdatePhase.Downloading));
            await controller.CheckAsync();
            Equal(1, backend.Checks);
            await controller.RestartAsync();
            Equal(1, backend.Applies);
            Console.WriteLine("PASS progress waits for explicit restart and staged update survives checking");

            await FeedTests.Run();
            await FeedBoundaryTests.Run();
            await BackendTests.Run();
            await CachedPackageTests.Run();
            await ControllerTests.Run();
            await TransportTests.Run();

        }

        static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}");
        }

    }
}

sealed class Backend : IWindowsUpdateBackend
{
    public bool IsInstalled => true;
    public Version CurrentVersion => new(0, 1, 10);
    public int Checks, Downloads, Applies;
    public Task<Version?> CheckAsync() { Checks++; return Task.FromResult<Version?>(new(0, 1, 20)); }
    public Task DownloadAsync(Version version, Action<int> progress, CancellationToken token) { Downloads++; progress(51); return Task.CompletedTask; }
    public void ApplyAndRestart(Version version) { Applies++; }
}
