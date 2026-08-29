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

    // Reserved for BG-005; catalogued so the help overlay renders them greyed.
    public const string Transactions = "transactions";
    public const string Returns = "returns";
}
