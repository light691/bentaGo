using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain.Identity;

namespace BentaGo.Application.Services;

/// <summary>
/// Builds a checkout session for the CURRENT session actor (D3): the actor →
/// user chain is resolved here, never supplied by a caller.
/// AUTH_ACTOR_NOT_SIGNED_IN / AUTH_ACTOR_UNKNOWN / AUTH_ACTOR_DEACTIVATED.
/// The trading day comes from the clock; the sale location is the single
/// warehouse.
/// </summary>
public sealed class CheckoutSessionFactory(
    ILedgerStore store,
    IProductStore products,
    ICheckoutService checkout,
    ISaleNumberSource numbers,
    IActorContext actors,
    IUserAccountStore users,
    Func<DateTimeOffset> clock)
    : ICheckoutSessionFactory
{
    public async Task<Result<ICheckoutSession>> CreateSessionAsync(CancellationToken ct = default)
    {
        Result<User> actor = await ActorGuard.ResolveAsync(actors, users, ct);
        if (actor.IsFailure)
        {
            return Result<ICheckoutSession>.Fail(actor.Errors);
        }

        return Result<ICheckoutSession>.Ok(new CheckoutSession(
            store, products, checkout, numbers, actors, actor.Value, clock));
    }
}
