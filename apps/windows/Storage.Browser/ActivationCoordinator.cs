namespace Storage.Browser;

internal interface IActivationPlatform : IDisposable
{
    bool TryOwn();
    IDisposable Listen(Action activated);
    void SignalExisting();
}

// Receives only a wake-up, not paths, credentials, or application protocol data.
internal sealed class ActivationCoordinator : IDisposable
{
    private readonly object gate = new();
    private readonly IActivationPlatform platform;
    private readonly IDisposable? listener;
    private Action<Action>? dispatch;
    private Action? activate;
    private bool pending, disposed;
    public bool IsOwner { get; }

    public ActivationCoordinator(IActivationPlatform platform)
    {
        this.platform = platform;
        try
        {
            IsOwner = platform.TryOwn();
            if (IsOwner) listener = platform.Listen(Request);
            else platform.SignalExisting();
        }
        catch { platform.Dispose(); throw; }
    }

    public void Attach(Action<Action> dispatch, Action activate)
    {
        lock (gate)
        {
            if (disposed || !IsOwner) return;
            this.dispatch = dispatch;
            this.activate = activate;
            if (pending) Request();
        }
    }

    private void Request()
    {
        lock (gate)
        {
            if (disposed) return;
            pending = true;
            if (dispatch is null) return;
            pending = false;
            dispatch(() =>
            {
                lock (gate) { if (!disposed) activate?.Invoke(); }
            });
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            activate = null;
            dispatch = null;
        }
        listener?.Dispose();
        platform.Dispose();
    }
}
