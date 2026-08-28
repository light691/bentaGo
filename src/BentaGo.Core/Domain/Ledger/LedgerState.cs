using BentaGo.Core.Common;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Payments;

namespace BentaGo.Core.Domain.Ledger;

/// <summary>
/// Thrown when a movement stream violates a ledger invariant (negative stock,
/// payment-total drift, closed-trip mutation, impossible reversal, ...).
/// }
/// </summary>
public sealed class LedgerIntegrityException(string message) : Exception(message);

public enum TripStatus
{
    Planned,
    Open,
    Closed,
}

public sealed record TripState(TripStatus Status, DateTimeOffset? OpenedUtc, DateTimeOffset? ClosedUtc)
{
    public static TripState Planned => new(TripStatus.Planned, null, null);
}

public enum SaleStatus
{
    Committed,
    Voided,
    PartiallyReturned,
    FullyReturned,
}

public sealed record SaleState(SaleStatus Status, Money Total, Money Received);

public sealed record InventoryBalance(ProductId Product, LocationId Location, Quantity Units);

/// <summary>
/// Per-cell stock story: what came in, what left, and the count variance.
/// "Loaded" counts transfer-ins documented by a load (stock ladled out to a
/// peddler); "Starting" counts opening stock and direct transfers.
/// </summary>
public sealed record InventoryBreakdown(
    ProductId Product,
    LocationId Location,
    Quantity Starting,
    Quantity Loaded,
    Quantity Sold,
    Quantity Returned,
    Quantity Damaged,
    Quantity Lost,
    Quantity Adjusted,
    Quantity Ending,
    Quantity? Observed,
    Quantity? Variance)
{
    public static InventoryBreakdown None(ProductId product, LocationId location, Quantity ending) =>
        new(product, location, Quantity.Zero, Quantity.Zero, Quantity.Zero, Quantity.Zero, Quantity.Zero,
            Quantity.Zero, Quantity.Zero, ending, null, null);
}

/// <summary>
/// The rebuildable projection of the movement stream. Every balance here is a
/// pure fold of the append-only ledger: the store advances it incrementally,
/// and any corruption of the stream is caught by <see cref="Fold"/> on replay.
/// All invariants are checked exactly once, in <see cref="Validate"/>.
/// </summary>
public sealed class LedgerState
{
    public static LedgerState Empty { get; } = new();

    private readonly Dictionary<(ProductId, LocationId), long> _stock = new();
    private readonly Dictionary<Fund, long> _funds = new();
    private readonly Dictionary<(ProductId, LocationId), long> _observed = new();
    private readonly Dictionary<TripId, TripState> _trips = new();
    private readonly Dictionary<SaleId, long> _receivedBySale = new();
    private readonly Dictionary<SaleId, long> _returnedBySale = new();
    private readonly HashSet<SaleId> _voidedSales = new();
    private readonly Dictionary<SaleLineId, long> _returnedPerLine = new();
    private readonly Dictionary<long, SaleIssueInfo> _saleIssues = new();
    private readonly Dictionary<long, ReceiptInfo> _receipts = new();
    private readonly Dictionary<long, long> _refundedByReceipt = new();
    private readonly Dictionary<long, long> _revokedByIssue = new();
    private readonly Dictionary<long, SaleLineId> _lineOfIssue = new();
    private readonly Dictionary<(ProductId, LocationId), CellStats> _cellStats = new();

    private IReadOnlyDictionary<TripId, Trip> _tripsById = new Dictionary<TripId, Trip>();
    private IReadOnlyDictionary<SaleId, Sale> _salesById = new Dictionary<SaleId, Sale>();
    private IReadOnlyDictionary<ReturnId, Return> _returnsById = new Dictionary<ReturnId, Return>();
    private IReadOnlyDictionary<LoadId, StockLoad> _loadsById = new Dictionary<LoadId, StockLoad>();
    private IReadOnlyDictionary<VoidId, SaleVoid> _voidsById = new Dictionary<VoidId, SaleVoid>();

    public long LastSequence { get; private set; }

    internal sealed record SaleIssueInfo(SaleLineId LineId, SaleId Sale, ProductId Product, LocationId Location, long Quantity, long LineQuantity);
    internal sealed record ReceiptInfo(PaymentMethod Method, SaleId Sale, long Amount, PaymentLineId PaymentLineId);

    private sealed class CellStats
    {
        public long Starting;
        public long Loaded;
        public long Sold;
        public long Returned;
        public long Damaged;
        public long Lost;
        public long Adjusted;
    }

