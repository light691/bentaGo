using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;

namespace BentaGo.Application.Services;

/// <summary>
/// The BG-002 in-memory ledger store: the test/scenario double that the
/// SQLite store replaces behind <see cref="ILedgerStore"/>. Single writer,
/// monotonic sequences in batch order, fold advanced incrementally.
///
/// Document registry model (BG-003): <see cref="RegisterSale"/> etc. stage
/// into an open-attempt set, <see cref="Bind"/> snapshots committed + staged
/// into the fold, and <see cref="Append(AppendBatch)"/> either commits the
/// staged documents (success) or clears them (failure) — a failed attempt's
/// documents never leak into a later bound snapshot (D4/R6). Trips are
/// movement-less and commit immediately (D5 asymmetry).
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
    private readonly Dictionary<string, string> _idempotency = new();
    private long _auditSequence;

    // Open-attempt staging: documents registered since the last Bind/Append.
    private readonly List<Sale> _stagedSales = new();
    private readonly List<Return> _stagedReturns = new();
    private readonly List<StockLoad> _stagedLoads = new();
    private readonly List<SaleVoid> _stagedVoids = new();
    private readonly List<Adjustment> _stagedAdjustments = new();
    private readonly List<PhysicalCount> _stagedCounts = new();
    private readonly List<DailyClose> _stagedCloses = new();

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

    public void RegisterSale(Sale sale) => _stagedSales.Add(sale);

    public void RegisterReturn(Return @return) => _stagedReturns.Add(@return);

    public void RegisterLoad(StockLoad load) => _stagedLoads.Add(load);

    public void RegisterVoid(SaleVoid @void) => _stagedVoids.Add(@void);

    public void RegisterAdjustment(Adjustment adjustment) => _stagedAdjustments.Add(adjustment);

    public void RegisterCount(PhysicalCount count) => _stagedCounts.Add(count);

    public void RegisterClose(DailyClose @close) => _stagedCloses.Add(@close);

    public void Bind()
    {
        IReadOnlyList<Sale> sales = Bound(_sales, _stagedSales);
        IReadOnlyList<Return> returns = Bound(_returns, _stagedReturns);
        IReadOnlyList<StockLoad> loads = Bound(_loads, _stagedLoads);
        IReadOnlyList<SaleVoid> voids = Bound(_voids, _stagedVoids);

        State.BindDocuments(_trips, sales, returns, loads, voids);
        Context = new LedgerContext(State, _trips.ToList(), sales, returns, loads, voids);
    }

    public Result<AppendOutcome> Append(AppendBatch batch)
    {
        // Duplicate idempotency key: roll back (commit nothing this call) and
        // replay the first commit's payload — never an error (D1).
        if (batch.Idempotency is { } mapping && _idempotency.TryGetValue(mapping.Key, out string? storedPayload))
        {
            return Result<AppendOutcome>.Ok(new AppendOutcome(null, Replayed: true, ReplayedPayload: storedPayload));
        }

        if (batch.Movements.Count == 0)
        {
            return Result<AppendOutcome>.Fail(new DomainError("EMPTY_APPEND", "nothing to append"));
        }

        long first = State.LastSequence + 1;
        long sequence = first;
        var staged = new List<Movement>(batch.Movements.Count);

        // Validate-and-apply off to the side, sequentially: movements that
        // are individually legal against the pre-batch state can still
        // interact (two lines consuming the same stock cell), so each one is
        // validated against the state the batch has already produced. On any
        // failure the clone is discarded, the open attempt is cleared, and
        // nothing is committed.
        LedgerState next = State.Clone();
        foreach (Movement movement in batch.Movements)
        {
            Movement sequenced = movement with { Sequence = sequence };
            DomainError? violation = next.ApplyCommitted(sequenced);
            if (violation is not null)
            {
                ClearOpenAttempt();
                return Result<AppendOutcome>.Fail(violation);
            }

            staged.Add(sequenced);
            sequence++;
        }

        // A stream that passes the per-movement checks but drifts from its
        // documents (duplicated receipt, mutated amount) must never commit.
        try
        {
            next.VerifyCrossChecks(_sales.Concat(_stagedSales).ToList());
        }
        catch (LedgerIntegrityException ex)
        {
            ClearOpenAttempt();
            return Result<AppendOutcome>.Fail(new DomainError("INTEGRITY", ex.Message));
        }

        // Commit: the fold, the movements, the audits, the idempotency
        // mapping, and the bound documents — all-or-nothing.
        State = next;
        _movements.AddRange(staged);
        if (batch.Audits is { Count: > 0 } audits)
        {
            _audits.AddRange(audits);
        }

        if (batch.Idempotency is { } idem)
        {
            _idempotency[idem.Key] = idem.Payload;
        }

        CommitStaged();
        Context = NewContext();
        return Result<AppendOutcome>.Ok(new AppendOutcome(new AppendReceipt(first, sequence - 1), Replayed: false, ReplayedPayload: null));
    }

    public Result<AppendReceipt> Append(IReadOnlyList<Movement> movements) =>
        Append(new AppendBatch(movements)).Map(outcome => outcome.Receipt!);

    /// <summary>
    /// In-memory RecordAudit keeps incrementing: each call assigns the next
    /// identity (matching the DB identity semantics; the -1 placeholder is
    /// replaced) and returns it. Golden scenario rendering is unaffected —
    /// audits render by message only.
    /// </summary>
    public long RecordAudit(AuditEvent audit)
    {
        _auditSequence++;
        AuditEvent stored = audit with { Sequence = _auditSequence };
        _audits.Add(stored);
        return stored.Sequence;
    }

    public Result<CheckoutReference?> FindCheckout(string idempotencyKey)
    {
        if (!_idempotency.TryGetValue(idempotencyKey, out string? payload))
        {
            return Result<CheckoutReference?>.Ok(null);
        }

        try
        {
            return Result<CheckoutReference?>.Ok(CheckoutReference.FromPayload(payload));
        }
        catch (InvalidOperationException)
        {
            return Result<CheckoutReference?>.Fail(new DomainError(
                "LEDGER_CORRUPT", $"stored idempotency payload for key '{idempotencyKey}' is not a checkout reference"));
        }
    }

    public Task<RebuildReceipt> RebuildAsync(CancellationToken ct = default)
    {
        LedgerState rebuilt = LedgerState.Fold(
            _movements, _trips, _sales, _returns, _loads, _voids);
        State = rebuilt;
        Context = NewContext();
        return Task.FromResult(new RebuildReceipt(
            rebuilt.LastSequence,
            _movements.Count,
            _trips.Count + _sales.Count + _returns.Count + _loads.Count + _voids.Count));
    }

    private LedgerContext NewContext() =>
        new(State, _trips.ToList(), _sales.ToList(), _returns.ToList(), _loads.ToList(), _voids.ToList());

    private static IReadOnlyList<T> Bound<T>(List<T> committed, List<T> staged) =>
        committed.Concat(staged).ToList();

    private void CommitStaged()
    {
        _sales.AddRange(_stagedSales);
        _returns.AddRange(_stagedReturns);
        _loads.AddRange(_stagedLoads);
        _voids.AddRange(_stagedVoids);
        ClearOpenAttempt();
    }

    private void ClearOpenAttempt()
    {
        _stagedSales.Clear();
        _stagedReturns.Clear();
        _stagedLoads.Clear();
        _stagedVoids.Clear();
        _stagedAdjustments.Clear();
        _stagedCounts.Clear();
        _stagedCloses.Clear();
    }
}
