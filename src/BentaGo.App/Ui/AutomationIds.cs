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
    public const string SalesDiscountText = "Sales.DiscountText";
    public const string SalesErrorBanner = "Sales.ErrorBanner";
    public const string SalesCorrectionsButton = "Sales.QuickAction.Corrections";

    // Corrections dialog (BG-007c2): void / return / damage-loss / adjustment.
    public const string CorrectionsModeList = "Corrections.ModeList";
    public const string CorrectionsSalePicker = "Corrections.SalePicker";
    public const string CorrectionsLinePicker = "Corrections.LinePicker";
    public const string CorrectionsProductPicker = "Corrections.ProductPicker";
    public const string CorrectionsReasonBox = "Corrections.ReasonBox";
    public const string CorrectionsQuantityBox = "Corrections.QuantityBox";
    public const string CorrectionsReturnQuantityBox = "Corrections.ReturnQuantityBox";
    public const string CorrectionsApproverPicker = "Corrections.ApproverPicker";
    public const string CorrectionsDestinationPicker = "Corrections.DestinationPicker";
    public const string CorrectionsKindList = "Corrections.KindList";
    public const string CorrectionsSubmitButton = "Corrections.SubmitButton";
    public const string CorrectionsErrorBanner = "Corrections.ErrorBanner";

    // Transactions (BG-006b)
    public const string TransactionsList = "Transactions.List";
    public const string TransactionsFilter = "Transactions.Filter";
    public const string TransactionsRefresh = "Transactions.Refresh";

    // Payment dialog
    public const string PaymentMethodTabs = "Payment.MethodTabs";
    public const string PaymentConfirmButton = "Payment.ConfirmButton";

    // Stock
    public const string StockSearchBox = "Stock.StockSearchBox";

    // Reconciliation
    public const string ReconciliationActualCashBox = "Reconciliation.ActualCashBox";
    public const string ReconciliationSubmitButton = "Reconciliation.SubmitCloseButton";

    // Admin (BG-005d): user management, catalog import, admin overview.
    public const string RailAdminUsers = "Rail.AdminUsers";
    public const string RailAdminImport = "Rail.AdminImport";
    public const string RailAdminOverview = "Rail.AdminOverview";
    public const string AdminUsersUsername = "Admin.Users.Username";
    public const string AdminUsersPassword = "Admin.Users.Password";
    public const string AdminUsersCreate = "Admin.Users.Create";
    public const string AdminUsersChangePassword = "Admin.Users.ChangePassword";
    public const string AdminUsersRefresh = "Admin.Users.Refresh";
    public const string AdminImportFilePath = "Admin.Import.FilePath";
    public const string AdminImportBrowse = "Admin.Import.Browse";
    public const string AdminImportPreview = "Admin.Import.Preview";
    public const string AdminImportConfirm = "Admin.Import.Confirm";
    public const string AdminOverviewRefresh = "Admin.Overview.Refresh";
    public const string AdminOverviewPeddlerTabs = "Admin.Overview.PeddlerTabs";

    // BG-007: printer profile + test receipt + scanner diagnostics.
    public const string RailAdminDevices = "Rail.AdminDevices";
    public const string AdminDevicesProfile = "Admin.Devices.Profile";
    public const string AdminDevicesSave = "Admin.Devices.Save";
    public const string AdminDevicesTestPrint = "Admin.Devices.TestPrint";
    public const string AdminDevicesRefresh = "Admin.Devices.Refresh";
    public const string AdminDevicesScanList = "Admin.Devices.ScanList";
}
