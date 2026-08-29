using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Identity;

namespace BentaGo.Application.Contracts;

/// <summary>
/// The account persistence seam behind the user use cases. EF-backed by
/// BentaGo.Infrastructure.Persistence.UserAccountStore; the in-memory double
/// (BentaGo.Application.Services.InMemoryUserAccountStore) serves unit tests.
/// </summary>
public interface IUserAccountStore
{
    Task<User?> FindByIdAsync(UserId id, CancellationToken cancellationToken = default);
    Task<User?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);
    Task AddAsync(User user, CancellationToken cancellationToken = default);
    Task UpdateAsync(User user, CancellationToken cancellationToken = default);

    /// <summary>Count of currently active (not deactivated) Admin accounts.</summary>
    Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken = default);
}
