using BentaGo.Core.Common;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Payments;

namespace BentaGo.Core.Domain.Ledger;

/// <summary>
/// Thrown when a movement stream violates a ledger invariant (negative stock,
/// payment-total drift, closed-day mutation, impossible reversal, ...).
/// </summary>
public sealed class LedgerIntegrityException(string message) : Exception(message);

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
/// The rebuildable projection of the movement stream. Every balance here is a
/// pure fold of the append-only ledger: the store advances it incrementally,
/// and any corruption of the stream is caught by <see cref="Fold"/> on replay
/// (invariants in <see cref="Validate"/>, document/total drift by
/// <see cref="VerifyCrossChecks"/>). All invariants are checked exactly once.
/// </summary>
public sealed class LedgerState
{
    private readonly Dictionary<(ProductId, LocationId), long> _stock = new();
    private readonly Dictionary<Fund, long> _funds = new();
    private readonly Dictionary<(ProductId, LocationId), long> _observed = new();
    private readonly Dictionary<DayKey, DayLifecycleState> _days = new();
    private readonly Dictionary<DayKey, long> _openingFloat = new();
    private readonly Dictionary<DayKey, long> _dayCash = new();
    private readonly Dictionary<SaleId, long> _receivedBySale = new();
    private readonly Dictionary<SaleId, long> _returnedBySale = new();
    private readonly HashSet<SaleId> _voidedSales = new();
    private readonly Dictionary<SaleLineId, long> _returnedPerLine = new();
    private readonly Dictionary<long, SaleIssueInfo> _saleIssues = new();
    private readonly Dictionary<long, ReceiptInfo> _receipts = new();
    private readonly Dictionary<long, long> _refundedByReceipt = new();
    private readonly Dictionary<long, long> _revokedByIssue = new();
    private readonly Dictionary<long, long> _revokedByReceipt = new();

    private IReadOnlyDictionary<SaleId, Sale> _salesById = new Dictionary<SaleId, Sale>();
    private IReadOnlyDictionary<ReturnId, Return> _returnsById = new Dictionary<ReturnId, Return>();
    private IReadOnlyDictionary<VoidId, SaleVoid> _voidsById = new Dictionary<VoidId, SaleVoid>();

    public long LastSequence { get; private set; }

    internal sealed record SaleIssueInfo(SaleLineId LineId, SaleId Sale, ProductId Product, LocationId Location, long Quantity);
    internal sealed record ReceiptInfo(PaymentMethod Method, SaleId Sale, long Amount, PaymentLineId PaymentLineId);

    /// <summary>
    /// Replays a committed stream from scratch. Throws <see cref="LedgerIntegrityException"/>
    /// on the first violation.
    /// </summary>
    public static LedgerState Fold(
        IEnumerable<Movement> movements,
        IReadOnlyList<Sale> sales,
        IReadOnlyList<Return> returns,
        IReadOnlyList<SaleVoid> voids)
    {
        var state = new LedgerState();
        state.BindDocuments(sales, returns, voids);

        foreach (Movement movement in movements)
        {
            DomainError? violation = state.Validate(movement);
            if (violation is not null)
            {
                throw new LedgerIntegrityException($"[{movement.Kind} seq {movement.Sequence}] {violation.Message}");
            }

            state.Apply(movement);
        }

        state.VerifyCrossChecks(sales);
        return state;
    }

    public void BindDocuments(
        IReadOnlyList<Sale> sales,
        IReadOnlyList<Return> returns,
        IReadOnlyList<SaleVoid> voids)
    {
        _salesById = sales.ToDictionary(s => s.Id);
        _returnsById = returns.ToDictionary(r => r.Id);
        _voidsById = voids.ToDictionary(v => v.Id);
    }

    /// <summary>
    /// Registers the day lifecycle documents (opens/closes/reopens) so the
    /// state can answer the day gate. The lifecycle derivation from the
    /// documents is a fold concern and lands with the P1b fold slice.
    /// </summary>
    public void BindDays(IReadOnlyList<DayOpen> opens, IReadOnlyList<DayClose> closes, IReadOnlyList<DayReopen> reopens)
    {
        foreach (DayOpen open in opens)
        {
            _days[open.Day] = DayLifecycleState.Open;
            _openingFloat[open.Day] = open.OpeningFloatCentavos;
        }

        foreach (DayClose close in closes)
        {
            _days[close.Day] = DayLifecycleState.Closed;
        }

        foreach (DayReopen reopen in reopens)
        {
            _days[reopen.Day] = DayLifecycleState.Open;
        }
    }

