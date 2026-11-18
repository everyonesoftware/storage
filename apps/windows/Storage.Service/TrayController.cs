namespace Storage.Service;

internal enum TrayStatus { Stopped, Starting, Running, Stopping, Error }
internal sealed class TrayController(Func<Action, CancellationToken, Task> runner)
{
    private readonly object stateGate = new();
    private TrayStatus status = TrayStatus.Stopped;
    private string? error;
    private string? disposalFailure;
    public TrayStatus Status { get { lock (stateGate) return status; } }
    public string? Error { get { lock (stateGate) return error; } }
    public event Action? Changed;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object cancellationGate = new();
    private int quitting;
    private CancellationTokenSource? cancellation;
    private Task? running;
    private void Report(TrayStatus value, string? failure = null)
    {
        lock (stateGate) { status = value; error = failure; }
        Changed?.Invoke();
    }
    public Task<bool> StartAsync() => ActAsync(async () =>
    {
        if (Status == TrayStatus.Running) return;
        await StopCoreAsync();
        if (disposalFailure is null) await StartCoreAsync();
    });
    private async Task<bool> ActAsync(Func<Task> action)
    {
        if (Volatile.Read(ref quitting) != 0 || !await gate.WaitAsync(0)) return false;
        try { if (Volatile.Read(ref quitting) != 0) return false; await action(); return true; }
        finally { gate.Release(); }
    }
    private async Task StartCoreAsync()
    {
        Report(TrayStatus.Starting);
        CancellationToken token;
        lock (cancellationGate)
        {
            if (quitting != 0) return;
            cancellation = new();
            token = cancellation.Token;
        }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        running = ObserveAsync(Task.Run(() => runner(() => ready.TrySetResult(), token)), token);
        await Task.WhenAny(ready.Task, running);
        if (running.IsCompleted)
        {
            await running;
        }
        else
        {
            lock (stateGate) { if (status == TrayStatus.Starting) status = TrayStatus.Running; }
            Changed?.Invoke();
        }
    }
    private async Task ObserveAsync(Task task, CancellationToken token)
    {
        string? failure = null;
        try { await task; if (!token.IsCancellationRequested) failure = "Service listener exited unexpectedly."; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e)
        {
            failure = e.Message;
            if (token.IsCancellationRequested) disposalFailure = failure;
        }
        if (failure is not null) Report(TrayStatus.Error, failure);
    }
    public Task<bool> StopAsync() => ActAsync(StopCoreAsync);
    public Task<bool> RestartAsync() => ActAsync(async () => { await StopCoreAsync(); if (disposalFailure is null) await StartCoreAsync(); });
    private async Task StopCoreAsync()
    {
        Report(TrayStatus.Stopping);
        lock (cancellationGate) cancellation?.Cancel();
        try { if (running is not null) await running; }
        catch (OperationCanceledException) when (cancellation!.IsCancellationRequested) { }
        catch (Exception e) { Report(TrayStatus.Error, e.Message); }
        lock (cancellationGate) { cancellation?.Dispose(); cancellation = null; }
        running = null;
        if (disposalFailure is not null) Report(TrayStatus.Error, disposalFailure);
        else if (Status != TrayStatus.Error) Report(TrayStatus.Stopped);
    }
    public async Task QuitAsync()
    {
        Interlocked.Exchange(ref quitting, 1);
        lock (cancellationGate) cancellation?.Cancel();
        await gate.WaitAsync();
        try { await StopCoreAsync(); } finally { gate.Release(); }
    }
    public bool EndSession(TimeSpan budget)
    {
        // WM_ENDSESSION must retain control while draining: Windows need not allow
        // another message-loop turn or Program's finally after its handler returns.
        // Run cancellation off the STA so even a slow cancellation callback is bounded.
        var shutdown = Task.Run(QuitAsync);
        _ = shutdown.ContinueWith(task => Console.Error.WriteLine(task.Exception),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return shutdown.Wait(budget) && Status == TrayStatus.Stopped; }
        catch (AggregateException) { return false; }
    }
}
