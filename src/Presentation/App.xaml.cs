using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Mskpy.Application;
using Mskpy.Infrastructure;
using Mskpy.Presentation.Services;
using Mskpy.Presentation.ViewModels;

namespace Mskpy.Windows;

public partial class App : System.Windows.Application
{
    ExistingApiGateway? gateway;
    ShellViewModel? shell;

    // Inspection services (session-independent parts created at startup).
    InspectionIndex? inspectionIndex;
    InspectionMultipartBuilder? inspectionBuilder;
    LocationProvider? inspectionLocation;
    WindowsLocationProvider? winLoc;

    // Optional override for the inspection host (e.g. http://127.0.0.1:15100 for the P9 mock).
    readonly string? inspectionHostOverride;

    public App()
    {
        inspectionHostOverride = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--host="))?.Substring("--host=".Length);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        InstallCrashGuards();

        // V5: Headless CLI submit mode. If --submit=<consumer> is passed,
        // run the submit flow headlessly and exit.
        var cliArgs = Environment.GetCommandLineArgs();
        string? cliSubmit = null, cliLogin = null, cliVendor = null, cliPhotosDir = null, cliIndexPath = null;
        bool cliHostOverride = false;
        foreach (var a in cliArgs)
        {
            if (a.StartsWith("--submit=")) cliSubmit = a.Substring("--submit=".Length);
            else if (a.StartsWith("--login=")) cliLogin = a.Substring("--login=".Length);
            else if (a.StartsWith("--vendor=")) cliVendor = a.Substring("--vendor=".Length);
            else if (a.StartsWith("--photos=")) cliPhotosDir = a.Substring("--photos=".Length);
            else if (a.StartsWith("--index=")) cliIndexPath = a.Substring("--index=".Length);
            else if (a.StartsWith("--host=")) cliHostOverride = true;
        }
        if (!string.IsNullOrWhiteSpace(cliSubmit))
        {
            gateway = new ExistingApiGateway(new ApiClient());

            inspectionIndex = new InspectionIndex(cliIndexPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RenewableEnergyDesktop", "index.tsv"));

            inspectionBuilder = new InspectionMultipartBuilder();
            winLoc = new WindowsLocationProvider(TimeSpan.FromSeconds(10));
            inspectionLocation = new LocationProvider { Geolocator = winLoc.GetLocationAsync, GeolocatorTimeout = TimeSpan.FromSeconds(10) };

            shell = new ShellViewModel(new ClientWorkflow(gateway), new PasswordBoxInput(null!),
                new WpfDesktopActions(null!), inspectionIndex, inspectionBuilder, inspectionLocation, winLoc,
                cliHostOverride ? inspectionHostOverride : null,
                cliIndexPath ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RenewableEnergyDesktop", "index.tsv"));

            shell.CliSubmitConsumer = cliSubmit;
            shell.CliSubmitLogin = cliLogin ?? "";
            shell.CliSubmitVendor = cliVendor ?? "";
            shell.CliSubmitPhotosDir = cliPhotosDir ?? "";

            var exitCode = await shell.RunCliSubmitAsync();
            Shutdown(exitCode);
            return;
        }

        gateway = new ExistingApiGateway(new ApiClient());

        // P2: Load index.tsv at startup. Path from args or default location.
        string? tsvPath = null;
        foreach (var a in cliArgs)
            if (a.StartsWith("--index=")) tsvPath = a.Substring("--index=".Length);
        tsvPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RenewableEnergyDesktop", "index.tsv");
        inspectionIndex = new InspectionIndex(tsvPath);

        // P5 builder + P6/P7 location (Windows geolocator injected from Presentation).
        inspectionBuilder = new InspectionMultipartBuilder();
        winLoc = new WindowsLocationProvider(TimeSpan.FromSeconds(10));
        inspectionLocation = new LocationProvider { Geolocator = winLoc.GetLocationAsync, GeolocatorTimeout = TimeSpan.FromSeconds(10) };

        var window = new MainWindow();
        shell = new ShellViewModel(new ClientWorkflow(gateway), window.SecretInput,
            new WpfDesktopActions(window), inspectionIndex, inspectionBuilder, inspectionLocation, winLoc, inspectionHostOverride, tsvPath);
        window.DataContext = shell;
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e) { shell?.Dispose(); gateway?.Dispose(); base.OnExit(e); }

    /// <summary>Where fatal errors are appended so a field crash is diagnosable.</summary>
    internal static string ErrorLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RenewableEnergyDesktop", "app-errors.log");

    static void LogCrash(string kind, Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(ErrorLogPath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(ErrorLogPath,
                $"{DateTime.Now:s} [{kind}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }

    /// <summary>
    /// A linestaff officer must never lose a staged inspection because an
    /// unexpected error killed the process. UI-thread errors are reported and
    /// swallowed; background-thread errors are logged before the process ends.
    /// </summary>
    void InstallCrashGuards()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("ui", args.Exception);
            args.Handled = true;
            try
            {
                MessageBox.Show(
                    $"Unexpected error — the form is still open. Close and reopen the inspection window and repeat the step.\n\n{args.Exception.Message}",
                    "MSKPY-Windows", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { /* no UI available */ }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) LogCrash("background", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("task", args.Exception);
            args.SetObserved();
        };
    }
}
