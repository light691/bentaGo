using BentaGo.App.Ui;

namespace BentaGo.App.Navigation;

/// <summary>
/// The concrete screen registry (bg-004-synthesis §5.1/§5.2): keys -&gt; view
/// model types -&gt; initial focus targets. Transactions/Returns are catalogued
/// in <see cref="ScreenKeys"/> but NOT registered here (parked until BG-005,
/// so CanNavigate stays false for them).
/// </summary>
public sealed class ScreenRegistry
{
    public static IReadOnlyList<ScreenRegistration> All { get; } =
    [
        new ScreenRegistration(ScreenKeys.Login, typeof(BentaGo.App.Features.Login.LoginViewModel), Ui.AutomationIds.LoginUsername),
        new ScreenRegistration(ScreenKeys.Home, typeof(BentaGo.App.Features.PeddlerHome.PeddlerHomeViewModel), Ui.AutomationIds.HomeNewSaleButton),
        new ScreenRegistration(ScreenKeys.Sales, typeof(BentaGo.App.Features.Sales.SalesViewModel), Ui.AutomationIds.SalesScanBox),
        new ScreenRegistration(ScreenKeys.Stock, typeof(BentaGo.App.Features.Stock.StockViewModel), Ui.AutomationIds.StockSearchBox),
        new ScreenRegistration(ScreenKeys.Reconciliation, typeof(BentaGo.App.Features.Reconciliation.ReconciliationViewModel), Ui.AutomationIds.ReconciliationActualCashBox),
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
