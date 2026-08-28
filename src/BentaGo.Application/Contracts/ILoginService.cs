namespace BentaGo.Application.Contracts;

/// <summary>
/// Authentication contract for the placeholder login shell.
/// BG-005 replaces this with the real user and role system.
/// </summary>
public interface ILoginService
{
    Task<bool> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default);
}
