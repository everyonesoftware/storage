using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Storage.Browser;

/// <summary>Bind declared names after runtime XAML load, never parent traversal before Loaded.</summary>
public sealed partial class WindowsUpdatesPanel : IDisposable
{
    private readonly WindowsUpdateController controller;
    private readonly DispatcherQueue dispatcher;
    private readonly Button check, download, restart;
    private readonly TextBlock status;
    private readonly ProgressBar progress;
    private bool disposed;

    public WindowsUpdatesPanel(FrameworkElement root, DispatcherQueue dispatcher, Func<Task> prepareRestart)
    {
        this.dispatcher = dispatcher;
        check = (Button)root.FindName("CheckUpdates");
        download = (Button)root.FindName("DownloadUpdate");
        restart = (Button)root.FindName("RestartUpdate");
        status = (TextBlock)root.FindName("UpdateStatus");
        progress = (ProgressBar)root.FindName("UpdateProgress");
        var assembly = typeof(WindowsUpdatesPanel).Assembly;
        ((TextBlock)root.FindName("ApplicationVersion")).Text = "Version " +
            (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown");
        controller = new WindowsUpdateController(new WindowsUpdateBackend(), prepareRestart);
        check.Click += CheckClicked;
        download.Click += DownloadClicked;
        restart.Click += RestartClicked;
        controller.Changed += OnChanged;
        Render(controller.Status);
    }

    private async void CheckClicked(object sender, RoutedEventArgs e) => await controller.CheckAsync();
    private async void DownloadClicked(object sender, RoutedEventArgs e) => await controller.DownloadAsync();
    private async void RestartClicked(object sender, RoutedEventArgs e) => await controller.RestartAsync();

    private void OnChanged(WindowsUpdateStatus _) => dispatcher.TryEnqueue(() =>
    {
        // Read live state: queued worker-thread progress must not rewind Ready.
        if (!disposed) Render(controller.Status);
    });

    private void Render(WindowsUpdateStatus state)
    {
        status.Text = state.Phase == WindowsUpdatePhase.Downloading ? $"{state.Message} {state.Progress}%" : state.Message;
        bool busy = state.Phase is WindowsUpdatePhase.Checking or WindowsUpdatePhase.Downloading or WindowsUpdatePhase.Restarting;
        check.IsEnabled = !busy && state.Phase is not (WindowsUpdatePhase.Ready or WindowsUpdatePhase.NotInstalled);
        download.Visibility = state.Phase == WindowsUpdatePhase.Available ? Visibility.Visible : Visibility.Collapsed;
        download.IsEnabled = !busy;
        restart.Visibility = state.Phase == WindowsUpdatePhase.Ready ? Visibility.Visible : Visibility.Collapsed;
        restart.IsEnabled = !busy;
        progress.Visibility = busy || state.Phase == WindowsUpdatePhase.Ready ? Visibility.Visible : Visibility.Collapsed;
        progress.IsIndeterminate = state.Phase is WindowsUpdatePhase.Checking or WindowsUpdatePhase.Restarting;
        progress.Value = state.Progress;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        check.Click -= CheckClicked;
        download.Click -= DownloadClicked;
        restart.Click -= RestartClicked;
        controller.Changed -= OnChanged;
        controller.Dispose();
    }
}