    public Quantity StockOnHand(ProductId product, LocationId location) =>
        Quantity.Of((int)(_stock.TryGetValue((product, location), out long units) ? units : 0));

    public Money FundsOnHand(Fund fund) =>
        new(_funds.TryGetValue(fund, out long centavos) ? centavos : 0);

    public Quantity ObservedUnits(ProductId product, LocationId location) =>
        Quantity.Of((int)(_observed.TryGetValue((product, location), out long units) ? units : 0));

    /// <summary>The lifecycle state of a trading day; a day with no documents is New.</summary>
    public DayLifecycleState Day(DayKey day) =>
        _days.TryGetValue(day, out DayLifecycleState state) ? state : DayLifecycleState.New;

    /// <summary>The opening float bound from the day's <see cref="DayOpen"/> document; zero when the day never opened.</summary>
    public long OpeningFloat(DayKey day) =>
        _openingFloat.TryGetValue(day, out long centavos) ? centavos : 0;

    /// <summary>
    /// The expected drawer cash for a trading day (docs/decisions/0004 §1):
    /// opening float + cash sales − cash refunds/revoked cash − approved cash
    /// expenses (FundOut) + cash pay-downs received (FundIn). Every term is a
    /// pure fold of the append-only stream, each cash flow day-keyed by its
    /// own movement timestamp (corrections attach to the day they happen on).
    /// </summary>
    public long ExpectedCash(DayKey day) =>
        OpeningFloat(day) + (_dayCash.TryGetValue(day, out long centavos) ? centavos : 0);

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

        DomainError? documentError = CheckDocument(movement);
        if (documentError is not null)
        {
            return documentError;
        }

