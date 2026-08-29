namespace BentaGo.Core.Domain.Identity;

/// <summary>
/// Audit categories and messages for authentication attempts (BG-005a
/// decision): EVERY attempt records an AuditEvent through the ledger store.
/// Failure messages never reveal whether a username exists — a fixed generic
/// string for bad credentials and a separate one for a locked account
/// (docs/03:33). The category carries the success/failure distinction.
/// </summary>
public static class AuthenticationAudit
{
    public const string LoginSuccessCategory = "AUTH.LOGIN_SUCCESS";
    public const string LoginFailureCategory = "AUTH.LOGIN_FAILURE";

    public const string LoginSuccessMessage = "sign-in succeeded";
    public const string InvalidCredentialsMessage = "invalid username or password";
    public const string AccountLockedMessage = "account is temporarily locked";

    /// <summary>
    /// Fixed BCrypt hash (work factor 12) of the literal "dummy". The
    /// authentication service verifies the submitted password against it on
    /// paths that cannot run a real credential check (unknown username,
    /// locked account, deactivated account), so those paths cost the same
    /// BCrypt round as a real wrong-password attempt and cannot be timed to
    /// reveal whether an account exists (timing-oracle guard). The verify
    /// result is deliberately discarded.
    /// </summary>
    public const string DummyPasswordHash = "$2a$12$tNzTFnziOK72YexWpoBdG.e/42KugZJ1VxUOa4iBFWgoEzyymyv8O";
}