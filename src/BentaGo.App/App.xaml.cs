using System.Windows;
using BentaGo.App.Mvvm;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App;

public partial class App : System.Windows.Application
{
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

        try
        {
            // Boot sequence: migrate the database and seed the initial admin
            // before the login shell appears (docs/02 boot contract).
            var provider = ObservableObjectFactory.Build();
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

        new MainWindow().Show();
    }
}
