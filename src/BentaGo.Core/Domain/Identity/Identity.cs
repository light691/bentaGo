namespace BentaGo.Core.Domain.Identity;

/// <summary>
/// The three roles (BG-005a decision). Admin owns every capability;
/// Supervisor is barred from user management, configuration, and high-risk
/// adjustments (docs/01:34); Peddler is operational data only (docs/01:26).
/// Capability checks live on <see cref="RoleExtensions"/> as small pure
/// predicates so use cases can gate on them directly.
/// </summary>
public enum Role
{
    Admin,
    Supervisor,
    Peddler,
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
    public PeddlerId? PeddlerLink { get; init; }
    public int FailedAttempts { get; init; }
    public DateTimeOffset? LockedUntilUtc { get; init; }
    public DateTimeOffset? DeactivatedUtc { get; init; }
    public UserId CreatedBy { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
}

/// <summary>
/// A selling agent (an inventory location per docs/01:40). Peddler login and
/// isolation are a later slice; the nullable direct link User.PeddlerLink
/// already exists so the link can be recorded now (BG-005a decision, Employee
/// deferred).
/// </summary>
public sealed record Peddler
{
    public PeddlerId Id { get; init; }
    public string DisplayName { get; init; } = "";
    public bool Active { get; init; }
}
