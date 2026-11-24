using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace Storage.Browser;
internal static class Program
{
    [STAThread] public static void Main(string[] args)
    {
        string service = Path.Combine(AppContext.BaseDirectory, "Service", "Storage.Service.exe");
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ =>
            {
                try { Storage.Client.ServiceLifetime.StopBundled(service); }
                finally { Storage.Client.WindowsStartup.Remove(service); }
            })
            .OnBeforeUpdateFastCallback(_ => Storage.Client.ServiceLifetime.StopBundled(service)).Run();
        // A real authorized IPC diagnostic, not a test-only bypass or core shortcut.
        if (args.Length == 2 && args[0] == "--service-health")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var client = new Storage.Client.StorageClient(token => Storage.Client.WindowsPeers.ConnectAsync(service, token));
            var health = client.HealthAsync(timeout.Token).GetAwaiter().GetResult();
            File.WriteAllBytes(args[1], System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(health, Storage.Client.WireJson.Options));
            return;
        }
        using var activation = new ActivationCoordinator(new ActivationWindowsPlatform());
        if (!activation.IsOwner) return;
        bool fromTray = args.Contains("--from-tray", StringComparer.Ordinal);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new StorageApplication(activation, fromTray);
        });
    }
}
public sealed partial class StorageApplication : Application, Microsoft.UI.Xaml.Markup.IXamlMetadataProvider
{
    private readonly Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider metadata = new();
    private MainWindow? window;
    private readonly ActivationCoordinator activation;
    private readonly bool fromTray;
    internal StorageApplication(ActivationCoordinator activation, bool fromTray)
    {
        this.activation = activation;
        this.fromTray = fromTray;
    }
    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(Type type) => metadata.GetXamlType(type);
    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(string name) => metadata.GetXamlType(name);
    public Microsoft.UI.Xaml.Markup.XmlnsDefinition[] GetXmlnsDefinitions() => metadata.GetXmlnsDefinitions();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        Resources["SystemAccentColor"] = Windows.UI.Color.FromArgb(255, 54, 179, 198);
        window = new MainWindow(fromTray);
        window.Closed += (_, _) => activation.Dispose();
        window.Activate();
        activation.Attach(action => { _ = window.DispatcherQueue.TryEnqueue(() => action()); }, window.ActivateFromExistingLaunch);
    }
}
