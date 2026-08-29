using BentaGo.App.Ui;

namespace BentaGo.App.Navigation;

/// <summary>
/// The concrete screen registry (bg-004-synthesis §5.1/§5.2): keys -&gt; view
/// model types -&gt; initial focus targets. Returns is catalogued in
/// <see cref="ScreenKeys"/> but NOT registered here (parked until returns
/// land, so CanNavigate stays false for it).
/// </summary>
public sealed class ScreenRegistry
{
    public static IReadOnlyList<ScreenRegistration> All { get; } =
    [
        new ScreenRegistration(ScreenKeys.Login, typeof(BentaGo.App.Features.Login.LoginViewModel), Ui.AutomationIds.LoginUsername),
        new ScreenRegistration(ScreenKeys.Home, typeof(BentaGo.App.Features.PeddlerHome.PeddlerHomeViewModel), Ui.AutomationIds.HomeNewSaleButton),
        new ScreenRegistration(ScreenKeys.Sales, typeof(BentaGo.App.Features.Sales.SalesViewModel), Ui.AutomationIds.SalesScanBox),
        new ScreenRegistration(ScreenKeys.Stock, typeof(BentaGo.App.Features.Stock.StockViewModel), Ui.AutomationIds.StockSearchBox),
        new ScreenRegistration(ScreenKeys.Transactions, typeof(BentaGo.App.Features.Transactions.TransactionsViewModel), Ui.AutomationIds.TransactionsList),
        new ScreenRegistration(ScreenKeys.Reconciliation, typeof(BentaGo.App.Features.Reconciliation.ReconciliationViewModel), Ui.AutomationIds.ReconciliationActualCashBox),
        // Admin (BG-005d-ui); reachability is role-gated (see ScreenKeys).
        new ScreenRegistration(ScreenKeys.AdminUsers, typeof(BentaGo.App.Features.Admin.UserManagementViewModel), Ui.AutomationIds.AdminUsersUsername),
        new ScreenRegistration(ScreenKeys.AdminImport, typeof(BentaGo.App.Features.Admin.CatalogImportViewModel), Ui.AutomationIds.AdminImportFilePath),
        new ScreenRegistration(ScreenKeys.AdminOverview, typeof(BentaGo.App.Features.Admin.AdminOverviewViewModel), Ui.AutomationIds.AdminOverviewRefresh),
    ];

    public ScreenRegistry()
        : this(All)
    {
    }

    /// <summary>Test seam: an extended registry for eviction-policy machinery tests.</summary>
    public ScreenRegistry(IReadOnlyList<ScreenRegistration> registrations)
    {
        Registrations = registrations;
    }

    public IReadOnlyList<ScreenRegistration> Registrations { get; }
}
