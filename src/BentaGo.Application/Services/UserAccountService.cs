using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Identity;

namespace BentaGo.Application.Services;

/// <summary>
/// User account use cases (BG-005a): CreateUser, SetUserActive/Deactivate,
/// ChangePassword. Each resolves the acting user, gates the capability through
/// the <see cref="RoleExtensions"/> predicates, and returns Result&lt;User&gt;
/// — a failure means nothing was persisted.
/// </summary>
public sealed class UserAccountService(IUserAccountStore store, IPasswordHasher hasher) : IUserAccountService
{
    public async Task<Result<User>> CreateUserAsync(CreateUserCommand cmd, CancellationToken cancellationToken = default)
    {
        User? actor = await store.FindByIdAsync(cmd.By, cancellationToken);
        if (actor is null)
        {
            return UserError("AUTH_ACTOR_UNKNOWN", "the acting user does not exist");
        }

        if (actor.DeactivatedUtc is not null)
        {
            return UserError("AUTH_ACTOR_DEACTIVATED", "the acting user is deactivated");
        }

        if (!actor.Role.CanManageUsers())
        {
            return UserError("FORBIDDEN", "only an admin may create users");
        }

        string username = cmd.Username?.Trim() ?? "";
        if (username.Length == 0)
        {
            return UserError("INVALID_USERNAME", "username is required");
        }

        if (string.IsNullOrEmpty(cmd.Password))
        {
            return UserError("INVALID_PASSWORD", "password is required");
        }

        if (await store.FindByUsernameAsync(username, cancellationToken) is not null)
        {
            return UserError("DUP_USERNAME", $"username '{username}' is already taken");
        }

        var user = new User
        {
            Id = cmd.Id,
            Username = username,
            PasswordHash = hasher.Hash(cmd.Password),
            Role = cmd.Role,
            PeddlerLink = cmd.PeddlerLink,
            FailedAttempts = 0,
            LockedUntilUtc = null,
            DeactivatedUtc = null,
            CreatedBy = cmd.By,
            CreatedUtc = cmd.NowUtc,
        };

        await store.AddAsync(user, cancellationToken);
        return Result<User>.Ok(user);
    }

    public async Task<Result<User>> SetUserActiveAsync(SetUserActiveCommand cmd, CancellationToken cancellationToken = default)
    {
        User? actor = await store.FindByIdAsync(cmd.By, cancellationToken);
        if (actor is null)
        {
            return UserError("AUTH_ACTOR_UNKNOWN", "the acting user does not exist");
        }

        if (actor.DeactivatedUtc is not null)
        {
            return UserError("AUTH_ACTOR_DEACTIVATED", "the acting user is deactivated");
        }

        if (!actor.Role.CanManageUsers())
        {
            return UserError("FORBIDDEN", "only an admin may activate or deactivate users");
        }

        User? target = await store.FindByIdAsync(cmd.Id, cancellationToken);
        if (target is null)
        {
            return UserError("USER_UNKNOWN", "the target user does not exist");
        }

        if (cmd.Active)
        {
            // Reactivation clears the auth state with the act itself: an admin
            // brought back is unlocked and starts with a clean counter.
            User reactivated = target with { DeactivatedUtc = null, FailedAttempts = 0, LockedUntilUtc = null };
            await store.UpdateAsync(reactivated, cancellationToken);
            return Result<User>.Ok(reactivated);
        }

        // Deactivation guards (F6): an admin cannot deactivate their own
        // account, and the LAST active admin cannot be deactivated (the
        // seeder only runs on first run, so a deactivated admin stays
        // deactivated by design — no auto re-seed).
        if (cmd.Id == cmd.By)
        {
            return UserError("AUTH_CANNOT_DEACTIVATE_SELF", "an admin cannot deactivate their own account");
        }

        if (target.Role == Role.Admin && target.DeactivatedUtc is null)
        {
            int activeAdmins = await store.CountActiveAdminsAsync(cancellationToken);
            if (activeAdmins <= 1)
            {
                return UserError("AUTH_NEEDS_ADMIN", "cannot deactivate the last active admin");
            }
        }

        User updated = target with { DeactivatedUtc = cmd.NowUtc };
        await store.UpdateAsync(updated, cancellationToken);
        return Result<User>.Ok(updated);
    }

    public async Task<Result<User>> ChangePasswordAsync(ChangePasswordCommand cmd, CancellationToken cancellationToken = default)
    {
        User? actor = await store.FindByIdAsync(cmd.By, cancellationToken);
        if (actor is null)
        {
            return UserError("AUTH_ACTOR_UNKNOWN", "the acting user does not exist");
        }

        if (actor.DeactivatedUtc is not null)
        {
            return UserError("AUTH_ACTOR_DEACTIVATED", "the acting user is deactivated");
        }

        bool admin = actor.Role.CanManageUsers();
        if (cmd.Id != cmd.By && !admin)
        {
            return UserError("FORBIDDEN", "only an admin may change another user's password");
        }

        if (string.IsNullOrEmpty(cmd.NewPassword))
        {
            return UserError("INVALID_PASSWORD", "password is required");
        }

        User? target = await store.FindByIdAsync(cmd.Id, cancellationToken);
        if (target is null)
        {
            return UserError("USER_UNKNOWN", "the target user does not exist");
        }

        User updated = target with { PasswordHash = hasher.Hash(cmd.NewPassword) };
        await store.UpdateAsync(updated, cancellationToken);
        return Result<User>.Ok(updated);
    }

    private static Result<User> UserError(string code, string message) =>
        Result<User>.Fail(new DomainError(code, message));
}