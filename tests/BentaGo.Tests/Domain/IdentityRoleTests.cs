using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using Xunit;

namespace BentaGo.Tests.Domain;

/// <summary>
/// BG-005a identity slice: role capability truth table (Admin all,
/// Supervisor no user management / configuration / high-risk adjustments,
/// Peddler operational only), the User/Peddler entity shapes, the anonymous
/// actor sentinel, and the hardened AuditEvent fields.
/// </summary>
public sealed class IdentityRoleTests
{
    [Theory]
    [InlineData(Role.Admin, true)]
    [InlineData(Role.Supervisor, false)]
    [InlineData(Role.Peddler, false)]
    public void CanManageUsers_TruthTable(Role role, bool expected) =>
        Assert.Equal(expected, role.CanManageUsers());

    [Theory]
    [InlineData(Role.Admin, true)]
    [InlineData(Role.Supervisor, false)]
    [InlineData(Role.Peddler, false)]
    public void CanManageConfiguration_TruthTable(Role role, bool expected) =>
        Assert.Equal(expected, role.CanManageConfiguration());

    [Theory]
    [InlineData(Role.Admin, true)]
    [InlineData(Role.Supervisor, false)]
    [InlineData(Role.Peddler, false)]
    public void CanMakeHighRiskAdjustments_TruthTable(Role role, bool expected) =>
        Assert.Equal(expected, role.CanMakeHighRiskAdjustments());

    [Fact]
    public void AnonymousUserId_IsGuidEmptySentinel() =>
        Assert.Equal(Guid.Empty, UserId.Anonymous.Value);

    [Fact]
    public void User_DefaultsToCleanAuthState_AndFollowsCreatedByConvention()
    {
        var actor = new UserId(Guid.NewGuid());
        var when = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

        var user = new User
        {
            Id = new UserId(Guid.NewGuid()),
            Username = "alice",
            PasswordHash = "hash",
            Role = Role.Peddler,
            PeddlerLink = new PeddlerId(Guid.NewGuid()),
            CreatedBy = actor,
            CreatedUtc = when,
        };

        Assert.Equal(0, user.FailedAttempts);
        Assert.Null(user.LockedUntilUtc);
        Assert.Null(user.DeactivatedUtc);
        Assert.Equal(actor, user.CreatedBy);
        Assert.Equal(when, user.CreatedUtc);
    }

    [Fact]
    public void Peddler_TracksActiveFlag()
    {
        var peddler = new Peddler { Id = new PeddlerId(Guid.NewGuid()), DisplayName = "Juan", Active = true };
        Assert.True(peddler.Active);
        Assert.Equal("Juan", peddler.DisplayName);
    }

    [Fact]
    public void AuditEvent_TerminalAndReason_DefaultNull_AndRoundTrip()
    {
        var actor = new UserId(Guid.NewGuid());
        var when = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

        var plain = new AuditEvent(-1, actor, when, "AUTH.LOGIN_SUCCESS", "sign-in succeeded", Ref: null);
        Assert.Null(plain.Terminal);
        Assert.Null(plain.Reason);

        var rich = plain with { Terminal = "POS-01", Reason = "invalid credentials" };
        Assert.Equal("POS-01", rich.Terminal);
        Assert.Equal("invalid credentials", rich.Reason);
        Assert.Equal("AUTH.LOGIN_SUCCESS", rich.Category);
        Assert.Equal(actor, rich.Actor);
    }
}
