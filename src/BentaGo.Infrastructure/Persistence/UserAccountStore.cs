using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// EF-backed user account store (BG-005a): the Users/Peddlers schema through
/// <see cref="BentaGoDbContext"/>. Read by the authentication service and the
/// user use cases; writes (auth-state updates, creation) go through
/// SaveChanges in the caller's small unit of work.
/// </summary>
public sealed class UserAccountStore(IDbContextFactory<BentaGoDbContext> factory) : IUserAccountStore
{
    public async Task<User?> FindByIdAsync(UserId id, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await context.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
    }

    public async Task<User?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await context.Users.SingleOrDefaultAsync(
            u => u.Username == username, cancellationToken);
    }

    public async Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(User user, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        context.Users.Update(user);
        await context.SaveChangesAsync(cancellationToken);
    }
}