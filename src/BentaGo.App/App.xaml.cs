using System.Windows;

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
        new MainWindow().Show();
    }
}
