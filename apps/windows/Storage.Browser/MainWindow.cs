using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Storage.Client;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
namespace Storage.Browser;
public sealed partial class MainWindow : Window
{
    private readonly Grid root;
    private readonly StorageClient client;
    private readonly WindowsUpdatesPanel updates;
    private Item[] items = [];
    private Collection[] collections = [];
    private Item? selected;
    private string page = "items", layout = "list";
    private Collection? filter;
    private bool busy, initialized, launchedService;
    private readonly CancellationTokenSource lifetime = new();
    private readonly string servicePath = Path.Combine(AppContext.BaseDirectory, "Service", "Storage.Service.exe");
    private T C<T>(string name) where T : class => (T)(root.FindName(name) ?? throw new InvalidOperationException("Missing control: " + name));
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(ColorHelper.FromArgb(255, r, g, b));
    public MainWindow()
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Storage.Browser.Shell.xaml")!;
        using var reader = new StreamReader(source);
        root = (Grid)XamlReader.Load(reader.ReadToEnd());
        Content = root; Title = "Storage";
        ExtendsContentIntoTitleBar = true; SetTitleBar(C<Border>("TitleBar"));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Storage.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 900));
        var title = AppWindow.TitleBar;
        title.ButtonBackgroundColor = ColorHelper.FromArgb(255, 23, 33, 41);
        title.ButtonInactiveBackgroundColor = ColorHelper.FromArgb(255, 23, 33, 41);
        title.ButtonForegroundColor = ColorHelper.FromArgb(255, 234, 243, 248);
        title.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 145, 166, 181);
        client = new StorageClient(ConnectAsync);
        C<Button>("ItemsNav").Click += async (_, _) => await Run(async () => { filter = null; SetPage("items"); await RefreshAsync(); });
        C<Button>("CollectionsNav").Click += async (_, _) => await Run(async () => { SetPage("collections"); await LoadCollectionsAsync(); });
        C<Button>("SettingsNav").Click += async (_, _) => await Run(async () => { SetPage("settings"); await HealthAsync(); });
        C<Button>("RefreshButton").Click += async (_, _) => await Run(RefreshAsync);
        C<Button>("ImportButton").Click += async (_, _) => await Run(PickImportAsync);
        C<Button>("LayoutButton").Click += (_, _) => { layout = layout == "list" ? "grid" : "list"; C<Button>("LayoutButton").Content = layout == "list" ? "Grid view" : "List view"; Render(); };
        C<TextBox>("Search").TextChanged += (_, _) => Render();
        C<Button>("ExportButton").Click += async (_, _) => { if (selected is { } item) await Run(() => ExportAsync(item, item.CurrentVersionId)); };
        C<Button>("NewVersionButton").Click += async (_, _) => await Run(NewVersionAsync);
        C<Button>("RenameButton").Click += async (_, _) => await Run(async () =>
        {
            if (selected is not { } item) return;
            selected = await client.RenameAsync(item.Id, item.Revision, C<TextBox>("RenameText").Text.Trim(), lifetime.Token);
            await RefreshAsync();
        });
        C<Button>("CreateCollectionButton").Click += async (_, _) => await Run(async () =>
        {
            await client.CreateCollectionAsync(C<TextBox>("CollectionName").Text.Trim(), lifetime.Token);
            C<TextBox>("CollectionName").Text = ""; await LoadCollectionsAsync();
        });
        C<Button>("AddCollectionButton").Click += async (_, _) => await Run(async () =>
        {
            if (selected is not { } item || C<ComboBox>("CollectionChoice").SelectedItem is not ComboBoxItem { Tag: Collection collection }) return;
            await client.AddToCollectionAsync(collection.Id, item.Id, collection.Revision, lifetime.Token);
            await LoadCollectionsAsync(); C<TextBlock>("Status").Text = "Item added to collection. Content was not copied.";
        });
        C<ListView>("CollectionsList").ItemClick += async (_, e) =>
        {
            if (e.ClickedItem is FrameworkElement { Tag: Collection collection }) await Run(async () => { filter = collection; SetPage("items"); await RefreshAsync(); });
        };
        updates = new WindowsUpdatesPanel(root, DispatcherQueue, PrepareUpdateRestartAsync);
        C<Grid>("BrowseHost").DragOver += (_, e) => { if (!busy && e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.Copy; e.DragUIOverride.Caption = "Import into Storage"; } };
        C<Grid>("BrowseHost").Drop += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                if (busy || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
                await Run(async () =>
                {
                    var files = await e.DataView.GetStorageItemsAsync();
                    foreach (var entry in files) if (entry is StorageFile file) await ImportFileAsync(file);
                    await RefreshAsync();
                });
            }
            finally { deferral.Complete(); }
        };
        root.Loaded += async (_, _) => { if (!initialized) { initialized = true; await Run(RefreshAsync); } };
        Closed += (_, _) => { updates.Dispose(); lifetime.Cancel(); lifetime.Dispose(); };
        SetPage("items");
    }
    private Task PrepareUpdateRestartAsync()
    {
        // No unsaved in-memory repository state: all mutations complete in the service.
        // Refuse restart while any import/export/editor operation is active; never clear protected state.
        if (busy) throw new InvalidOperationException("Wait for the local service operation to finish before restarting.");
        return Task.CompletedTask;
    }

    private async Task<Stream> ConnectAsync(CancellationToken token)
    {
        try { return await WindowsPeers.ConnectAsync(servicePath, token); }
        catch (TimeoutException) when (!launchedService && File.Exists(servicePath))
        {
            launchedService = true;
            var start = new ProcessStartInfo(servicePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(servicePath)! };
            // This requests a service-owned default-No dialog, never a silent grant.
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "Update.exe"))) start.ArgumentList.Add("--approve-bundled-browser");
            using var service = Process.Start(start);
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                if (service is not null && service.HasExited) throw new StorageException("PermissionDenied", "Browser approval required. Start the installed service with --approve-bundled-browser and approve its own dialog, then Refresh.");
                try { return await WindowsPeers.ConnectAsync(servicePath, token); }
                catch (TimeoutException) { await Task.Delay(250, token); }
            }
            throw new StorageException("PermissionDenied", "Browser approval required in the Storage Service dialog. Approve, then Refresh.");
        }
    }
    private async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; SetBusy(true); C<InfoBar>("Notice").IsOpen = false;
        C<TextBlock>("Status").Text = "Working with the local service…";
        try { await action(); }
        catch (OperationCanceledException) { C<TextBlock>("Status").Text = "Operation cancelled or timed out. Refresh to verify current state."; }
        catch (Exception error)
        {
            var notice = C<InfoBar>("Notice");
            notice.Message = error is StorageException storage ? $"{storage.Code}: {storage.Message}" : error is TimeoutException or IOException ? "Local service unavailable or transfer interrupted. Start the independent service, check Browser enrollment, then Refresh. See apps/windows/README.md." : error.Message;
            notice.IsOpen = true; C<TextBlock>("Status").Text = "Operation did not complete. No success was reported.";
        }
        finally { busy = false; SetBusy(false); }
    }
    private void SetBusy(bool value)
    {
        C<ProgressBar>("Busy").Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        foreach (string name in new[] { "ImportButton", "RefreshButton", "ItemsNav", "CollectionsNav", "SettingsNav", "LayoutButton", "ExportButton", "NewVersionButton", "RenameButton", "CreateCollectionButton", "AddCollectionButton" }) C<Button>(name).IsEnabled = !value;
        C<ContentControl>("ItemsHost").IsEnabled = !value;
        C<ListView>("CollectionsList").IsEnabled = !value;
        foreach (var version in C<StackPanel>("Versions").Children.OfType<Button>()) version.IsEnabled = !value;
    }
    private void SetPage(string target)
    {
        page = target;
        C<TextBlock>("Heading").Text = target == "items" ? filter?.Name ?? "All items" : target == "collections" ? "Collections" : "Settings";
        C<TextBlock>("Subtitle").Text = target switch { "items" => "Original content. Immutable history. Yours to keep.", "collections" => "Bring related items together.", _ => "Your local service and application." };
        C<Grid>("BrowseHost").Visibility = target == "items" ? Visibility.Visible : Visibility.Collapsed;
        C<StackPanel>("CollectionsPanel").Visibility = target == "collections" ? Visibility.Visible : Visibility.Collapsed;
        C<ScrollViewer>("SettingsPanel").Visibility = target == "settings" ? Visibility.Visible : Visibility.Collapsed;
        C<Grid>("Toolbar").Visibility = target == "items" ? Visibility.Visible : Visibility.Collapsed;
        C<Button>("ImportButton").Visibility = target == "items" ? Visibility.Visible : Visibility.Collapsed;
        C<TextBlock>("CollectionFilter").Visibility = filter is not null && target == "items" ? Visibility.Visible : Visibility.Collapsed;
        C<TextBlock>("CollectionFilter").Text = "Collection view · choose All items to clear";
        foreach (var (name, value) in new[] { ("ItemsNav", "items"), ("CollectionsNav", "collections"), ("SettingsNav", "settings") })
        {
            C<Button>(name).Background = value == page ? Brush(37, 65, 79) : Brush(23, 33, 41);
            AutomationProperties.SetHelpText(C<Button>(name), value == page ? "Current page" : "");
        }
    }
    private async Task RefreshAsync()
    {
        var result = filter is null ? await client.ListItemsAsync(lifetime.Token) : await client.ListCollectionItemsAsync(filter.Id, lifetime.Token);
        items = result.Items; Render(); await LoadCollectionsAsync(); await HealthAsync();
        C<TextBlock>("Status").Text = $"{items.Length} items{(result.HasMore ? " · partial listing; more items exist" : "")} · Local service connected";
        if (selected is { } prior)
        {
            var current = items.FirstOrDefault(i => i.Id == prior.Id);
            if (current is not null) await InspectAsync(current);
            else { selected = null; C<StackPanel>("Inspector").Visibility = Visibility.Collapsed; C<StackPanel>("InspectorEmpty").Visibility = Visibility.Visible; }
        }
    }
    private async Task HealthAsync()
    {
        var health = await client.HealthAsync(lifetime.Token);
        C<TextBlock>("ServiceHealth").Text = $"Status: {health.Status}\nDevice: {health.DeviceId}\nData store: {health.DataStoreId}\n{health.ItemCount} items · {health.VersionCount} immutable versions\nLocal owner: local-owner";
    }
    private async Task LoadCollectionsAsync()
    {
        var collectionPage = await client.ListCollectionsPageAsync(lifetime.Token);
        collections = collectionPage.Collections;
        var list = C<ListView>("CollectionsList"); list.Items.Clear();
        var choice = C<ComboBox>("CollectionChoice"); choice.Items.Clear();
        foreach (var collection in collections.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            list.Items.Add(new TextBlock { Text = collection.Name + "  ›", Tag = collection, Margin = new Thickness(4, 12, 4, 12), FontSize = 16 });
            choice.Items.Add(new ComboBoxItem { Content = collection.Name, Tag = collection });
        }
        if (collectionPage.HasMore) list.Items.Add(new TextBlock { Text = "Partial listing: more collections exist. Continuation browsing is not implemented in this slice.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 12, 4, 12) });
        if (collections.Length == 0) list.Items.Add(new TextBlock { Text = "No collections yet. Create one above.", Foreground = Brush(145, 166, 181), Margin = new Thickness(4, 12, 4, 12) });
    }
    private void Render()
    {
        string query = C<TextBox>("Search").Text.Trim();
        var visible = items.Where(i => (i.Name + " " + i.ContentType).Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        ListViewBase view = layout == "grid" ? new GridView() : new ListView();
        view.SelectionMode = ListViewSelectionMode.None; view.IsItemClickEnabled = true;
        foreach (var item in visible)
        {
            var copy = new StackPanel { Spacing = 7, Tag = item, Padding = new Thickness(12), Width = layout == "grid" ? 188 : double.NaN };
            if (layout == "grid") copy.Children.Add(new FontIcon { Glyph = "\uE8A5", FontSize = 32, Foreground = Brush(85, 196, 210), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 20) });
            copy.Children.Add(new TextBlock { Text = item.Name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 });
            copy.Children.Add(new TextBlock { Text = $"{item.Attributes.ByteLength:N0} bytes  ·  revision {item.Revision}", FontSize = 12, Foreground = Brush(145, 166, 181) });
            copy.Children.Add(new TextBlock { Text = item.ContentType, FontSize = 11, Foreground = Brush(145, 166, 181), TextTrimming = TextTrimming.CharacterEllipsis });
            AutomationProperties.SetName(copy, $"{item.Name}, {item.ContentType}, {item.Attributes.ByteLength} bytes");
            view.Items.Add(copy);
        }
        view.ItemClick += async (_, e) => { if (e.ClickedItem is FrameworkElement { Tag: Item item }) await Run(() => InspectAsync(item)); };
        C<ContentControl>("ItemsHost").Content = view;
        C<StackPanel>("EmptyState").Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        C<TextBlock>("EmptyTitle").Text = query.Length == 0 ? "Your space, ready for its first item" : "No matching items";
        C<TextBlock>("EmptyText").Text = query.Length == 0 ? "Import files or drop them here. Original versions stay intact." : "Try another name or content type.";
    }
    private async Task InspectAsync(Item item)
    {
        var current = await client.GetItemAsync(item.Id, lifetime.Token);
        var versionPage = await client.ListVersionsPageAsync(item.Id, lifetime.Token);
        var versions = versionPage.Versions;
        selected = current;
        C<StackPanel>("InspectorEmpty").Visibility = Visibility.Collapsed; C<StackPanel>("Inspector").Visibility = Visibility.Visible;
        C<TextBlock>("ItemName").Text = current.Name; C<TextBlock>("ItemType").Text = current.ContentType;
        C<TextBox>("RenameText").Text = current.Name;
        C<TextBlock>("Attributes").Text = $"{current.Attributes.ByteLength:N0} bytes\nSHA-256\n{current.Attributes.Sha256}\n\nItem {current.Id}\nRevision {current.Revision}\nCreated {current.CreatedAt}\nModified {current.ModifiedAt}\nProperties: {current.Properties.GetRawText()}";
        C<TextBlock>("Access").Text = $"Owner context: local-owner\nPolicy: {current.AccessPolicyId}\nPolicy revision: {current.AccessPolicyRevision}\nOwner access is retained. New items are owner-private. Reader enumeration is not available in this slice; private status is not inferred from a revision.";
        var history = C<StackPanel>("Versions"); history.Children.Clear();
        if (current.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) && current.Attributes.ByteLength <= 65536)
        {
            var preview = new Button { Content = "Preview text…" };
            preview.Click += async (_, _) => await Run(async () =>
            {
                using var bytes = new MemoryStream(); await client.ExportAsync(current.CurrentVersionId, bytes, lifetime.Token);
                var text = new TextBox { Text = Encoding.UTF8.GetString(bytes.ToArray()), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 440, Width = 520 };
                await new ContentDialog { XamlRoot = root.XamlRoot, Title = current.Name, Content = text, CloseButtonText = "Close", RequestedTheme = ElementTheme.Dark }.ShowAsync();
            });
            history.Children.Add(preview);
        }
        foreach (var version in versions.OrderByDescending(v => v.CreatedAt))
        {
            var copy = new StackPanel { Spacing = 4 };
            copy.Children.Add(new TextBlock { Text = version.Id == current.CurrentVersionId ? "Current version" : "Historical version", Foreground = Brush(85, 196, 210), FontSize = 12 });
            copy.Children.Add(new TextBlock { Text = $"{version.ByteLength:N0} bytes · {version.CreatedAt}", FontSize = 11, TextWrapping = TextWrapping.Wrap });
            copy.Children.Add(new TextBlock { Text = version.Id, FontSize = 10, TextWrapping = TextWrapping.Wrap });
            var export = new Button { Content = copy, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10) };
            AutomationProperties.SetName(export, "Export version " + version.Id);
            export.Click += async (_, _) => await Run(() => ExportAsync(current, version.Id)); history.Children.Add(export);
        }
        C<TextBlock>("Status").Text = $"{current.Name} · {versions.Length} immutable versions{(versionPage.HasMore ? " shown · partial history; more versions exist" : "")}";
    }
    private FileOpenPicker OpenPicker()
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this)); return picker;
    }
    private async Task PickImportAsync()
    {
        var files = await OpenPicker().PickMultipleFilesAsync();
        if (files.Count == 0) { C<TextBlock>("Status").Text = "Import cancelled."; return; }
        foreach (var file in files) await ImportFileAsync(file);
        await RefreshAsync();
    }
    private async Task ImportFileAsync(StorageFile file)
    {
        using var bytes = await file.OpenStreamForReadAsync();
        selected = await client.ImportAsync(file.Name, string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, bytes, bytes.Length, lifetime.Token);
        // Importing while a collection is visible also adds membership explicitly.
        if (filter is { } collection)
        {
            filter = await client.AddToCollectionAsync(collection.Id, selected.Id, collection.Revision, lifetime.Token);
        }
    }
    private async Task NewVersionAsync()
    {
        if (selected is not { } item) return;
        var file = await OpenPicker().PickSingleFileAsync();
        if (file is null) { C<TextBlock>("Status").Text = "New version cancelled."; return; }
        using var bytes = await file.OpenStreamForReadAsync();
        selected = await client.ImportVersionAsync(item.Id, item.Revision, bytes, bytes.Length, lifetime.Token);
        await RefreshAsync();
    }
    private async Task ExportAsync(Item item, string versionId)
    {
        var picker = new FileSavePicker { SuggestedFileName = item.Name };
        picker.FileTypeChoices.Add("Exported item", new List<string> { ".bin" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) { C<TextBlock>("Status").Text = "Export cancelled."; return; }
        using var transaction = await file.OpenTransactedWriteAsync();
        using var bytes = transaction.Stream.AsStreamForWrite(); bytes.SetLength(0);
        await client.ExportAsync(versionId, bytes, lifetime.Token); await bytes.FlushAsync(lifetime.Token); await transaction.CommitAsync();
        C<TextBlock>("Status").Text = "Export complete · " + file.Name;
    }
}
