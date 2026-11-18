using Storage.Browser;

static class BackendTests
{
    public static async Task Run()
    {
        var backend = new WindowsUpdateBackend();
        bool downloadRejected = false, installRejected = false;
        try { await backend.DownloadAsync(new Version(0, 1, 20), _ => { }, CancellationToken.None); }
        catch (InvalidOperationException) { downloadRejected = true; }
        try { backend.ApplyAndRestart(new Version(0, 1, 20)); }
        catch (InvalidOperationException) { installRejected = true; }
        FeedTests.Require(downloadRejected && installRejected, "Native backend rejects missing offer without starting installer");
        FeedTests.Require(WindowsUpdateBackend.Options.AllowVersionDowngrade == false && WindowsUpdateBackend.Options.ExplicitChannel == "win-x64" && WindowsUpdateBackend.Options.MaximumDeltasBeforeFallback == -1, "Pinned full-package channel without downgrades");
        Console.WriteLine("PASS real Velopack backend rejects unoffered install/download and disables downgrades");
    }
}
