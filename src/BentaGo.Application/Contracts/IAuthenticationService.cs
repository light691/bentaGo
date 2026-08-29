using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Identity;

namespace BentaGo.Application.Contracts;

/// <summary>Why an authentication attempt failed (BG-005a decision).</summary>
public enum AuthFailureReason
{
    InvalidCredentials,
    AccountLocked,
}

/// <summary>
/// Outcome of an authentication attempt: Success/Failure plus the attributed
/// user and role on success and the reason code on failure (BG-005a
/// decision). Never carries a password and never echoes the submitted
/// username, so nothing here can leak account existence through copy.
/// </summary>
public sealed record AuthResult(
    bool Success,
    UserId? UserId = null,
    Role? Role = null,
    AuthFailureReason? FailureReason = null)
{
    public static AuthResult Ok(UserId userId, Role role) => new(true, userId, role);

    public static AuthResult InvalidCredentials =>
        new(false, FailureReason: AuthFailureReason.InvalidCredentials);

    public static AuthResult AccountLocked =>
        new(false, FailureReason: AuthFailureReason.AccountLocked);
}

/// <summary>
/// The real authentication path (BG-005a): verifies the password against the
/// Users table, enforces the lockout policy, and records an AuditEvent for
/// EVERY attempt (success or failure) through the ledger store. The
/// Infrastructure LoginService implements both this contract and the
/// placeholder <see cref="ILoginService"/> so the WPF shell keeps compiling
/// unchanged.
/// </summary>
public interface IAuthenticationService
{
    Task<AuthResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default);
}