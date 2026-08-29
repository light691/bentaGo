using BentaGo.Core.Domain;

namespace BentaGo.Application.Contracts;

/// <summary>
/// The signed-in actor for the current session (BG-005a decision; the actor
/// supply seam for account use cases — BG-005d). Singleton in the host.
///
/// Lifetime semantics (BG-005d, decision in bg-005d report): the ACTOR is the
/// LOGGED-IN user. <see cref="IAuthenticationService"/> sets it ONLY on login
/// success; a failed attempt never touches it (the previous session actor, if
/// any, stays — a failed re-login does not log anyone out). The context
/// starts null (a fresh host build IS the cleared state, so "clear on host
/// start" needs no explicit call) and <see cref="Clear"/> exists for the
/// logout path. No logout exists yet: the WPF shell performs no sign-out
/// flow, so Clear is currently called nowhere in production — the logout gap
/// is owned by BG-005d-ui (the admin shell must call Clear on sign-out). A
/// deactivated user is NOT cleared at deactivation time: the session reflects
/// the signed-in actor, and the account use cases re-read the Users row on
/// every call, so a deactivated actor is rejected by the capability gates
/// (AUTH_ACTOR_DEACTIVATED) regardless of the context.
///
/// Account use cases (UserAccountService) derive the actor from here and
/// reject any command whose By does not equal CurrentUserId
/// (AUTH_ACTOR_MISMATCH), so a caller can never spoof another user's id.
/// </summary>
public interface IActorContext
{
    /// <summary>The signed-in account, or null before any sign-in / after sign-out.</summary>
    UserId? CurrentUserId { get; }

    /// <summary>Sets the signed-in actor (login success path only by convention).</summary>
    void Set(UserId userId);

    /// <summary>Clears the signed-in actor (sign-out). Host start begins cleared.</summary>
    void Clear();
}
