using BentaGo.Infrastructure.Security;
using Xunit;

namespace BentaGo.Tests.Domain;

/// <summary>
/// BCrypt password hasher round-trip (BG-005a decision: BCrypt.Net-Next,
/// work factor 12). Verifies correct/wrong passwords and the salted nonce
/// property — the same hasher the seeder and the authentication service use.
/// </summary>
public sealed class AuthPasswordHasherTests
{
    private readonly BCryptPasswordHasher _hasher = new();

    [Fact]
    public void CorrectPassword_Verifies()
    {
        string hash = _hasher.Hash("admin");
        Assert.True(_hasher.Verify("admin", hash));
    }

    [Fact]
    public void WrongPassword_FailsVerification()
    {
        string hash = _hasher.Hash("admin");
        Assert.False(_hasher.Verify("wrong-password", hash));
        Assert.False(_hasher.Verify("", hash));
    }

    [Fact]
    public void Hash_IsNotThePlaintext()
    {
        string hash = _hasher.Hash("admin");
        Assert.NotEqual("admin", hash);
        Assert.StartsWith("$2", hash);
    }

    [Fact]
    public void TwoHashesOfSamePassword_DifferDueToSalt()
    {
        Assert.NotEqual(_hasher.Hash("admin"), _hasher.Hash("admin"));
    }
}
