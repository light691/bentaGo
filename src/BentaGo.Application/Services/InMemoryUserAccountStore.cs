using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Identity;

namespace BentaGo.Application.Services;

/// <summary>
/// In-memory user account store double for unit tests (the
/// <see cref="InMemoryLedgerStore"/> precedent): the user use cases run
/// store-agnostically.
/// </summary>
public sealed class InMemoryUserAccountStore : IUserAccountStore
{
    private readonly List<User> _users = new();

    public IReadOnlyList<User> Users => _users;

    public Task<User?> FindByIdAsync(UserId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_users.FirstOrDefault(u => u.Id == id));

    public Task<User?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
        Task.FromResult(_users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.Ordinal)));

    public Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        _users.Add(user);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(User user, CancellationToken cancellationToken = default)
    {
        int index = _users.FindIndex(u => u.Id == user.Id);
        if (index >= 0)
        {
            _users[index] = user;
        }

        return Task.CompletedTask;
    }

    public Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_users.Count(u => u.Role == Role.Admin && u.DeactivatedUtc is null));
}