    /// <summary>
    /// Replays a committed stream from scratch. Throws <see cref="LedgerIntegrityException"/>
    /// on the first violation.
    /// </summary>
    public static LedgerState Fold(
        IEnumerable<Movement> movements,
        IReadOnlyList<Trip> trips,
        IReadOnlyList<Sale> sales,
        IReadOnlyList<Return> returns,
        IReadOnlyList<StockLoad> loads,
        IReadOnlyList<SaleVoid> voids)
    {
        var state = new LedgerState();
        state.BindDocuments(trips, sales, returns, loads, voids);

        foreach (Trip trip in trips)
        {
            state._trips.TryAdd(trip.Id, TripState.Planned);
        }

        foreach (Movement movement in movements)
        {
            DomainError? violation = state.Validate(movement);
            if (violation is not null)
            {
                throw new LedgerIntegrityException($"[{movement.Kind} seq {movement.Sequence}] {violation.Message}");
            }

            state.Apply(movement);
        }

        state.FinalizeSaleStates(sales);
        return state;
    }

    public void BindDocuments(
        IReadOnlyList<Trip> trips,
        IReadOnlyList<Sale> sales,
        IReadOnlyList<Return> returns,
        IReadOnlyList<StockLoad> loads,
        IReadOnlyList<SaleVoid> voids)
    {
        _tripsById = trips.ToDictionary(t => t.Id);
        _salesById = sales.ToDictionary(s => s.Id);
        _returnsById = returns.ToDictionary(r => r.Id);
        _loadsById = loads.ToDictionary(l => l.Id);
        _voidsById = voids.ToDictionary(v => v.Id);

        foreach (Trip trip in trips)
        {
            _trips.TryAdd(trip.Id, TripState.Planned);
        }
    }

    public Quantity StockOnHand(ProductId product, LocationId location) =>
        Quantity.Of((int)(_stock.TryGetValue((product, location), out long units) ? units : 0));

    public Money FundsOnHand(Fund fund) =>
        new(_funds.TryGetValue(fund, out long centavos) ? centavos : 0);

    public Quantity ObservedUnits(ProductId product, LocationId location) =>
        Quantity.Of((int)(_observed.TryGetValue((product, location), out long units) ? units : 0));

    public TripState Trip(TripId id) =>
        _trips.TryGetValue(id, out TripState? state) ? state : TripState.Planned;

    public SaleState Sale(SaleId id)
    {
        if (!_salesById.TryGetValue(id, out Sale? sale))
        {
            return new SaleState(SaleStatus.Committed, Money.Zero, Money.Zero);
        }

        return FinalizeSaleState(sale);
    }

    public Quantity Returned(SaleLineId line) =>
        Quantity.Of((int)(_returnedPerLine.TryGetValue(line, out long units) ? units : 0));

    /// <summary>The sequence of the sale issue for (sale, line), if it exists.</summary>
    public long? SaleIssueSequence(SaleId sale, SaleLineId line)
    {
        foreach ((long seq, SaleIssueInfo info) in _saleIssues)
        {
            if (info.Sale == sale && info.LineId == line)
            {
                return seq;
            }
        }

        return null;
    }

    /// <summary>The sequence of the receipt for (sale, payment line), if it exists.</summary>
    public long? ReceiptSequence(SaleId sale, PaymentLineId line)
    {
        foreach ((long seq, ReceiptInfo info) in _receipts)
        {
            if (info.Sale == sale && info.PaymentLineId == line)
            {
                return seq;
            }
        }

        return null;
    }

    public InventoryBreakdown Breakdown(ProductId product, LocationId location)
    {
        long ending = _stock.TryGetValue((product, location), out long units) ? units : 0;
        if (!_cellStats.TryGetValue((product, location), out CellStats? stats))
        {
            return InventoryBreakdown.None(product, location, Quantity.Of((int)ending));
        }

        long observed = _observed.TryGetValue((product, location), out long seen) ? seen : -1;
        return new InventoryBreakdown(
            product,
            location,
            Quantity.Of((int)stats.Starting),
            Quantity.Of((int)stats.Loaded),
            Quantity.Of((int)stats.Sold),
            Quantity.Of((int)stats.Returned),
            Quantity.Of((int)stats.Damaged),
            Quantity.Of((int)stats.Lost),
            Quantity.Of((int)stats.Adjusted),
            Quantity.Of((int)ending),
            observed >= 0 ? Quantity.Of((int)observed) : null,
            observed >= 0 ? Quantity.Of((int)(observed - ending)) : null);
    }

