namespace BentaGo.Core.Domain.Identity;

/// <summary>
/// The store roles (BG-005a decision). Admin owns every capability;
/// Supervisor is barred from user management, configuration, and high-risk
/// adjustments (docs/01:34). Capability checks live on
/// <see cref="RoleExtensions"/> as small pure predicates so use cases can
/// gate on them directly.
/// </summary>
public enum Role
{
    Admin,
    Supervisor,
}

/// <summary>
/// A local account. The auth state (FailedAttempts/LockedUntilUtc) sits on
/// the record as immutable snapshot fields: the lockout policy returns a NEW
/// snapshot (<see cref="AccountLockout"/>) that the caller persists, so the
/// domain never mutates in place. CreatedBy/CreatedUtc follow the
/// CreatedBy/WhenUtc convention of every other domain fact.
/// </summary>
public sealed record User
{
    public UserId Id { get; init; }
    public string Username { get; init; } = "";
    public string PasswordHash { get; init; } = "";
    public Role Role { get; init; }
    public int FailedAttempts { get; init; }
    public DateTimeOffset? LockedUntilUtc { get; init; }
    public DateTimeOffset? DeactivatedUtc { get; init; }
    public UserId CreatedBy { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
}