        return movement switch
        {
            GoodsReceived => null,
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
            FundIn => null,
            FundOut m => CheckFundNonNegative(m.Method.FundOf()!.Value, m.Amount.Centavos),
            CountObserved => null,
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

    /// <summary>
    /// Each movement kind pins exactly the document kinds that can cause it,
    /// so a forged movement carrying the wrong document kind never reaches
    /// the casts in <see cref="Apply"/>.
    /// </summary>
    private DomainError? CheckDocument(Movement movement)
    {
        bool legal = movement switch
        {
            GoodsReceived => movement.Document is GoodsRef,
            SaleIssue or Receipt => movement.Document is SaleRef,
            SaleIssueRevoke or ReceiptRevoke => movement.Document is VoidRef,
            ReturnToStock or ReceiptRefund => movement.Document is ReturnRef,
            Damage or Loss or AdjustmentIncrease or AdjustmentDecrease => movement.Document is AdjustmentRef,
            FundIn or FundOut => movement.Document is FundRef,
            CountObserved => movement.Document is CountRef,
            _ => true,
        };

        return legal
            ? null
            : new DomainError("DOC_KIND", $"{movement.Kind} cannot carry a {movement.Document.GetType().Name}");
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

        // BG-007 L8: a return may land at a DIFFERENT destination than the
        // selling location — the reversal pins the product and line, while
        // the location is where the item physically went. The
        // returned-quantity limit stays per line.
        if (issue.Product != m.ProductId || issue.LineId != m.SaleLineId)
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

        if (_revokedByReceipt.ContainsKey(m.Reverses))
        {
            return new DomainError("REFUND_REVOKED", $"receipt {m.Reverses} was revoked and cannot be refunded");
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

        if (_revokedByReceipt.ContainsKey(m.Reverses))
        {
            return new DomainError("REVOKED_ALREADY", $"receipt {m.Reverses} was already revoked");
        }

        if (_refundedByReceipt.ContainsKey(m.Reverses))
        {
            return new DomainError("REVOKE_REFUNDED", $"receipt {m.Reverses} was refunded and cannot be revoked");
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

        switch (movement)
        {
            case GoodsReceived m:
                Cell(m.ProductId, m.LocationId, +m.Quantity.Units);
                break;
            case SaleIssue m:
                Cell(m.ProductId, m.LocationId, -m.Quantity.Units);
                _saleIssues[movement.Sequence] = new SaleIssueInfo(
                    m.SaleLineId, ((SaleRef)m.Document).Id, m.ProductId, m.LocationId, m.Quantity.Units);
                break;
            case SaleIssueRevoke m:
                Cell(m.ProductId, m.LocationId, +m.Quantity.Units);
                _revokedByIssue[m.Reverses] = m.Quantity.Units;
                if (_saleIssues.TryGetValue(m.Reverses, out SaleIssueInfo? issue))
                {
                    _voidedSales.Add(issue.Sale);
                }

                break;
            case ReturnToStock m:
                Cell(m.ProductId, m.LocationId, +m.Quantity.Units);
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
                break;
            case Loss m:
                Cell(m.ProductId, m.LocationId, -m.Quantity.Units);
                break;
            case AdjustmentIncrease m:
                Cell(m.ProductId, m.LocationId, +m.Quantity.Units);
                break;
            case AdjustmentDecrease m:
                Cell(m.ProductId, m.LocationId, -m.Quantity.Units);
                break;
            case Receipt m:
                Fund(m.Method.FundOf()!.Value, +m.Amount.Centavos);
                if (m.Method == PaymentMethod.Cash)
                {
                    DayCash(DayKey.FromUtc(m.WhenUtc), +m.Amount.Centavos);
                }

                _receipts[movement.Sequence] = new ReceiptInfo(m.Method, ((SaleRef)m.Document).Id, m.Amount.Centavos, m.PaymentLineId);
                _receivedBySale[((SaleRef)m.Document).Id] =
                    (_receivedBySale.TryGetValue(((SaleRef)m.Document).Id, out long recv) ? recv : 0) + m.Amount.Centavos;
                break;
            case ReceiptRefund m:
                Fund(m.Method.FundOf()!.Value, -m.Amount.Centavos);
                if (m.Method == PaymentMethod.Cash)
                {
                    DayCash(DayKey.FromUtc(m.WhenUtc), -m.Amount.Centavos);
                }

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
                if (m.Method == PaymentMethod.Cash)
                {
                    DayCash(DayKey.FromUtc(m.WhenUtc), -m.Amount.Centavos);
                }

                _revokedByReceipt[m.Reverses] = m.Amount.Centavos;
                if (_receipts.TryGetValue(m.Reverses, out ReceiptInfo? revokedReceipt))
                {
                    _receivedBySale[revokedReceipt.Sale] =
                        (_receivedBySale.TryGetValue(revokedReceipt.Sale, out long recv3) ? recv3 : 0) - m.Amount.Centavos;
                }

                break;
            case FundIn m:
                Fund(m.Method.FundOf()!.Value, +m.Amount.Centavos);
                if (m.Method == PaymentMethod.Cash)
                {
                    DayCash(DayKey.FromUtc(m.WhenUtc), +m.Amount.Centavos);
                }

                break;
            case FundOut m:
                Fund(m.Method.FundOf()!.Value, -m.Amount.Centavos);
                if (m.Method == PaymentMethod.Cash)
                {
                    DayCash(DayKey.FromUtc(m.WhenUtc), -m.Amount.Centavos);
                }

                break;
            case CountObserved m:
                _observed[(m.ProductId, m.LocationId)] = m.ObservedQuantity.Units;
                break;
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

    /// <summary>
    /// The synthesis's replay guard: on top of the per-movement invariants this
    /// independently verifies the bound documents against the stream, so a
    /// duplicated or mutated receipt/issue is loud instead of silently
    /// inflating funds or stock. Called by <see cref="Fold"/> on every replay
    /// and by the store after each append batch.
    /// </summary>
    public void VerifyCrossChecks(IReadOnlyList<Sale> sales)
    {
        foreach ((long seq, ReceiptInfo info) in _receipts)
        {
            if (!_salesById.TryGetValue(info.Sale, out Sale? sale))
            {
                throw new LedgerIntegrityException($"[seq {seq}] receipt for unknown sale {info.Sale}");
            }

            bool known = false;
            foreach (PaymentLine payment in sale.Payments)
            {
                if (payment.Id == info.PaymentLineId)
                {
                    known = true;
                    break;
                }
            }

            if (!known)
            {
                throw new LedgerIntegrityException($"[seq {seq}] receipt settles foreign payment line {info.PaymentLineId}");
            }
        }

        foreach ((long seq, SaleIssueInfo info) in _saleIssues)
        {
            if (!_salesById.TryGetValue(info.Sale, out Sale? sale))
            {
                throw new LedgerIntegrityException($"[seq {seq}] issue for unknown sale {info.Sale}");
            }

            bool known = false;
            foreach (SaleLine line in sale.Lines)
            {
                if (line.Id == info.LineId)
                {
                    known = true;
                    break;
                }
            }

            if (!known)
            {
                throw new LedgerIntegrityException($"[seq {seq}] issue for foreign sale line {info.LineId}");
            }
        }

        foreach (Sale sale in sales)
        {
            foreach (SaleLine line in sale.Lines)
            {
                long issues = 0;
                foreach ((long seq, SaleIssueInfo info) in _saleIssues)
                {
                    if (info.Sale != sale.Id || info.LineId != line.Id)
                    {
                        continue;
                    }

                    issues++;
                    if (info.Quantity != line.Quantity.Units)
                    {
                        throw new LedgerIntegrityException($"[sale {sale.Id}] issue {seq} carries {info.Quantity} but the document line says {line.Quantity.Units}");
                    }
                }

                if (issues != 1)
                {
                    throw new LedgerIntegrityException($"[sale {sale.Id}] line {line.Id} has {issues} issues; expected exactly 1");
                }
            }

            foreach (PaymentLine payment in sale.Payments)
            {
                if (payment.Method == PaymentMethod.Credit)
                {
                    continue;
                }

                long receipts = 0;
                foreach ((long seq, ReceiptInfo info) in _receipts)
                {
                    if (info.Sale != sale.Id || info.PaymentLineId != payment.Id)
                    {
                        continue;
                    }

                    receipts++;
                    if (info.Method != payment.Method || info.Amount != payment.Amount.Centavos)
                    {
                        throw new LedgerIntegrityException($"[sale {sale.Id}] receipt {seq} ({info.Method} {info.Amount}) does not match payment line {payment.Id} ({payment.Method} {payment.Amount.Centavos})");
                    }
                }

                if (receipts != 1)
                {
                    throw new LedgerIntegrityException($"[sale {sale.Id}] payment line {payment.Id} has {receipts} receipts; expected exactly 1");
                }
            }
        }
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

    /// <summary>The day-scoped cash term of the expected-cash fold (docs/decisions/0004 §1).</summary>
    private void DayCash(DayKey day, long delta)
    {
        long current = _dayCash.TryGetValue(day, out long c) ? c : 0;
        _dayCash[day] = current + delta;
    }

    /// <summary>
    /// Copies this state so a batch can be validated-and-applied off to the
    /// side: on any failure the clone is discarded and the live state is
    /// untouched, so an append is truly atomic.
    /// </summary>
    public LedgerState Clone()
    {
        var copy = new LedgerState { LastSequence = LastSequence };

        foreach ((var key, long value) in _stock)
            copy._stock[key] = value;
        foreach ((var key, long value) in _funds)
            copy._funds[key] = value;
        foreach ((var key, long value) in _observed)
            copy._observed[key] = value;
        foreach ((var key, DayLifecycleState value) in _days)
            copy._days[key] = value;
        foreach ((var key, long value) in _openingFloat)
            copy._openingFloat[key] = value;
        foreach ((var key, long value) in _dayCash)
            copy._dayCash[key] = value;
        foreach ((var key, long value) in _receivedBySale)
            copy._receivedBySale[key] = value;
        foreach ((var key, long value) in _returnedBySale)
            copy._returnedBySale[key] = value;
        foreach (SaleId key in _voidedSales)
            copy._voidedSales.Add(key);
        foreach ((var key, long value) in _returnedPerLine)
            copy._returnedPerLine[key] = value;
        foreach ((var key, SaleIssueInfo value) in _saleIssues)
            copy._saleIssues[key] = value;
        foreach ((var key, ReceiptInfo value) in _receipts)
            copy._receipts[key] = value;
        foreach ((var key, long value) in _refundedByReceipt)
            copy._refundedByReceipt[key] = value;
        foreach ((var key, long value) in _revokedByIssue)
            copy._revokedByIssue[key] = value;
        foreach ((var key, long value) in _revokedByReceipt)
            copy._revokedByReceipt[key] = value;

        copy._salesById = _salesById;
        copy._returnsById = _returnsById;
        copy._voidsById = _voidsById;
        return copy;
    }
}