    /// <summary>
    /// The store's pre-append guard: would this movement be legal right now?
    /// Returns null when the movement may be appended.
    /// </summary>
    public DomainError? Accepts(Movement movement) => Validate(movement);

    /// <summary>
    /// Validates and applies one committed movement to this state
    /// (incremental path used by the in-memory store). Bind the documents
    /// first; the state itself stays a pure fold, so <see cref="Fold"/> and
    /// this path must agree.
    /// </summary>
    public DomainError? ApplyCommitted(Movement movement)
    {
        DomainError? violation = Validate(movement);
        if (violation is not null)
        {
            return violation;
        }

        Apply(movement);
        return null;
    }

    private DomainError? Validate(Movement movement)
    {
        if (movement.Sequence <= LastSequence)
        {
            return new DomainError("SEQ_ORDER", $"sequence {movement.Sequence} is not after the last {LastSequence}");
        }

        if (movement.WhenUtc.Offset != TimeSpan.Zero)
        {
            return new DomainError("UTC_REQUIRED", "movement timestamp is not UTC");
        }

        TripId? tripId = TripOf(movement);
        if (tripId is { } trip)
        {
            TripStatus status = _trips.TryGetValue(trip, out TripState? state) ? state.Status : TripStatus.Planned;
            if (movement is TripReopen)
            {
                if (status != TripStatus.Closed)
                {
                    return new DomainError("TRIP_NOT_CLOSED", $"trip {trip} is {status}; only a closed trip may be reopened");
                }
            }
            else if (status == TripStatus.Closed)
            {
                return new DomainError("TRIP_CLOSED", $"trip {trip} is closed; mutate it only after an authorized reopen");
            }
        }

        return movement switch
        {
            OpeningStock m => CheckCell(m.ProductId, m.LocationId, +m.Quantity.Units),
            TransferOut m => CheckCell(m.ProductId, m.LocationId, -m.Quantity.Units),
            TransferIn => null,
            SaleIssue m => CheckCell(m.ProductId, m.LocationId, -m.Quantity.Units),
            SaleIssueRevoke m => CheckSaleRevocation(m),
            ReturnToStock m => CheckReturn(m),
            Damage m => CheckCell(m.ProductId, m.LocationId, -m.Quantity.Units),
            Loss m => CheckCell(m.ProductId, m.LocationId, -m.Quantity.Units),
            AdjustmentIncrease m => CheckCell(m.ProductId, m.LocationId, +m.Quantity.Units),
            AdjustmentDecrease m => CheckCell(m.ProductId, m.LocationId, -m.Quantity.Units),
            Receipt m => CheckReceipt(m),
            ReceiptRefund m => CheckRefund(m),
            ReceiptRevoke m => CheckRevokeReceipt(m),
            CountObserved => null,
            TripClose m => _trips.TryGetValue(m.TripId, out TripState? ts) && ts.Status == TripStatus.Open
                ? null
                : new DomainError("TRIP_NOT_OPEN", $"trip {m.TripId} is not open and cannot be closed"),
            TripReopen => null,
            _ => new DomainError("UNKNOWN_KIND", $"unhandled movement kind {movement.GetType().Name}"),
        };
    }

    private DomainError? CheckCell(ProductId product, LocationId location, long delta)
    {
        long current = _stock.TryGetValue((product, location), out long c) ? c : 0;
        return current + delta >= 0
            ? null
            : new DomainError("STOCK_NEGATIVE", $"stock for {product} @ {location} would go negative ({current} {delta:+0;-0;} -> {current + delta})");
    }

    private DomainError? CheckReceipt(Receipt m)
    {
        if (m.Document is not SaleRef saleRef || !_salesById.TryGetValue(saleRef.Id, out Sale? sale))
        {
            return new DomainError("UNKNOWN_SALE", "receipt references an unknown sale");
        }

        if (_voidedSales.Contains(sale.Id))
        {
            return new DomainError("SALE_VOIDED", $"sale {sale.Id} is voided");
        }

        return m.Method.FundOf() is null
            ? new DomainError("NO_FUND", $"{m.Method} posts no fund")
            : null;
    }

