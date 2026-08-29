using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Identity;

namespace BentaGo.Application.Services;

/// <summary>
/// Session-actor resolution (BG-006 D3 / BG-005d F8 pattern). The SESSION
/// path derives the acting user from <see cref="IActorContext"/> and never
/// trusts a caller-supplied By; the same guard set as UserAccountService:
/// AUTH_ACTOR_NOT_SIGNED_IN / AUTH_ACTOR_UNKNOWN / AUTH_ACTOR_DEACTIVATED /
/// AUTH_ACTOR_MISMATCH (context changed between create and submit).
/// </summary>
public static class ActorGuard
{
    public static async Task<Result<User>> ResolveAsync(
        IActorContext actors, IUserAccountStore users, CancellationToken ct = default)
    {
        UserId? current = actors.CurrentUserId;
        if (current is null)
        {
            return Result<User>.Fail(new DomainError("AUTH_ACTOR_NOT_SIGNED_IN", "no user is signed in for this session"));
        }

        User? user = await users.FindByIdAsync(current.Value, ct);
        if (user is null)
        {
            return Result<User>.Fail(new DomainError("AUTH_ACTOR_UNKNOWN", "the acting user does not exist"));
        }

        if (user.DeactivatedUtc is not null)
        {
            return Result<User>.Fail(new DomainError("AUTH_ACTOR_DEACTIVATED", "the acting user is deactivated"));
        }

        return Result<User>.Ok(user);
    }

    /// <summary>Re-verifies the session actor before a submit: the context
    /// user must still be set and must equal the session's own user.</summary>
    public static Result<bool> Verify(IActorContext actors, UserId sessionUser)
    {
        UserId? current = actors.CurrentUserId;
        if (current is null)
        {
            return Result<bool>.Fail(new DomainError("AUTH_ACTOR_NOT_SIGNED_IN", "no user is signed in for this session"));
        }

        if (current.Value != sessionUser)
        {
            return Result<bool>.Fail(new DomainError(
                "AUTH_ACTOR_MISMATCH", "the session actor changed between session creation and submit"));
        }

        return Result<bool>.Ok(true);
    }
}
