using Storage.Client;
using System.Runtime.Versioning;
using Storage.Service.Pairing;
namespace Storage.Service;
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Storage Service production host requires Windows. Use Storage.Tests for Linux transport/core validation."); return 2; }
        return RunWindows(args);
    }
    [SupportedOSPlatform("windows")]
    private static int RunWindows(string[] args)
    {
        try
        {
            DevelopmentGrant? development = null;
            bool approve = args.SequenceEqual(new[] { "--approve-bundled-browser" });
            if (args.SequenceEqual(new[] { "--stop" })) { ServiceLifetime.StopBundled(Environment.ProcessPath!); return 0; }
            if (args.Length != 0 && !approve)
            {
                if (args.Length != 6 || args[0] != "--dev-browser" || args[2] != "--dev-sha256" || args[4] != "--dev-assembly-sha256" || !File.Exists(args[1]) || args[3].Length != 64 || args[5].Length != 64 || !args[3].All(Uri.IsHexDigit) || !args[5].All(Uri.IsHexDigit))
                    throw new ArgumentException("Usage: Storage.Service [--dev-browser FULL_EXE_PATH --dev-sha256 EXE_SHA256 --dev-assembly-sha256 DLL_SHA256]");
                development = new(Path.GetFullPath(args[1]), args[3], args[5]);
                Console.Error.WriteLine("DEVELOPMENT ENROLLMENT: exact Browser executable + managed assembly hashes; no third-party enrollment. This is not a same-user malware sandbox.");
            }
            using var singleton = new Mutex(true, "Local\\" + WindowsPeers.PipeName.Replace('\\', '.'), out bool owns);
            if (!owns) { Console.Error.WriteLine("Storage Service is already running for this user."); return 3; }
            string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everyonesoftware", "Storage");
            var consent = new BundledBrowserConsent(AppContext.BaseDirectory, Path.Combine(home, "service-config"));
            if (development is null)
            {
                development = consent.Load();
                if (development is null && approve)
                {
                    if (!consent.RequestApproval(BundledBrowserConsent.ConfirmOnWindows))
                    { Console.Error.WriteLine("Browser approval required. No grant was saved."); return 4; }
                    development = consent.Load();
                }
            }
            using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ServiceLifetime.StopEventName(Environment.ProcessPath!));
            // This delegate runs off the STA. Its task owns the entire session and core,
            // so cancellation is not complete until native work and disposal have drained.
            var controller = new TrayController(async (ready, token) =>
            {
                token.ThrowIfCancellationRequested();
                string stage = Path.Combine(home, "staging"); Directory.CreateDirectory(stage);
                foreach (string partial in Directory.EnumerateFiles(stage, "*.partial")) File.Delete(partial);
                string library = Path.Combine(AppContext.BaseDirectory, "storage_core.dll");
                if (!File.Exists(library)) throw new FileNotFoundException("Deploy the real Windows storage_core.dll beside Storage.Service.exe.");
                using var core = new NativeCore(Path.Combine(home, "repository"), library);
                var health = core.Execute(WireJson.Element(new { op = "health", args = new { } }));
                if (!health.GetProperty("ok").GetBoolean() || !health.TryGetProperty("result", out var result) ||
                    !result.TryGetProperty("status", out var status) || status.GetString() != "healthy")
                    throw new IOException("Repository initialization failed: " + health.GetRawText());
                token.ThrowIfCancellationRequested();
                await WindowsHost.RunAsync(new ServiceSession(core, stage), development, token, ready);
            });
            var pairing = new PairingManager(Path.Combine(home, "pairing"));
            using var tray = new NativeTrayWindow(controller, pairing);
            var stopRegistration = ThreadPool.RegisterWaitForSingleObject(stopEvent, (_, _) => tray.RequestQuit(), null, Timeout.Infinite, true);
            try
            {
                return tray.Run();
            }
            finally
            {
                stopRegistration.Unregister(null);
                controller.QuitAsync().GetAwaiter().GetResult();
                singleton.ReleaseMutex();
            }
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
