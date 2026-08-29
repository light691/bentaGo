using BentaGo.App.Data;
using BentaGo.App.Dialogs;
using BentaGo.App.Dialogs.Confirm;
using BentaGo.App.Dialogs.Payment;
using BentaGo.App.Features.Admin;
using BentaGo.App.Features.Help;
using BentaGo.App.Features.Login;
using BentaGo.App.Features.PeddlerHome;
using BentaGo.App.Features.Reconciliation;
using BentaGo.App.Features.Sales;
using BentaGo.App.Features.Stock;
using BentaGo.App.Features.Transactions;
using BentaGo.App.Navigation;
using BentaGo.Application.Contracts;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Import;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Mvvm;

/// <summary>
/// Builds the application service provider: BentaGoHost services plus
/// App-level view model and shell registrations.
/// </summary>
public static class ObservableObjectFactory
{
    private static ServiceProvider? _provider;

    public static IServiceProvider Build()
    {
        if (_provider is not null)
        {
            return _provider;
        }

        _provider = BentaGoHost.Build(extra: RegisterAppServices);
        return _provider;
    }

    /// <summary>
    /// The full BG-004 app registration surface. Tests build their own host
    /// with a throwaway data root and call this so the real shell and the
    /// test host can never drift apart. The BG-005 data-source swap is the
    /// single line marked below (bg-004-synthesis §12).
    /// </summary>
    public static void RegisterAppServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        services.AddSingleton<ScreenRegistry>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IFocusService, FocusCoordinator>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IAppDataSource, ScenarioDataSource>(); // BG-005: swap this line only

        // BG-005d admin glue: the import seam is intentionally NOT
        // host-registered until the UI wires it (IProductImportService
        // contract doc), and the admin read-mostly glue composes the
        // registered persistence + ledger seams (users/peddlers enumeration
        // reads use the EF context — no store enumeration seam exists).
        services.AddSingleton<IProductStore, ProductStore>();
        services.AddSingleton<IProductImportService, ProductImportService>();
        services.AddSingleton<AdminReadService>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<PeddlerHomeViewModel>();
        services.AddTransient<SalesViewModel>();
        services.AddTransient<StockViewModel>();
        services.AddTransient<TransactionsViewModel>();
        services.AddTransient<ReconciliationViewModel>();
        services.AddTransient<UserManagementViewModel>();
        services.AddTransient<CatalogImportViewModel>();
        services.AddTransient<AdminOverviewViewModel>();
        services.AddTransient<PaymentDialogViewModel>();   // dialogs, not screens
        services.AddTransient<ConfirmDialogViewModel>();
        services.AddTransient<HelpOverlayViewModel>();
    }
}
