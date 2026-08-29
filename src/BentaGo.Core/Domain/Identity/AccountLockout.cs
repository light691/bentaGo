namespace BentaGo.Core.Domain.Identity;

/// <summary>
/// The account lockout policy (BG-005a decision): 5 consecutive failed
/// attempts lock the account for 15 minutes. Pure functions over immutable
/// <see cref="User"/> snapshots — the authentication service persists the
/// snapshot a function returns. The failure counter resets on success and
/// after a lock has expired; a failure while locked changes nothing (the
/// service refuses the attempt before consulting this).
/// </summary>
public static class AccountLockout
{
    public const int MaxFailedAttempts = 5;

    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    /// <summary>True while the account is inside an active lock window.</summary>
    public static bool IsLocked(User user, DateTimeOffset nowUtc) =>
        user.LockedUntilUtc is { } until && until > nowUtc;

    /// <summary>A successful sign-in clears the failure state.</summary>
    public static User OnSuccess(User user) => user with { FailedAttempts = 0, LockedUntilUtc = null };

    /// <summary>
    /// Applies one failed attempt: an expired lock resets the counter first,
    /// then the attempt increments it; reaching <see cref="MaxFailedAttempts"/>
    /// opens a lock until <paramref name="nowUtc"/> + <see cref="LockDuration"/>.
    /// While the account is locked nothing changes (never extend a lock).
    /// </summary>
    public static User OnFailedAttempt(User user, DateTimeOffset nowUtc)
    {
        if (IsLocked(user, nowUtc))
        {
            return user;
        }

        int failed = user.FailedAttempts;
        DateTimeOffset? lockedUntil = user.LockedUntilUtc;

        if (lockedUntil is { } until && until <= nowUtc)
        {
            failed = 0;
            lockedUntil = null;
        }

        failed += 1;
        if (failed >= MaxFailedAttempts)
        {
            lockedUntil = nowUtc + LockDuration;
        }

        return user with { FailedAttempts = failed, LockedUntilUtc = lockedUntil };
    }
}