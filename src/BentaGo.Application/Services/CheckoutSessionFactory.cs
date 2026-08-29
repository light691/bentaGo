using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Ledger;

namespace BentaGo.Application.Services;

/// <summary>
/// Builds a checkout session for the CURRENT session actor (D3): the actor →
/// user → PeddlerLink → open trip chain is derived here, never supplied by a
/// caller. AUTH_ACTOR_NOT_SIGNED_IN / AUTH_ACTOR_UNKNOWN /
/// AUTH_ACTOR_DEACTIVATED / NO_PEDDLER_LINK / NO_OPEN_TRIP.
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

        PeddlerId? link = actor.Value.PeddlerLink;
        if (link is null)
        {
            return Result<ICheckoutSession>.Fail(new DomainError(
                "NO_PEDDLER_LINK", "this account is not linked to a peddler"));
        }

        store.Bind();
        Trip? openTrip = null;
        foreach (Trip trip in store.Context.Trips)
        {
            if (trip.Peddler == link.Value && store.State.Trip(trip.Id).Status == TripStatus.Open)
            {
                openTrip = trip;
                break;
            }
        }

        if (openTrip is null)
        {
            return Result<ICheckoutSession>.Fail(new DomainError(
                "NO_OPEN_TRIP", "there is no open trip for this peddler"));
        }

        return Result<ICheckoutSession>.Ok(new CheckoutSession(
            store, products, checkout, numbers, actors, actor.Value, clock, openTrip));
    }
}
