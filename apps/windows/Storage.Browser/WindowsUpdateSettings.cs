using System.Reflection;
using EveryoneSoftware.WindowsCommon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Storage.Browser;

/// <summary>Bind declared names after runtime XAML load, never parent traversal before Loaded.</summary>
internal sealed class WindowsUpdateSettings : IDisposable
{
    private readonly WindowsUpdatesPanel panel;
    private readonly WindowsUpdateController controller;
    private readonly WindowsUpdateBackend backend;
    private readonly WindowsChangelogController notes;
    private readonly DispatcherQueue dispatcher;
    private readonly Button retryNotes;
    private readonly TextBlock changeCount, notesStatus, sourceRange;
    private readonly Expander whatsNew, commitDetails;
    private readonly StackPanel notesGroups, commitSubjects;

    private WindowsUpdateOffer? displayedOffer;
    private WindowsChangelog? renderedNotes;
    private bool disposed;

    public WindowsUpdateSettings(FrameworkElement root, DispatcherQueue dispatcher, Func<Task> prepareRestart)
    {
        this.dispatcher = dispatcher;
        retryNotes = (Button)root.FindName("RetryChangelog");
        whatsNew = (Expander)root.FindName("WhatsNew");
        commitDetails = (Expander)root.FindName("CommitDetails");
        changeCount = (TextBlock)root.FindName("ChangeCount");
        notesStatus = (TextBlock)root.FindName("NotesStatus");
        sourceRange = (TextBlock)root.FindName("SourceRange");
        notesGroups = (StackPanel)root.FindName("NotesGroups");
        commitSubjects = (StackPanel)root.FindName("CommitSubjects");
        var stagingHost = (ContentControl)root.FindName("ChangelogStagingHost");
        stagingHost.Content = null;
        var assembly = typeof(WindowsUpdateSettings).Assembly;
        string build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        ((TextBlock)root.FindName("ApplicationBuild")).Text = build;
        panel = new WindowsUpdatesPanel("Storage", build.Split('+')[0]);
        ((ContentControl)root.FindName("UpdatesHost")).Content = panel.View;
        backend = new WindowsUpdateBackend();
        controller = new WindowsUpdateController(backend, prepareRestart);
        notes = new WindowsChangelogController(new WindowsChangelogSource(), build);
        panel.CheckRequested += CheckClicked;
        panel.DownloadRequested += DownloadClicked;
        panel.RestartRequested += RestartClicked;
        whatsNew.Expanding += NotesExpanded;
        retryNotes.Click += RetryNotesClicked;
        controller.Changed += OnChanged;
        notes.Changed += OnNotesChanged;
        Render(controller.Status);
    }

    private async void CheckClicked(object? sender, EventArgs e)
    {
        // Invalidate synchronously before an old notes task can publish during a new check.
        notes.Select(null);
        whatsNew.IsExpanded = false;
        await controller.CheckAsync();
    }
    private async void DownloadClicked(object? sender, EventArgs e) => await controller.DownloadAsync();
    private async void RestartClicked(object? sender, EventArgs e) => await controller.RestartAsync();
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
        var phase = state.Phase switch
        {
            WindowsUpdatePhase.Idle => UpdatePhase.Idle,
            WindowsUpdatePhase.Checking => UpdatePhase.Checking,
            WindowsUpdatePhase.Current => UpdatePhase.Current,
            WindowsUpdatePhase.Available => UpdatePhase.Available,
            // 0.1.6 has no distinct verification phase. Keep Check and Restart disabled,
            // and keep the backend's verification message visible until it reports Ready.
            WindowsUpdatePhase.Downloading or WindowsUpdatePhase.Verifying => UpdatePhase.Downloading,
            WindowsUpdatePhase.Ready => UpdatePhase.Ready,
            WindowsUpdatePhase.Restarting => UpdatePhase.Restarting,
            WindowsUpdatePhase.Error => UpdatePhase.Error,
            WindowsUpdatePhase.NotInstalled => UpdatePhase.NotInstalled,
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        RenderNotes();
        panel.Render(new(phase, state.Message, hasOffer ? state.Version : null, state.Progress),
            restartAllowed: state.Phase == WindowsUpdatePhase.Ready, changelog: hasOffer ? whatsNew : null);
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
        panel.CheckRequested -= CheckClicked;
        panel.DownloadRequested -= DownloadClicked;
        panel.RestartRequested -= RestartClicked;
        whatsNew.Expanding -= NotesExpanded;
        retryNotes.Click -= RetryNotesClicked;
        controller.Changed -= OnChanged;
        notes.Changed -= OnNotesChanged;
        notes.Dispose();
        controller.Dispose();
        panel.Dispose();
    }
}