    private DomainError? CheckSaleRevocation(SaleIssueRevoke m)
    {
        if (!_saleIssues.TryGetValue(m.Reverses, out SaleIssueInfo? issue))
        {
            return new DomainError("REVERSES_UNKNOWN", $"no sale issue at sequence {m.Reverses}");
        }

        if (_revokedByIssue.ContainsKey(m.Reverses))
        {
            return new DomainError("REVOKED_ALREADY", $"sale issue {m.Reverses} was already revoked");
        }

        if (issue.Product != m.ProductId || issue.Location != m.LocationId || issue.Quantity != m.Quantity.Units)
        {
            return new DomainError("REVERSES_MISMATCH", "revoke does not match the original sale issue");
        }

        return _voidedSales.Contains(issue.Sale)
            ? new DomainError("SALE_VOIDED", $"sale {issue.Sale} is already voided")
            : null;
    }

    private DomainError? CheckReturn(ReturnToStock m)
    {
        if (!_saleIssues.TryGetValue(m.Reverses, out SaleIssueInfo? issue))
        {
            return new DomainError("REVERSES_UNKNOWN", $"no sale issue at sequence {m.Reverses}");
        }

        if (issue.Product != m.ProductId || issue.Location != m.LocationId || issue.LineId != m.SaleLineId)
        {
            return new DomainError("REVERSES_MISMATCH", "return does not match the original sale issue");
        }

        if (_voidedSales.Contains(issue.Sale))
        {
            return new DomainError("SALE_VOIDED", $"return against voided sale {issue.Sale}");
        }

        long revokeBonus = _revokedByIssue.TryGetValue(m.Reverses, out long revoked) ? revoked : 0;
        long returned = _returnedPerLine.TryGetValue(issue.LineId, out long r) ? r : 0;
        return returned + m.Quantity.Units <= issue.Quantity + revokeBonus
            ? null
            : new DomainError("RETURN_LIMIT", $"return exceeds sold quantity for line {issue.LineId}");
    }

    private DomainError? CheckRefund(ReceiptRefund m)
    {
        if (!_receipts.TryGetValue(m.Reverses, out ReceiptInfo? receipt))
        {
            return new DomainError("REVERSES_UNKNOWN", $"no receipt at sequence {m.Reverses}");
        }

        if (receipt.Method != m.Method)
        {
            return new DomainError("REVERSES_MISMATCH", "refund method does not match the original receipt");
        }

        long refunded = _refundedByReceipt.TryGetValue(m.Reverses, out long r) ? r : 0;
        if (refunded + m.Amount.Centavos > receipt.Amount)
        {
            return new DomainError("REFUND_LIMIT", $"refund exceeds the original receipt at sequence {m.Reverses}");
        }

        return CheckFundNonNegative(receipt.Method.FundOf()!.Value, m.Amount.Centavos);
    }

    private DomainError? CheckRevokeReceipt(ReceiptRevoke m)
    {
        if (!_receipts.TryGetValue(m.Reverses, out ReceiptInfo? receipt))
        {
            return new DomainError("REVERSES_UNKNOWN", $"no receipt at sequence {m.Reverses}");
        }

        if (receipt.Method != m.Method || receipt.Amount != m.Amount.Centavos)
        {
            return new DomainError("REVERSES_MISMATCH", "revoke does not match the original receipt");
        }

        return CheckFundNonNegative(receipt.Method.FundOf()!.Value, m.Amount.Centavos);
    }

    private DomainError? CheckFundNonNegative(Fund fund, long outgoing)
    {
        long current = _funds.TryGetValue(fund, out long c) ? c : 0;
        return current >= outgoing
            ? null
            : new DomainError("FUND_NEGATIVE", $"fund {fund} would go negative by {outgoing - current} centavos");
    }

