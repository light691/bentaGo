namespace BentaGo.App.Ui;

/// <summary>
/// Stable AutomationIds / FocusTarget.Id values (bg-004-synthesis §9, §11):
/// the lane drivers, the STA tests, and the FocusCoordinator address elements
/// through these names, so renaming an element is a compile + test event.
/// Values are "Screen.Element" by convention; they are string attributes, not
/// WPF x:Name values (which cannot contain dots).
/// </summary>
public static class AutomationIds
{
    // Shell
    public const string RailHome = "Rail.Home";
    public const string RailSales = "Rail.Sales";
    public const string RailStock = "Rail.Stock";
    public const string RailTransactions = "Rail.Transactions";
    public const string RailReconciliation = "Rail.Reconciliation";
    public const string HelpOverlayRoot = "HelpOverlay.Root";

    // Login
    public const string LoginUsername = "Login.UsernameBox";
    public const string LoginPassword = "Login.PasswordBox";
    public const string LoginSubmit = "Login.Submit";

    // Peddler home
    public const string HomeNewSaleButton = "Home.NewSaleButton";
    public const string HomeStockButton = "Home.StockButton";
    public const string HomeEndTripButton = "Home.EndTripButton";

    // Sales / POS
    public const string SalesScanBox = "Sales.ScanBox";
    public const string SalesSearchBox = "Sales.SearchBox";
    public const string SalesCart = "Sales.Cart";
    public const string SalesCheckoutButton = "Sales.CheckoutButton";

    // Payment dialog
    public const string PaymentMethodTabs = "Payment.MethodTabs";
    public const string PaymentConfirmButton = "Payment.ConfirmButton";

    // Stock
    public const string StockSearchBox = "Stock.StockSearchBox";

    // Reconciliation
    public const string ReconciliationActualCashBox = "Reconciliation.ActualCashBox";
    public const string ReconciliationSubmitButton = "Reconciliation.SubmitCloseButton";
}
