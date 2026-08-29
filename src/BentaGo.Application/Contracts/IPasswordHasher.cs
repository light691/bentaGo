namespace BentaGo.Application.Contracts;

/// <summary>
/// Password hashing seam (BCrypt work factor 12, BG-005a decision; the
/// concrete BCrypt implementation lives in BentaGo.Infrastructure.Security).
/// Placed in Application so store-agnostic use cases can hash without knowing
/// the algorithm.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}
