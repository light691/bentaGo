using BentaGo.Application.Contracts;

namespace BentaGo.Infrastructure.Security;

/// <summary>
/// BCrypt-based password hasher implementing the Application hashing seam.
/// Work factor 12 strikes a balance between cost and resistance for
/// interactive desktop use (BG-005a decision; BCrypt.Net-Next).
/// </summary>
public sealed class BCryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 12;

    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    public bool Verify(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);
}