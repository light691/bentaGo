using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;

namespace BentaGo.Infrastructure.Security;

/// <summary>
/// Real authentication (BG-005a): resolves the account through
/// <see cref="IUserAccountStore"/> (the Users table), verifies the password
/// with BCrypt via <see cref="IPasswordHasher"/>, enforces the lockout
/// policy (<see cref="AccountLockout"/>: 5 fails / 15 minutes), and records
/// an AuditEvent for EVERY attempt through the ledger store. Implements BOTH
/// <see cref="IAuthenticationService"/> (the rich AuthResult) and the
/// UNCHANGED <see cref="ILoginService"/> (bool) so the WPF shell compiles and
/// runs untouched.
///
/// Audit rules: failure messages are fixed generic strings that never reveal
/// whether a username exists (docs/03:33); the actor is the attributed user
/// when the username maps to one, otherwise <see cref="UserId.Anonymous"/>;
/// terminal is the machine name per docs/01:77.
/// </summary>
public sealed class LoginService : IAuthenticationService, ILoginService
{
    private readonly IUserAccountStore _store;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILedgerStore _ledger;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// F1: in-process serialization of the authentication critical section.
    /// The read-verify-increment sequence must be atomic — a burst of
    /// concurrent attempts that each READ the counter, verify, and write a
    /// full-row snapshot could otherwise lose increments. The service is a
    /// host singleton, so one gate serializes every attempt in the process;
    /// each failure then reads the latest committed counter and writes its
    /// own +1 (no lost updates, no extended locks).
    /// </summary>
    private readonly SemaphoreSlim _authGate = new(1, 1);

    public LoginService(IUserAccountStore store, IPasswordHasher passwordHasher, ILedgerStore ledger, Func<DateTimeOffset> clock)
    {
        _store = store;
        _passwordHasher = passwordHasher;
        _ledger = ledger;
        _clock = clock;
    }

    /// <summary>The rich authentication path (BG-005a).</summary>
    public Task<AuthResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default) =>
        AuthenticateCoreAsync(username, password, cancellationToken);

    /// <summary>
    /// Placeholder-shell compatibility: the SAME authentication (audits and
    /// lockout included) reduced to the bool the WPF shell expects.
    /// </summary>
    async Task<bool> ILoginService.AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        AuthResult result = await AuthenticateCoreAsync(username, password, cancellationToken);
        return result.Success;
    }

    private async Task<AuthResult> AuthenticateCoreAsync(string username, string password, CancellationToken cancellationToken)
    {
        // F1: every attempt runs through the process-wide gate, so the
        // read-verify-increment sequence below is atomic (mutual exclusion).
        await _authGate.WaitAsync(cancellationToken);
        try
        {
            return await AuthenticateSerializedAsync(username, password, cancellationToken);
        }
        finally
        {
            _authGate.Release();
        }
    }

    private async Task<AuthResult> AuthenticateSerializedAsync(string username, string password, CancellationToken cancellationToken)
    {
        // Blank/empty submits are NOT attempts: no audit, no counter change,
        // generic failure. (IsNullOrEmpty covers null and "" so an empty
        // password is treated like a missing one; the shell never sends these
        // and they cannot leak anything.)
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return AuthResult.InvalidCredentials;
        }

        DateTimeOffset now = _clock();

        User? user = await _store.FindByUsernameAsync(username, cancellationToken);
        if (user is null)
        {
            // Timing-oracle guard: an unknown username still burns one BCrypt
            // verify (against the fixed dummy hash) so this path costs the
            // same as a real wrong-password attempt and cannot be timed to
            // reveal account existence. The result is deliberately discarded.
            VerifyDummy(password);
            AuditLogin(UserId.Anonymous, now,
                AuthenticationAudit.LoginFailureCategory, AuthenticationAudit.InvalidCredentialsMessage);
            return AuthResult.InvalidCredentials;
        }

        if (AccountLockout.IsLocked(user, now))
        {
            // Locked: refuse BEFORE any credential work; counter untouched
            // (AccountLockout no-extends while locked). The dummy verify keeps
            // the BCrypt cost constant on this path too.
            VerifyDummy(password);
            AuditLogin(user.Id, now,
                AuthenticationAudit.LoginFailureCategory, AuthenticationAudit.AccountLockedMessage);
            return AuthResult.AccountLocked;
        }

        // A deactivated account can never sign in; the response stays generic
        // so existence is not revealed through the failure text. Same dummy
        // verify for constant BCrypt cost.
        if (user.DeactivatedUtc is not null)
        {
            VerifyDummy(password);
            AuditLogin(user.Id, now,
                AuthenticationAudit.LoginFailureCategory, AuthenticationAudit.InvalidCredentialsMessage);
            return AuthResult.InvalidCredentials;
        }

        if (!_passwordHasher.Verify(password, user.PasswordHash))
        {
            // One failed attempt, derived from the counter read while holding
            // the auth gate (F1): the pure policy computes the next snapshot
            // (+1, lock at 5 until now+15, no-extend while locked, expiry
            // resets the counter) and the full-row write then cannot race a
            // concurrent attempt.
            User updated = AccountLockout.OnFailedAttempt(user, now);
            await _store.UpdateAsync(updated, cancellationToken);
            AuditLogin(user.Id, now,
                AuthenticationAudit.LoginFailureCategory, AuthenticationAudit.InvalidCredentialsMessage);
            return AuthResult.InvalidCredentials;
        }

        // Success: clear the failure state and record the happy path.
        await _store.UpdateAsync(AccountLockout.OnSuccess(user), cancellationToken);
        AuditLogin(user.Id, now,
            AuthenticationAudit.LoginSuccessCategory, AuthenticationAudit.LoginSuccessMessage);
        return AuthResult.Ok(user.Id, user.Role);
    }

    /// <summary>
    /// Burns one BCrypt verify against the fixed dummy hash (F2 timing-oracle
    /// guard). The result is discarded on purpose: it is never allowed to
    /// change the response.
    /// </summary>
    private void VerifyDummy(string password)
    {
        _passwordHasher.Verify(password, AuthenticationAudit.DummyPasswordHash);
    }

    /// <summary>One audit row per attempt, in its own tiny transaction (lane-9 style).</summary>
    private void AuditLogin(UserId actor, DateTimeOffset when, string category, string message)
    {
        _ledger.RecordAudit(new AuditEvent(
            -1, actor, when, category, message, Ref: null,
            Terminal: Environment.MachineName, Reason: null));
    }
}