    private void Apply(Movement movement)
    {
        LastSequence = movement.Sequence;
        OpenTripOnFirstActivity(movement);

        switch (movement)
        {
            case OpeningStock m:
                Cell(m.ProductId, m.LocationId, +m.Quantity.Units);
                Stats(m.ProductId, m.LocationId).Starting += m.Quantity.Units;
                break;
            case TransferOut m:
                Cell(m.ProductId, m.LocationId, -m.Quantity.Units);
                Stats(m.ProductId, m.LocationId).Starting -= m.Quantity.Units;
                break;
            case TransferIn m:
                Cell(m.ProductId, m.LocationId, +m.Quantity.Units);
                if (m.Document is LoadRef)
                {
                    Stats(m.ProductId, m.LocationId).Loaded += m.Quantity.Units;
                }
                else
                {
                    Stats(m.ProductId, m.LocationId).Starting += m.Quantity.Units;
                }

                break;
            case SaleIssue m:
                Cell(m.ProductId, m.LocationId, -m.Quantity.Units);
                Stats(m.ProductId, m.LocationId).Sold += m.Quantity.Units;
                _saleIssues[movement.Sequence] = new SaleIssueInfo(
                    m.SaleLineId, ((SaleRef)m.Document).Id, m.ProductId, m.LocationId, m.Quantity.Units, m.Quantity.Units);
                break;
            case SaleIssueRevoke m:
                Cell(m.ProductId, m.LocationId, +m.Quantity.Units);
                Stats(m.ProductId, m.LocationId).Sold -= m.Quantity.Units;
                _revokedByIssue[m.Reverses] = m.Quantity.Units;
                if (_saleIssues.TryGetValue(m.Reverses, out SaleIssueInfo? issue))
                {
                    _voidedSales.Add(issue.Sale);
                }

                break;
            case ReturnToStock m:
                Cell(m.ProductId, m.LocationId, +m.Quantity.Units);
                Stats(m.ProductId, m.LocationId).Returned += m.Quantity.Units;
                long returned = _returnedPerLine.TryGetValue(m.SaleLineId, out long r) ? r : 0;
                _returnedPerLine[m.SaleLineId] = returned + m.Quantity.Units;
                if (_saleIssues.TryGetValue(m.Reverses, out SaleIssueInfo? issue2))
                {
                    long bySale = _returnedBySale.TryGetValue(issue2.Sale, out long s) ? s : 0;
                    _returnedBySale[issue2.Sale] = bySale + m.Quantity.Units;
                }

                break;
            case Damage m:
                Cell(m.ProductId, m.LocationId, -m.Quantity.Units);
                Stats(m.ProductId, m.LocationId).Damaged += m.Quantity.Units;
                break;
            case Loss m:
                Cell(m.ProductId, m.LocationId, -m.Quantity.Units);
                Stats(m.ProductId, m.LocationId).Lost += m.Quantity.Units;
                break;
            case AdjustmentIncrease m:
                Cell(m.ProductId, m.LocationId, +m.Quantity.Units);
                Stats(m.ProductId, m.LocationId).Adjusted += m.Quantity.Units;
                break;
            case AdjustmentDecrease m:
                Cell(m.ProductId, m.LocationId, -m.Quantity.Units);
                Stats(m.ProductId, m.LocationId).Adjusted -= m.Quantity.Units;
                break;
            case Receipt m:
                Fund(m.Method.FundOf()!.Value, +m.Amount.Centavos);
                _receipts[movement.Sequence] = new ReceiptInfo(m.Method, ((SaleRef)m.Document).Id, m.Amount.Centavos, m.PaymentLineId);
                _receivedBySale[((SaleRef)m.Document).Id] =
                    (_receivedBySale.TryGetValue(((SaleRef)m.Document).Id, out long recv) ? recv : 0) + m.Amount.Centavos;
                break;
            case ReceiptRefund m:
                Fund(m.Method.FundOf()!.Value, -m.Amount.Centavos);
                long refunded = _refundedByReceipt.TryGetValue(m.Reverses, out long rf) ? rf : 0;
                _refundedByReceipt[m.Reverses] = refunded + m.Amount.Centavos;
                if (_receipts.TryGetValue(m.Reverses, out ReceiptInfo? refundedReceipt))
                {
                    _receivedBySale[refundedReceipt.Sale] =
                        (_receivedBySale.TryGetValue(refundedReceipt.Sale, out long recv2) ? recv2 : 0) - m.Amount.Centavos;
                }

                break;
            case ReceiptRevoke m:
                Fund(m.Method.FundOf()!.Value, -m.Amount.Centavos);
                if (_receipts.TryGetValue(m.Reverses, out ReceiptInfo? revokedReceipt))
                {
                    _receivedBySale[revokedReceipt.Sale] =
                        (_receivedBySale.TryGetValue(revokedReceipt.Sale, out long recv3) ? recv3 : 0) - m.Amount.Centavos;
                }

                break;
            case CountObserved m:
                _observed[(m.ProductId, m.LocationId)] = m.ObservedQuantity.Units;
                break;
            case TripClose m:
                MarkTrip(m.TripId, TripStatus.Closed, closedUtc: m.WhenUtc);
                break;
            case TripReopen m:
                MarkTrip(m.TripId, TripStatus.Open, openedUtc: m.WhenUtc);
                break;
        }
    }

