using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Identity;
using BentaGo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BentaGo.App.Data;

/// <summary>
/// Read-only admin glue (BG-005d-ui): composes the registered persistence
/// seams into the read-mostly models the admin screens bind. EVERY write
/// still goes through the real use-case services
/// (<see cref="IUserAccountService"/>, <see cref="IProductImportService"/>)
/// which enforce the session actor (By == session user, F8) and the role
/// gates; this type only READs.
///
/// Seam note (see the BG-005d-ui report): Users enumeration has NO store
/// surface (IUserAccountStore exposes single-row lookups only), so the list
/// read goes through the EF context directly — an App-layer read that keeps
/// every existing contract untouched.
/// </summary>
public sealed class AdminReadService
{
    private readonly IDbContextFactory<BentaGoDbContext> _factory;
    private readonly IActorContext _actorContext;
    private readonly IUserAccountStore _userStore;

    public AdminReadService(
        IDbContextFactory<BentaGoDbContext> factory,
        IActorContext actorContext,
        IUserAccountStore userStore)
    {
        _factory = factory;
        _actorContext = actorContext;
        _userStore = userStore;
    }

    /// <summary>The signed-in actor's Users row (null while signed out).</summary>
    public async Task<User?> GetSessionUserAsync(CancellationToken cancellationToken = default)
    {
        UserId? id = _actorContext.CurrentUserId;
        return id is null ? null : await _userStore.FindByIdAsync(id.Value, cancellationToken);
    }

    /// <summary>Every account, ordered by username.</summary>
    public async Task<IReadOnlyList<User>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.Users
            .AsNoTracking()
            .OrderBy(u => u.Username)
            .ToListAsync(cancellationToken);
    }
}
