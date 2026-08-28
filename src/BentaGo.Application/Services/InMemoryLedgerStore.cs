using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;

namespace BentaGo.Application.Services;

/// <summary>
/// The BG-002 in-memory ledger store: the test/scenario double that the
/// SQLite store will replace behind <see cref="ILedgerStore"/>. Single
/// writer, monotonic sequences in batch order, fold advanced incrementally.
/// </summary>
public sealed class InMemoryLedgerStore : ILedgerStore
{
    private readonly List<Movement> _movements = new();
    private readonly List<Trip> _trips = new();
    private readonly List<Sale> _sales = new();
    private readonly List<Return> _returns = new();
    private readonly List<StockLoad> _loads = new();
    private readonly List<SaleVoid> _voids = new();
    private readonly List<AuditEvent> _audits = new();

    public InMemoryLedgerStore()
    {
        State = new LedgerState();
        Context = NewContext();
    }

    public LedgerContext Context { get; private set; }

    public LedgerState State { get; private set; }

    public IReadOnlyList<Movement> Movements => _movements;

    public IReadOnlyList<AuditEvent> Audits => _audits;

    public void RegisterTrip(Trip trip) => _trips.Add(trip);

    public void RegisterSale(Sale sale) => _sales.Add(sale);

    public void RegisterReturn(Return @return) => _returns.Add(@return);

    public void RegisterLoad(StockLoad load) => _loads.Add(load);

    public void RegisterVoid(SaleVoid @void) => _voids.Add(@void);

    public void Bind()
    {
        State.BindDocuments(_trips, _sales, _returns, _loads, _voids);
        Context = NewContext();
    }

    public Result<AppendReceipt> Append(IReadOnlyList<Movement> movements)
    {
        if (movements.Count == 0)
        {
            return Result<AppendReceipt>.Fail(new DomainError("EMPTY_APPEND", "nothing to append"));
        }

        long first = State.LastSequence + 1;
        long sequence = first;
        var staged = new List<Movement>(movements.Count);

        // Validate-and-apply off to the side, sequentially: movements that
        // are individually legal against the pre-batch state can still
        // interact (two lines consuming the same stock cell), so each one is
        // validated against the state the batch has already produced. On any
        // failure the clone is discarded and nothing is committed.
        LedgerState next = State.Clone();
        foreach (Movement movement in movements)
        {
            Movement sequenced = movement with { Sequence = sequence };
            DomainError? violation = next.ApplyCommitted(sequenced);
            if (violation is not null)
            {
                return Result<AppendReceipt>.Fail(violation);
            }

            staged.Add(sequenced);
            sequence++;
        }

        // A stream that passes the per-movement checks but drifts from its
        // documents (duplicated receipt, mutated amount) must never commit.
        try
        {
            next.VerifyCrossChecks(_sales);
        }
        catch (LedgerIntegrityException ex)
        {
            return Result<AppendReceipt>.Fail(new DomainError("INTEGRITY", ex.Message));
        }

        State = next;
        _movements.AddRange(staged);
        Context = NewContext();
        return Result<AppendReceipt>.Ok(new AppendReceipt(first, sequence - 1));
    }

    public void RecordAudit(AuditEvent audit) => _audits.Add(audit);

    private LedgerContext NewContext() =>
        new(State, _trips, _sales, _returns, _loads, _voids);
}
