using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Infrastructure.Security;
using Xunit;

namespace BentaGo.Tests.Foundation;

/// <summary>
/// F3 (BG-005d): auth-state write + audit are one LOGICAL unit of work.
/// SQLite cannot span a single transaction across the account store's EF
/// context and the ledger store's long-lived raw connection, so LoginService
/// uses explicit compensation: persist the state change, then the audit; on
/// an audit write failure the user row is reverted to the pre-attempt
/// snapshot before the exception propagates. These tests drive the real
/// LoginService over the IN-MEMORY mirrors (InMemoryUserAccountStore +
/// InMemoryLedgerStore) so the counter/audit parity the audit follow-up
/// demands is asserted without a database: audit EVERY attempt (success and
/// failure), lockout 5/15 unchanged, and a failed audit never leaves a
/// persisted counter with no evidence.
/// </summary>
public sealed class LoginServiceAtomicityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => $"hash:{password}";
        public bool Verify(string password, string hash) => hash == $"hash:{password}";
    }

    /// <summary>
    /// Ledger double that can force the RecordAudit write to fail (the F3
    /// compensation trigger); every other member delegates to the in-memory
    /// store.
    /// </summary>
    private sealed class FailingAuditLedger(InMemoryLedgerStore inner) : ILedgerStore
    {
        public bool FailNextAudit { get; set; }

        public LedgerContext Context => inner.Context;
        public LedgerState State => inner.State;
        public IReadOnlyList<Movement> Movements => inner.Movements;
        public IReadOnlyList<AuditEvent> Audits => inner.Audits;

        public void RegisterTrip(Trip trip) => inner.RegisterTrip(trip);
        public void RegisterSale(Sale sale) => inner.RegisterSale(sale);
        public void RegisterReturn(Return @return) => inner.RegisterReturn(@return);
        public void RegisterLoad(StockLoad load) => inner.RegisterLoad(load);
        public void RegisterVoid(SaleVoid @void) => inner.RegisterVoid(@void);
        public void RegisterAdjustment(Adjustment adjustment) => inner.RegisterAdjustment(adjustment);
        public void RegisterCount(PhysicalCount count) => inner.RegisterCount(count);
        public void RegisterClose(DailyClose @close) => inner.RegisterClose(@close);
        public void Bind() => inner.Bind();

        public Result<AppendOutcome> Append(AppendBatch batch) => inner.Append(batch);
        public Result<AppendReceipt> Append(IReadOnlyList<Movement> movements) => inner.Append(movements);

        public long RecordAudit(AuditEvent audit)
        {
            if (FailNextAudit)
            {
                FailNextAudit = false;
                throw new InvalidOperationException("simulated audit write failure (F3)");
            }

            return inner.RecordAudit(audit);
        }

        public Result<CheckoutReference?> FindCheckout(string idempotencyKey) => inner.FindCheckout(idempotencyKey);
        public Task<RebuildReceipt> RebuildAsync(CancellationToken ct = default) => inner.RebuildAsync(ct);
    }

    private sealed class World
    {
        public InMemoryUserAccountStore Accounts { get; } = new();
        public FailingAuditLedger Ledger { get; }
        public ActorContext Session { get; } = new();
        public LoginService Auth { get; }

        public World()
        {
            var memory = new InMemoryLedgerStore();
            Ledger = new FailingAuditLedger(memory);
            Auth = new LoginService(Accounts, new FakeHasher(), Ledger, () => T0, Session);
        }
    }

    private static User NewUser(int failedAttempts = 0, string username = "admin") => new()
    {
        Id = new UserId(Guid.NewGuid()),
        Username = username,
        PasswordHash = "hash:secret", // the FakeHasher's world: password "secret" verifies
        Role = Role.Admin,
        FailedAttempts = failedAttempts,
        LockedUntilUtc = null,
        DeactivatedUtc = null,
        CreatedBy = UserId.Anonymous,
        CreatedUtc = T0,
    };

    [Fact]
    public async Task EveryFailedAttempt_IncrementsCounter_AndWritesOneAudit_InMemoryParity()
    {
        var world = new World();
        User user = NewUser(failedAttempts: 2);
        await world.Accounts.AddAsync(user);

        for (int i = 0; i < 3; i++)
        {
            AuthResult failed = await world.Auth.AuthenticateAsync("admin", "wrong-" + i);
            Assert.Equal(AuthFailureReason.InvalidCredentials, failed.FailureReason);
        }

        // The 6th combined attempt (5 fails total) — correct password, but
        // locked: refused without extending the counter, one locked audit.
        AuthResult locked = await world.Auth.AuthenticateAsync("admin", "secret");
        Assert.False(locked.Success);
        Assert.Equal(AuthFailureReason.AccountLocked, locked.FailureReason);

        // Counter: 2 + 3 = 5, locked until T0+15 (lockout 5/15 unchanged).
        User stored = (await world.Accounts.FindByUsernameAsync("admin"))!;
        Assert.Equal(5, stored.FailedAttempts);
        Assert.Equal(T0.AddMinutes(15), stored.LockedUntilUtc);

        // Audits: EVERY attempt recorded (3 credential failures + 1 locked),
        // each attributed to the known account, on the ledger store.
        Assert.Equal(4, world.Ledger.Audits.Count);
        Assert.Equal(3, world.Ledger.Audits.Count(a => a.Message == AuthenticationAudit.InvalidCredentialsMessage));
        Assert.Equal(1, world.Ledger.Audits.Count(a => a.Message == AuthenticationAudit.AccountLockedMessage));
        Assert.All(world.Ledger.Audits, a => Assert.Equal(user.Id, a.Actor));
        Assert.All(world.Ledger.Audits, a => Assert.True(a.Sequence > 0, "fails/audits may never keep -1"));

        // The session actor is untouched by all these failures.
        Assert.Null(world.Session.CurrentUserId);
    }

    [Fact]
    public async Task SuccessfulLogin_ResetsCounter_AndWritesSuccessAudit_AndBindsSessionActor()
    {
        var world = new World();
        User user = NewUser(failedAttempts: 0);
        await world.Accounts.AddAsync(user);

        // One failure (counter 1), then success: counter resets, success audit.
        await world.Auth.AuthenticateAsync("admin", "wrong");
        AuthResult ok = await world.Auth.AuthenticateAsync("admin", "secret");
        Assert.True(ok.Success);
        Assert.Equal(Role.Admin, ok.Role);

        User stored = (await world.Accounts.FindByUsernameAsync("admin"))!;
        Assert.Equal(0, stored.FailedAttempts);
        Assert.Null(stored.LockedUntilUtc);

        Assert.Equal(2, world.Ledger.Audits.Count);
        Assert.Equal(AuthenticationAudit.LoginSuccessCategory, world.Ledger.Audits[1].Category);
        Assert.True(world.Ledger.Audits[1].Sequence > 0);

        // F8: success binds the session actor to the signed-in user.
        Assert.Equal(user.Id, world.Session.CurrentUserId);
    }

    [Fact]
    public async Task AuditWriteFailure_RevertsTheCounterWrite_AndPropagates()
    {
        var world = new World();
        User user = NewUser(failedAttempts: 3);
        await world.Accounts.AddAsync(user);

        // F3 compensation: the state write (counter 3 -> 4) lands, the audit
        // write fails, and LoginService reverts the user row to 3 before the
        // failure surfaces — counters and audits never diverge.
        world.Ledger.FailNextAudit = true;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Auth.AuthenticateAsync("admin", "wrong"));
        Assert.Equal("simulated audit write failure (F3)", ex.Message);

        User stored = (await world.Accounts.FindByUsernameAsync("admin"))!;
        Assert.Equal(3, stored.FailedAttempts);      // compensated back
        Assert.Null(stored.LockedUntilUtc);          // no lock can linger from a reverted attempt
        Assert.Empty(world.Ledger.Audits);           // no audit row for the failed write

        // The account still authenticates normally afterward.
        AuthResult ok = await world.Auth.AuthenticateAsync("admin", "secret");
        Assert.True(ok.Success);
        Assert.Equal(0, (await world.Accounts.FindByUsernameAsync("admin"))!.FailedAttempts);
        Assert.Single(world.Ledger.Audits); // exactly the success audit
    }

    [Fact]
    public async Task LockedAccountPath_DoesNotExtendTheLock_AndAuditsTheAttempt()
    {
        // Lockout no-extend while locked (AccountLockout): six attempts at the
        // lock boundary keep LockedUntilUtc at the FIRST lock's expiry and
        // every locked attempt is audited (5/15 semantics preserved).
        var world = new World();
        User user = NewUser(failedAttempts: 5, username: "locked-user");
        user = user with { LockedUntilUtc = T0.AddMinutes(15) };
        await world.Accounts.AddAsync(user);

        for (int i = 0; i < 3; i++)
        {
            AuthResult attempt = await world.Auth.AuthenticateAsync("locked-user", "wrong");
            Assert.Equal(AuthFailureReason.AccountLocked, attempt.FailureReason);
        }

        User stored = (await world.Accounts.FindByUsernameAsync("locked-user"))!;
        Assert.Equal(5, stored.FailedAttempts);
        Assert.Equal(T0.AddMinutes(15), stored.LockedUntilUtc); // never extended

        Assert.Equal(3, world.Ledger.Audits.Count);
        Assert.All(world.Ledger.Audits, a => Assert.Equal(AuthenticationAudit.AccountLockedMessage, a.Message));
    }
}
