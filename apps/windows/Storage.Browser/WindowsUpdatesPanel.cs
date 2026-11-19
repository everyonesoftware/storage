using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Storage.Browser;

/// <summary>Bind declared names after runtime XAML load, never parent traversal before Loaded.</summary>
public sealed partial class WindowsUpdatesPanel : IDisposable
{
    private readonly WindowsUpdateController controller;
    private readonly WindowsUpdateBackend backend;
    private readonly WindowsChangelogController notes;
    private readonly DispatcherQueue dispatcher;
    private readonly Button check, download, restart, retryNotes;
    private readonly TextBlock status, title, offeredVersion, changeCount, notesStatus, sourceRange;
    private readonly Expander whatsNew, commitDetails;
    private readonly StackPanel notesGroups, commitSubjects;
    private readonly ProgressBar progress;
    private WindowsUpdateOffer? displayedOffer;
    private WindowsChangelog? renderedNotes;
    private bool disposed;

    public WindowsUpdatesPanel(FrameworkElement root, DispatcherQueue dispatcher, Func<Task> prepareRestart)
    {
        this.dispatcher = dispatcher;
        check = (Button)root.FindName("CheckUpdates");
        download = (Button)root.FindName("DownloadUpdate");
        restart = (Button)root.FindName("RestartUpdate");
        retryNotes = (Button)root.FindName("RetryChangelog");
        status = (TextBlock)root.FindName("UpdateStatus");
        title = (TextBlock)root.FindName("UpdateTitle");
        offeredVersion = (TextBlock)root.FindName("OfferedVersion");
        progress = (ProgressBar)root.FindName("UpdateProgress");
        whatsNew = (Expander)root.FindName("WhatsNew");
        commitDetails = (Expander)root.FindName("CommitDetails");
        changeCount = (TextBlock)root.FindName("ChangeCount");
        notesStatus = (TextBlock)root.FindName("NotesStatus");
        sourceRange = (TextBlock)root.FindName("SourceRange");
        notesGroups = (StackPanel)root.FindName("NotesGroups");
        commitSubjects = (StackPanel)root.FindName("CommitSubjects");
        var assembly = typeof(WindowsUpdatesPanel).Assembly;
        string build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        ((TextBlock)root.FindName("ApplicationVersion")).Text = "Current Version: " + build.Split('+')[0];
        ((TextBlock)root.FindName("ApplicationBuild")).Text = build;
        backend = new WindowsUpdateBackend();
        controller = new WindowsUpdateController(backend, prepareRestart);
        notes = new WindowsChangelogController(new WindowsChangelogSource(), build);
        check.Click += CheckClicked;
        download.Click += DownloadClicked;
        restart.Click += RestartClicked;
        whatsNew.Expanding += NotesExpanded;
        retryNotes.Click += RetryNotesClicked;
        controller.Changed += OnChanged;
        notes.Changed += OnNotesChanged;
        Render(controller.Status);
    }

    private async void CheckClicked(object sender, RoutedEventArgs e)
    {
        // Invalidate synchronously before an old notes task can publish during a new check.
        notes.Select(null);
        whatsNew.IsExpanded = false;
        await controller.CheckAsync();
    }
    private async void DownloadClicked(object sender, RoutedEventArgs e) => await controller.DownloadAsync();
    private async void RestartClicked(object sender, RoutedEventArgs e) => await controller.RestartAsync();
    private async void NotesExpanded(Expander sender, ExpanderExpandingEventArgs e) => await notes.LoadAsync();
    private async void RetryNotesClicked(object sender, RoutedEventArgs e) => await notes.LoadAsync();

    private void OnChanged(WindowsUpdateStatus _) => dispatcher.TryEnqueue(() =>
    {
        // Read live state: queued worker-thread progress must not rewind Ready.
        if (!disposed) Render(controller.Status);
    });
    private void OnNotesChanged() => dispatcher.TryEnqueue(() => { if (!disposed) RenderNotes(); });

