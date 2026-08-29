using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;

namespace BentaGo.Application.Services;

/// <summary>
/// Session actor context (BG-005a decision): holds the signed-in user for the
/// current session. Singleton in the host. Lifetime semantics (BG-005d): set
/// on login success only (LoginService), starts null on every host build
/// (fresh singleton = "cleared on host start"), Clear is the future sign-out
/// path (BG-005d-ui). A failed login never changes the context.
/// </summary>
public sealed class ActorContext : IActorContext
{
    public UserId? CurrentUserId { get; private set; }

    public void Set(UserId userId) => CurrentUserId = userId;

    public void Clear() => CurrentUserId = null;
}
