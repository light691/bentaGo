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

        // Guard against the CURRENT state before assigning sequences, then
        // assign in batch order. The guards see only already-committed state,
        // so a batch is atomic in effect: either every movement is legal
        // against the state BEFORE the batch, or nothing is appended.
        foreach (Movement movement in movements)
        {
            DomainError? violation = State.Accepts(movement with { Sequence = sequence });
            if (violation is not null)
            {
                return Result<AppendReceipt>.Fail(violation);
            }

            staged.Add(movement with { Sequence = sequence });
            sequence++;
        }

        foreach (Movement movement in staged)
        {
            State.BindDocuments(_trips, _sales, _returns, _loads, _voids);
            DomainError? error = State.ApplyCommitted(movement);
            if (error is not null)
            {
                // Cannot happen: ApplyCommitted re-runs the same guard the
                // batch already passed, against the same state.
                return Result<AppendReceipt>.Fail(error);
            }
        }

        State.BindDocuments(_trips, _sales, _returns, _loads, _voids);
        _movements.AddRange(staged);
        Context = NewContext();
        return Result<AppendReceipt>.Ok(new AppendReceipt(first, sequence - 1));
    }

    public void RecordAudit(AuditEvent audit) => _audits.Add(audit);

    private LedgerContext NewContext() =>
        new(State, _trips, _sales, _returns, _loads, _voids);
}