    private void Render(WindowsUpdateStatus state)
    {
        bool hasOffer = state.Version is not null && state.Phase is WindowsUpdatePhase.Available or WindowsUpdatePhase.Downloading or WindowsUpdatePhase.Verifying or WindowsUpdatePhase.Ready or WindowsUpdatePhase.Restarting;
        var nextOffer = hasOffer ? backend.Offer : null;
        if (displayedOffer != nextOffer)
        {
            displayedOffer = nextOffer;
            whatsNew.IsExpanded = false;
            commitDetails.IsExpanded = false;
        }
        notes.Select(nextOffer);
        title.Text = hasOffer ? "Update available" : state.Phase switch
        {
            WindowsUpdatePhase.Checking => "Checking for updates…",
            WindowsUpdatePhase.Current => "Up to date",
            WindowsUpdatePhase.Error => "Update failed",
            WindowsUpdatePhase.NotInstalled => "Installed build required",
            _ => "Not checked yet"
        };
        offeredVersion.Text = hasOffer ? "Storage " + state.Version : "";
        offeredVersion.Visibility = hasOffer ? Visibility.Visible : Visibility.Collapsed;
        whatsNew.Visibility = hasOffer ? Visibility.Visible : Visibility.Collapsed;
        status.Text = state.Phase == WindowsUpdatePhase.Downloading ? $"{state.Message} {state.Progress}%" : state.Message;
        status.Visibility = string.IsNullOrEmpty(status.Text) ? Visibility.Collapsed : Visibility.Visible;
        bool busy = state.Phase is WindowsUpdatePhase.Checking or WindowsUpdatePhase.Downloading or WindowsUpdatePhase.Verifying or WindowsUpdatePhase.Restarting;
        check.Content = state.Phase == WindowsUpdatePhase.Checking ? "Checking…" : "Check for updates";
        check.IsEnabled = !busy && state.Phase is not (WindowsUpdatePhase.Ready or WindowsUpdatePhase.NotInstalled);
        download.Visibility = state.Phase == WindowsUpdatePhase.Available ? Visibility.Visible : Visibility.Collapsed;
        download.IsEnabled = !busy;
        restart.Visibility = state.Phase == WindowsUpdatePhase.Ready ? Visibility.Visible : Visibility.Collapsed;
        restart.IsEnabled = !busy;
        progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        progress.IsIndeterminate = state.Phase is WindowsUpdatePhase.Checking or WindowsUpdatePhase.Verifying or WindowsUpdatePhase.Restarting;
        progress.Value = state.Progress;
        RenderNotes();
    }

    private void RenderNotes()
    {
        var state = notes.Status;
        var result = state.Notes;
        changeCount.Text = state.Phase switch
        {
            WindowsNotesPhase.Ready => $"{result!.Improvements.Count + result.Fixes.Count} {(result.Improvements.Count + result.Fixes.Count == 1 ? "change" : "changes")}",
            WindowsNotesPhase.Loading => "Loading…",
            WindowsNotesPhase.Unavailable => "Unavailable",
            _ => "On demand"
        };
        notesStatus.Text = state.Phase switch
        {
            WindowsNotesPhase.Loading => "Loading what’s new…",
            WindowsNotesPhase.Unavailable => "Couldn’t load what’s new. Published source history may be unavailable. You can still download this update.",
            WindowsNotesPhase.Ready when result!.Improvements.Count + result.Fixes.Count == 0 => "No Windows-facing changes in this source range.",
            _ => ""
        };
        notesStatus.Visibility = string.IsNullOrEmpty(notesStatus.Text) ? Visibility.Collapsed : Visibility.Visible;
        retryNotes.Visibility = state.Phase == WindowsNotesPhase.Unavailable ? Visibility.Visible : Visibility.Collapsed;
        commitDetails.Visibility = result is not null ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(renderedNotes, result)) return;
        renderedNotes = result;
        notesGroups.Children.Clear();
        commitSubjects.Children.Clear();
        sourceRange.Text = "";
        commitDetails.IsExpanded = false;
        if (result is null) return;
        AddGroup("Improvements", result.Improvements);
        AddGroup("Fixes", result.Fixes);
        sourceRange.Text = $"Source range: {result.InstalledSource}..{result.OfferedSource}";
        foreach (var commit in result.Commits)
            commitSubjects.Children.Add(new TextBlock { Text = $"{commit.Hash} · {commit.Subject}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 });
    }

    private void AddGroup(string heading, IReadOnlyList<string> entries)
    {
        if (entries.Count == 0) return;
        var group = new StackPanel { Spacing = 8 };
        group.Children.Add(new TextBlock { Text = heading, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 124, 210, 221)) });
        foreach (string text in entries)
        {
            var row = new Grid { ColumnSpacing = 9 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = "•" });
            var bullet = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(bullet, 1);
            row.Children.Add(bullet);
            group.Children.Add(row);
        }
        notesGroups.Children.Add(group);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        check.Click -= CheckClicked;
        download.Click -= DownloadClicked;
        restart.Click -= RestartClicked;
        whatsNew.Expanding -= NotesExpanded;
        retryNotes.Click -= RetryNotesClicked;
        controller.Changed -= OnChanged;
        notes.Changed -= OnNotesChanged;
        notes.Dispose();
        controller.Dispose();
    }
}