    private void OpenTripOnFirstActivity(Movement movement)
    {
        if (movement is TripClose or TripReopen)
        {
            return;
        }

        TripId? tripId = TripOf(movement);
        if (tripId is not { } trip)
        {
            return;
        }

        if (_trips.TryGetValue(trip, out TripState? state) && state.Status == TripStatus.Planned)
        {
            _trips[trip] = new TripState(TripStatus.Open, movement.WhenUtc, null);
        }
    }

    private void FinalizeSaleStates(IReadOnlyList<Sale> sales)
    {
        foreach (Sale sale in sales)
        {
            // keep the existing entry so incremental appends preserve receipts
            _ = _receivedBySale[sale.Id] = _receivedBySale.TryGetValue(sale.Id, out long received) ? received : 0;
        }
    }

    private SaleState FinalizeSaleState(Sale sale)
    {
        long received = _receivedBySale.TryGetValue(sale.Id, out long r) ? r : 0;
        SaleStatus status;
        if (_voidedSales.Contains(sale.Id))
        {
            status = SaleStatus.Voided;
        }
        else
        {
            long lineUnits = 0;
            foreach (SaleLine line in sale.Lines)
            {
                lineUnits += line.Quantity.Units;
            }

            long returned = _returnedBySale.TryGetValue(sale.Id, out long ret) ? ret : 0;
            status = returned == 0
                ? SaleStatus.Committed
                : returned >= lineUnits
                    ? SaleStatus.FullyReturned
                    : SaleStatus.PartiallyReturned;
        }

        return new SaleState(status, sale.Total, new Money(received));
    }

    private TripId? TripOf(Movement movement)
    {
        switch (movement)
        {
            case TripClose m:
                return m.TripId;
            case TripReopen m:
                return m.TripId;
            case SaleIssue m:
                return SaleTrip(((SaleRef)m.Document).Id);
            case Receipt m:
                return SaleTrip(((SaleRef)m.Document).Id);
            case SaleIssueRevoke m:
                if (_saleIssues.TryGetValue(m.Reverses, out SaleIssueInfo? issue))
                {
                    return _salesById.TryGetValue(issue.Sale, out Sale? sale) ? sale.Trip : null;
                }

                return null;
            case ReturnToStock m:
                return SaleTripOfIssue(m.Reverses);
            case ReceiptRefund m:
                return _receipts.TryGetValue(m.Reverses, out ReceiptInfo? receipt)
                    ? SaleTrip(receipt.Sale)
                    : null;
            case TransferOut m when m.Document is LoadRef loadRef:
                return LoadTrip(loadRef.Id);
            case TransferIn m when m.Document is LoadRef loadRef:
                return LoadTrip(loadRef.Id);
            default:
                return null;
        }

        TripId? SaleTrip(SaleId saleId) =>
            _salesById.TryGetValue(saleId, out Sale? sale) ? sale.Trip : null;

        TripId? LoadTrip(LoadId loadId) =>
            _loadsById.TryGetValue(loadId, out StockLoad? load) ? load.Trip : null;

        TripId? SaleTripOfIssue(long issueSeq) =>
            _saleIssues.TryGetValue(issueSeq, out SaleIssueInfo? issue) ? SaleTrip(issue.Sale) : null;
    }

    private void Cell(ProductId product, LocationId location, long delta)
    {
        long current = _stock.TryGetValue((product, location), out long c) ? c : 0;
        _stock[(product, location)] = current + delta;
    }

    private void Fund(Fund fund, long delta)
    {
        long current = _funds.TryGetValue(fund, out long c) ? c : 0;
        _funds[fund] = current + delta;
    }

    private void MarkTrip(TripId trip, TripStatus status, DateTimeOffset? closedUtc = null, DateTimeOffset? openedUtc = null)
    {
        TripState current = _trips.TryGetValue(trip, out TripState? s) ? s : TripState.Planned;
        _trips[trip] = new TripState(status, openedUtc ?? current.OpenedUtc, closedUtc ?? current.ClosedUtc);
    }

    private CellStats Stats(ProductId product, LocationId location)
    {
        if (!_cellStats.TryGetValue((product, location), out CellStats? stats))
        {
            stats = new CellStats();
            _cellStats[(product, location)] = stats;
        }

        return stats;
    }
}
