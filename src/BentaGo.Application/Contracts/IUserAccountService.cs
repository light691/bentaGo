using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Identity;

namespace BentaGo.Application.Contracts;

/// <summary>
/// User account use cases (BG-005a). Every command carries By (acting user)
/// and NowUtc per convention, and every case derives the acting user from the
/// SESSION <see cref="IActorContext"/> and enforces By == CurrentUserId
/// (AUTH_ACTOR_MISMATCH, BG-005d F8) before gating the capability through the
/// <see cref="RoleExtensions"/> predicates. A failure result means nothing was
/// persisted.
/// </summary>
public interface IUserAccountService
{
    /// <summary>Admin-only: creates a local account (hashed, active, zero auth state).</summary>
    Task<Result<User>> CreateUserAsync(CreateUserCommand cmd, CancellationToken cancellationToken = default);

    /// <summary>Admin-only: activates (clears DeactivatedUtc) or deactivates (sets it) an account.</summary>
    Task<Result<User>> SetUserActiveAsync(SetUserActiveCommand cmd, CancellationToken cancellationToken = default);

    /// <summary>Changes a password: admins for anyone, any user for themselves.</summary>
    Task<Result<User>> ChangePasswordAsync(ChangePasswordCommand cmd, CancellationToken cancellationToken = default);
}
