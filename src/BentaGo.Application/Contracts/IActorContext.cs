using BentaGo.Core.Domain;

namespace BentaGo.Application.Contracts;

/// <summary>
/// The signed-in actor for the current session (BG-005a decision). Singleton
/// in the host, settable after a successful sign-in; null before any sign-in
/// or after sign-out. The WPF shell sets it (BG-005d wiring) so use cases and
/// audit writers can attribute actions.
/// </summary>
public interface IActorContext
{
    UserId? CurrentUserId { get; }
    void Set(UserId userId);
}