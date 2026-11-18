using Storage.Browser;

static class ControllerTests
{
    public static async Task Run()
    {
        foreach (var offered in new Version?[] { null, new(0, 1, 9), new(0, 1, 10) })
        {
            var backend = new ControlledBackend { Offered = offered };
            using var controller = new WindowsUpdateController(backend, () => Task.CompletedTask);
            await controller.DownloadAsync(); await controller.RestartAsync();
            await controller.CheckAsync(); await controller.DownloadAsync(); await controller.RestartAsync();
            Require(controller.Status.Phase == WindowsUpdatePhase.Current && backend.Downloads == 0 && backend.Applies == 0, "No update/equal/older cannot install");
        }
        var loose = new ControlledBackend { Installed = false };
        using (var controller = new WindowsUpdateController(loose, () => Task.CompletedTask))
        {
            await controller.CheckAsync();
            Require(controller.Status.Phase == WindowsUpdatePhase.NotInstalled && loose.Checks == 0, "Development build does not contact feed");
        }
        foreach (string failure in new[] { "check", "download", "prepare", "apply" })
        {
            var backend = new ControlledBackend { Failure = failure };
            bool prepared = false;
            using var controller = new WindowsUpdateController(backend, () => { if (backend.Failure == "prepare") throw new IOException("secret repository path"); prepared = true; return Task.CompletedTask; });
            backend.BeforeApply = () => Require(prepared, "Prepare before native restart");
            await controller.CheckAsync();
            if (failure != "check") await controller.DownloadAsync();
            if (failure is "prepare" or "apply") await controller.RestartAsync();
            Require(controller.Status.Phase == (failure is "prepare" or "apply" ? WindowsUpdatePhase.Ready : WindowsUpdatePhase.Error), "Honest retryable error state");
            Require(!controller.Status.Message.Contains("secret") && backend.Applies == 0, "Errors do not expose paths or apply");
            backend.Failure = null;
            if (failure is "check" or "download") { await controller.CheckAsync(); await controller.DownloadAsync(); }
            await controller.RestartAsync();
            Require(backend.Applies == 1, "Failure can be retried explicitly");
        }
        var pending = new TaskCompletionSource<Version?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new ControlledBackend { Pending = pending.Task };
        using (var controller = new WindowsUpdateController(delayed, () => Task.CompletedTask))
        {
            var first = controller.CheckAsync();
            await controller.CheckAsync(); await controller.DownloadAsync(); await controller.RestartAsync();
            Require(delayed.Checks == 1 && delayed.Downloads == 0, "Concurrent actions refused");
            controller.Dispose();
            pending.SetResult(new(0, 1, 20)); await first;
            Require(controller.Status.Phase == WindowsUpdatePhase.Checking, "Close suppresses late completion");
        }
        Console.WriteLine("PASS no-update, downgrade, loose build, safe retry, preparation ordering, concurrent actions and close");
    }
    private static void Require(bool condition, string message) => FeedTests.Require(condition, message);
    private sealed class ControlledBackend : IWindowsUpdateBackend
    {
        public bool Installed = true;
        public bool IsInstalled => Installed;
        public Version CurrentVersion => new(0, 1, 10);
        public Version? Offered = new(0, 1, 20);
        public string? Failure;
        public Task<Version?>? Pending;
        public Action? BeforeApply;
        public int Checks, Downloads, Applies;
        public Task<Version?> CheckAsync() { Checks++; return Failure == "check" ? Task.FromException<Version?>(new IOException("secret url")) : Pending ?? Task.FromResult(Offered); }
        public Task DownloadAsync(Version version, Action<int> progress, CancellationToken token) { Downloads++; progress(51); return Failure == "download" ? Task.FromException(new IOException("secret file")) : Task.CompletedTask; }
        public void ApplyAndRestart(Version version) { BeforeApply?.Invoke(); if (Failure == "apply") throw new IOException("secret installer"); Applies++; }
    }
}
