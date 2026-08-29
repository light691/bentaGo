using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Display;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Trips;
using BentaGo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BentaGo.App.Data;

/// <summary>
/// One peddler's admin-overview line (BG-005d). Display values are
/// preformatted strings (the AppSnapshot convention — presentation text, no
/// business math in the shell); the trip phase and today's fold live in the
/// domain, the shell only renders them. The active trip is the most recent
/// NON-terminal trip (TripLifecycle terminal = Closed/Cancelled); today's
/// sales/transactions are folded from the committed sale documents for the
/// peddler's own trips (voided sales excluded, Manila day boundary).
/// </summary>
public sealed record PeddlerOverviewData(
    PeddlerId Id,
    string DisplayName,
    string TripsCountText,
    string ActiveTripText,
    string TodaySalesText,
    string TodayTransactionsText,
    string OnHandSummaryText,
    IReadOnlyList<OnHandDisplayRow> OnHandRows);

/// <summary>One on-hand line (sku + preformatted units) for the selected peddler.</summary>
public sealed record OnHandDisplayRow(string Sku, string UnitsText);

/// <summary>
/// Read-only admin glue (BG-005d-ui): composes the registered persistence and
/// ledger seams into the read-mostly models the three admin screens bind.
/// EVERY write still goes through the real use-case services
/// (<see cref="IUserAccountService"/>, <see cref="IProductImportService"/>)
/// which enforce the session actor (By == session user, F8) and the role
/// gates; this type only READs.
///
/// Seam notes (see the BG-005d-ui report): Users/Peddlers enumeration has NO
/// store surface (IUserAccountStore exposes single-row lookups only), so the
/// list reads go through the EF context directly — an App-layer read that
/// keeps every existing contract untouched. Trip/on-hand reads go through the
/// peddler-scoped <see cref="ITripReadService"/>; the today fold and trip
/// phase use <see cref="ILedgerStore"/> the same way TripReadService does
/// (Bind, then read Context/State).
/// </summary>
public sealed class AdminReadService
{
    private readonly IDbContextFactory<BentaGoDbContext> _factory;
    private readonly IActorContext _actorContext;
    private readonly IUserAccountStore _userStore;
    private readonly ITripReadService _trips;
    private readonly ILedgerStore _ledger;
    private readonly Func<DateTimeOffset> _clock;

    public AdminReadService(
        IDbContextFactory<BentaGoDbContext> factory,
        IActorContext actorContext,
        IUserAccountStore userStore,
        ITripReadService trips,
        ILedgerStore ledger,
        Func<DateTimeOffset> clock)
    {
        _factory = factory;
        _actorContext = actorContext;
        _userStore = userStore;
        _trips = trips;
        _ledger = ledger;
        _clock = clock;
    }

    /// <summary>The signed-in actor's Users row (null while signed out).</summary>
    public async Task<User?> GetSessionUserAsync(CancellationToken cancellationToken = default)
    {
        UserId? id = _actorContext.CurrentUserId;
        return id is null ? null : await _userStore.FindByIdAsync(id.Value, cancellationToken);
    }

    /// <summary>Every account, ordered by username.</summary>
    public async Task<IReadOnlyList<User>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.Users
            .AsNoTracking()
            .OrderBy(u => u.Username)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Peddler rows, ordered by display name.</summary>
    public async Task<IReadOnlyList<Peddler>> ListPeddlersAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.Peddlers
            .AsNoTracking()
            .OrderBy(p => p.DisplayName)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// One overview line per peddler. Read-mostly and PER-PEDDLER by design
    /// (the conservative BG-005d choice: the peddler-scoped services stay
    /// authoritative; a cross-peddler read API is a documented follow-up, not
    /// this slice's scope).
    /// </summary>
    public async Task<IReadOnlyList<PeddlerOverviewData>> GetPeddlerOverviewsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Peddler> peddlers = await ListPeddlersAsync(cancellationToken);
        var rows = new List<PeddlerOverviewData>(peddlers.Count);
        foreach (Peddler peddler in peddlers)
        {
            rows.Add(BuildOverview(peddler));
        }

        return rows;
    }

    private PeddlerOverviewData BuildOverview(Peddler peddler)
    {
        // Peddler-scoped reads through the registered service surface.
        IReadOnlyList<Trip> trips = _trips.ListTrips(peddler.Id).IsSuccess
            ? _trips.ListTrips(peddler.Id).Value
            : Array.Empty<Trip>();

        // The ledger fold refreshes on read (the TripReadService call shape):
        // Context/State self-sync, so the folds below see the committed stream.
        _ledger.Bind();

        var tripIds = new HashSet<TripId>(trips.Select(t => t.Id));
        DateOnly today = DateOnly.FromDateTime(ManilaDisplay.UtcAsManila(_clock()).DateTime);
        Money todaySales = Money.Zero;
        int todayTransactions = 0;
        foreach (Sale sale in _ledger.Context.Sales)
        {
            if (sale.Peddler != peddler.Id || !tripIds.Contains(sale.Trip))
            {
                continue;
            }

            if (_ledger.State.Sale(sale.Id).Status == SaleStatus.Voided)
            {
                continue;
            }

            if (DateOnly.FromDateTime(ManilaDisplay.UtcAsManila(sale.CreatedUtc).DateTime) != today)
            {
                continue;
            }

            todaySales = todaySales.Plus(sale.Total);
            todayTransactions++;
        }

        Trip? active = trips
            .Where(trip => !TripLifecycle.IsTerminal(PhaseOf(trip)))
            .OrderByDescending(trip => trip.CreatedUtc)
            .FirstOrDefault();

        string activeTripText = active is null
            ? "None — no open trip"
            : $"{PhaseLabel(PhaseOf(active))} · {ShortLocation(active.Location)}";

        IReadOnlyList<OnHandRow> onHand = _trips.OnHandAll(peddler.Id).IsSuccess
            ? _trips.OnHandAll(peddler.Id).Value
            : Array.Empty<OnHandRow>();

        int totalUnits = 0;
        var onHandRows = new List<OnHandDisplayRow>(onHand.Count);
        foreach (OnHandRow row in onHand)
        {
            totalUnits += row.Units.Units;
            onHandRows.Add(new OnHandDisplayRow(row.Sku ?? "-", row.Units.Units.ToString()));
        }

        return new PeddlerOverviewData(
            peddler.Id,
            peddler.DisplayName,
            trips.Count == 1 ? "1 trip" : $"{trips.Count} trips",
            activeTripText,
            MoneyDisplay.Peso(todaySales),
            todayTransactions == 1 ? "1 sale today" : $"{todayTransactions} sales today",
            onHandRows.Count == 1 ? "1 item on hand" : $"{onHandRows.Count} items on hand · {totalUnits} units",
            onHandRows);
    }

    private TripPhase PhaseOf(Trip trip)
    {
        bool hasSales = _ledger.Context.Sales.Any(sale => sale.Trip == trip.Id);
        return TripLifecycle.PhaseOf(_ledger.State.Trip(trip.Id), hasSales);
    }

    private static string PhaseLabel(TripPhase phase) => phase switch
    {
        TripPhase.Draft => "Draft",
        TripPhase.Loading => "Loading",
        TripPhase.Active => "Active",
        TripPhase.Reconciling => "Reconciling",
        TripPhase.Closed => "Closed",
        TripPhase.Cancelled => "Cancelled",
        _ => "Unknown",
    };

    private static string ShortLocation(LocationId location) =>
        "LOC-" + location.Value.ToString("N")[..8].ToUpperInvariant();
}