using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Identity;
using Xunit;

namespace BentaGo.Tests.Domain;

/// <summary>
/// Lockout policy (BG-005a decision: 5 consecutive failures lock the account
/// for 15 minutes). Pure-function tests over User snapshots — the same policy
/// the authentication service applies.
/// </summary>
public sealed class AuthLockoutTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private static User NewUser() => new()
    {
        Id = new UserId(Guid.NewGuid()),
        Username = "alice",
        PasswordHash = "hash",
        Role = Role.Peddler,
        CreatedBy = UserId.Anonymous,
        CreatedUtc = T0,
    };

    [Fact]
    public void FourFailures_DoNotLock()
    {
        User user = NewUser();
        for (int i = 0; i < 4; i++)
        {
            user = AccountLockout.OnFailedAttempt(user, T0);
        }

        Assert.Equal(4, user.FailedAttempts);
        Assert.False(AccountLockout.IsLocked(user, T0));
    }

    [Fact]
    public void FifthFailure_Locks_UntilFifteenMinutesLater()
    {
        User user = NewUser();
        for (int i = 0; i < 5; i++)
        {
            user = AccountLockout.OnFailedAttempt(user, T0);
        }

        Assert.Equal(AccountLockout.MaxFailedAttempts, user.FailedAttempts);
        Assert.Equal(T0 + AccountLockout.LockDuration, user.LockedUntilUtc);
        Assert.True(AccountLockout.IsLocked(user, T0));
    }

    [Fact]
    public void FailureWhileLocked_ChangesNothing_NeverExtends()
    {
        User user = NewUser();
        for (int i = 0; i < AccountLockout.MaxFailedAttempts; i++)
        {
            user = AccountLockout.OnFailedAttempt(user, T0);
        }

        User after = AccountLockout.OnFailedAttempt(user, T0.AddMinutes(1));
        Assert.Equal(user, after); // locked: the service refuses first; the policy never extends the window
    }

    [Fact]
    public void LockExpiry_Unlocks_AndCounterResetsOnNextFail()
    {
        User user = NewUser();
        for (int i = 0; i < AccountLockout.MaxFailedAttempts; i++)
        {
            user = AccountLockout.OnFailedAttempt(user, T0);
        }

        DateTimeOffset afterExpiry = T0 + AccountLockout.LockDuration;
        Assert.False(AccountLockout.IsLocked(user, afterExpiry)); // exactly at expiry is NOT locked

        user = AccountLockout.OnFailedAttempt(user, afterExpiry.AddMinutes(1));
        Assert.Equal(1, user.FailedAttempts); // counter reset by the expired lock, then one new fail
        Assert.Null(user.LockedUntilUtc);
    }

    [Fact]
    public void Success_ResetsCounterAndLock()
    {
        User user = NewUser();
        for (int i = 0; i < AccountLockout.MaxFailedAttempts; i++)
        {
            user = AccountLockout.OnFailedAttempt(user, T0);
        }

        User success = AccountLockout.OnSuccess(user);
        Assert.Equal(0, success.FailedAttempts);
        Assert.Null(success.LockedUntilUtc);
        Assert.False(AccountLockout.IsLocked(success, T0));
    }
}
