namespace BentaGo.App.Navigation;

/// <summary>
/// Open-set screen keys (bg-004-synthesis §5.1): BG-005 adds admin screens
/// without recompiling an enum; constants only at call sites. Keys listed
/// here but not registered (Transactions, Returns) are catalogued so the F1
/// overlay can render them greyed; CanNavigate returns false for them.
/// </summary>
public static class ScreenKeys
{
    public const string Login = "login";
    public const string Home = "home";
    public const string Sales = "sales";
    public const string Stock = "stock";
    public const string Reconciliation = "reconciliation";

    // Admin (BG-005d-ui): Admin-role rail entries. The keys are registered
    // (navigable post-auth) but the rail, the Alt+6/7/8 shell command, and
    // each admin view model gate on RoleExtensions.CanManageUsers, so a
    // non-admin session can never reach them.
    public const string AdminUsers = "admin-users";
    public const string AdminImport = "admin-import";
    public const string AdminDevices = "admin-devices";

    // Reserved for BG-005; catalogued so the help overlay renders them greyed.
    public const string Transactions = "transactions";
    public const string Returns = "returns";
}
