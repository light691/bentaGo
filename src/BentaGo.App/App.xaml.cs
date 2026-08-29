using System.Windows;
using BentaGo.App.Mvvm;
using BentaGo.App.Themes;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App;

public partial class App : System.Windows.Application
{
    private HighContrastManager? _highContrast;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                MessageBox.Show(args.Exception?.Message ?? "An unexpected error occurred.",
                    "BentaGo error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // The error window itself failed; never crash the process silently.
            }
            args.Handled = true;
        };

        // Lane boot args (bg-004-synthesis §11): --size WxH sets the window
        // size (defaults to the 1366x768 reference), --sim-hc forces the
        // high-contrast dictionary for the L9 lane on hosts where the OS
        // toggle does not fire live.
        int width = 1366;
        int height = 768;
        bool simulateHighContrast = false;
        string[] args = e.Args;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--size" && i + 1 < args.Length && TryParseSize(args[i + 1], out int w, out int h))
            {
                width = w;
                height = h;
                i++;
            }
            else if (args[i] == "--sim-hc")
            {
                simulateHighContrast = true;
            }
        }

        var provider = ObservableObjectFactory.Build();
        try
        {
            // Boot sequence: migrate the database and seed the initial admin
            // before the login shell appears (docs/02 boot contract).
            provider.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync().GetAwaiter().GetResult();
            provider.GetRequiredService<IAdminSeeder>().SeedAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Data path failure (locked, corrupt, read-only) is explained and
            // the app exits without partial files.
            MessageBox.Show(ex.Message, "BentaGo cannot start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _highContrast = new HighContrastManager(this, () => SystemParameters.HighContrast || simulateHighContrast);

        var viewModel = provider.GetRequiredService<MainViewModel>();
        var window = new MainWindow(viewModel)
        {
            Width = width,
            Height = height,
        };
        window.Show();
        viewModel.Boot();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _highContrast?.Dispose();
        base.OnExit(e);
    }

    private static bool TryParseSize(string text, out int width, out int height)
    {
        width = 0;
        height = 0;
        string[] parts = text.Split('x');
        return parts.Length == 2
               && int.TryParse(parts[0], out width)
               && int.TryParse(parts[1], out height)
               && width > 0
               && height > 0;
    }
}