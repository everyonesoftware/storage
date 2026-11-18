namespace Storage.Browser;

public interface IWindowsUpdateBackend
{
    bool IsInstalled { get; }
    Version CurrentVersion { get; }
    Task<Version?> CheckAsync();
    Task DownloadAsync(Version version, Action<int> progress, CancellationToken token);
    void ApplyAndRestart(Version version);
}

public enum WindowsUpdatePhase { Idle, Checking, Current, Available, Downloading, Ready, Restarting, Error, NotInstalled }
public sealed record WindowsUpdateStatus(WindowsUpdatePhase Phase, string Message, Version? Version = null, int Progress = 0);

/// <summary>Only explicit user actions check, download or apply. No app credentials or repository access.</summary>
public sealed class WindowsUpdateController(IWindowsUpdateBackend backend, Func<Task> prepareRestart) : IDisposable
{
    private readonly Func<Task> prepareRestart = prepareRestart;
    private readonly CancellationTokenSource lifetime = new();
    private int busy;
    private bool disposed;
    public WindowsUpdateStatus Status { get; private set; } = new(WindowsUpdatePhase.Idle, "Check for Windows updates when you choose.");
    public event Action<WindowsUpdateStatus>? Changed;

    private void Set(WindowsUpdateStatus status)
    {
        if (disposed) return;
        Status = status;
        Changed?.Invoke(status);
    }

    public async Task CheckAsync()
    {
        if (disposed || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        try
        {
            if (Status.Phase is WindowsUpdatePhase.Ready or WindowsUpdatePhase.Restarting) return;
            if (!backend.IsInstalled)
            {
                Set(new(WindowsUpdatePhase.NotInstalled, "Install Storage using the Windows installer to enable in-app updates. Loose development builds cannot update."));
                return;
            }
            Set(new(WindowsUpdatePhase.Checking, "Checking Windows releases…"));
            var version = await backend.CheckAsync();
            Set(version is not null && version > backend.CurrentVersion
                ? new(WindowsUpdatePhase.Available, $"Windows {version} is available. Download it to continue.", version)
                : new(WindowsUpdatePhase.Current, "No newer Windows release is available."));
        }
        catch { Set(new(WindowsUpdatePhase.Error, "Could not check for updates. Check your connection or try again later; GitHub may be unavailable or rate-limiting requests.")); }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    public async Task DownloadAsync()
    {
        if (disposed || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        try
        {
            if (Status.Phase != WindowsUpdatePhase.Available || Status.Version is not { } version) return;
            Set(new(WindowsUpdatePhase.Downloading, $"Downloading Windows {version}…", version));
            await backend.DownloadAsync(version, value =>
            {
                if (Status.Phase == WindowsUpdatePhase.Downloading)
                    Set(new(WindowsUpdatePhase.Downloading, $"Downloading Windows {version}…", version, Math.Clamp(value, 0, 100)));
            }, lifetime.Token);
            Set(new(WindowsUpdatePhase.Ready, $"Windows {version} is downloaded. Restart and install when you are ready.", version, 100));
        }
        catch { Set(new(WindowsUpdatePhase.Error, "Download failed or was cancelled. Nothing was installed. Check for updates to try again.")); }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    public async Task RestartAsync()
    {
        if (disposed || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        var version = Status.Version;
        try
        {
            if (Status.Phase != WindowsUpdatePhase.Ready || version is null) return;
            Set(new(WindowsUpdatePhase.Restarting, "Preparing to restart and install…", version, 100));
            await prepareRestart();
            if (!disposed) backend.ApplyAndRestart(version);
        }
        catch (InvalidDataException) { Set(new(WindowsUpdatePhase.Error, "The downloaded update failed integrity verification. Nothing was installed. Check for updates to download again.")); }
        catch { Set(new(WindowsUpdatePhase.Ready, "Could not prepare the application or start the installer. Try Restart and install again.", version, 100)); }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    public void Dispose()
    {
        disposed = true;
        lifetime.Cancel();
        // A download may still be observing the cancellation token.
    }
}
