using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;

namespace BentaGo.Application.Services;

/// <summary>
/// Session actor context (BG-005a): holds the signed-in user for the current
/// session. Singleton in the host; the WPF shell sets it after sign-in.
/// </summary>
public sealed class ActorContext : IActorContext
{
    public UserId? CurrentUserId { get; private set; }

    public void Set(UserId userId) => CurrentUserId = userId;
}