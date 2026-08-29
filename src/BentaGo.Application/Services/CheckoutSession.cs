using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Catalog;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Payments;

namespace BentaGo.Application.Services;

/// <summary>
/// The BG-006 deep module (D1–D7). One instance per sale; exactly-once is a
/// typed invariant: <see cref="ICheckoutSession.Key"/> is minted once and
/// reused on every attempt, so a duplicate Enter, a retry after failed
/// persistence, and a post-crash re-ask all present the SAME key —
/// FindCheckout replays the identical SavedSaleState. The UI never builds a
/// NewSaleCommand, never sees the store, and never touches a receipt format.
/// The checkout service seam stays byte-unchanged; all policy lives here.
/// </summary>
public sealed class CheckoutSession : ICheckoutSession
{
    private const int MaxReceiptRetries = 1;

    private readonly ILedgerStore _store;
    private readonly IProductStore _products;
    private readonly ICheckoutService _checkout;
    private readonly ISaleNumberSource _numbers;
    private readonly IActorContext _actors;
    private readonly User _user;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CheckoutKey _key = new(Guid.NewGuid());

    private readonly List<SessionLine> _lines = new();
    private readonly List<PaymentLineDraft> _payments = new();
    private readonly HashSet<string> _attemptedReceipts = new(StringComparer.Ordinal);

    private Money _change = Money.Zero;
    private CheckoutSessionState _state = CheckoutSessionState.Building;
    private string _receiptNumber = "";
    private SaleId _savedSaleId;

    public CheckoutSession(
        ILedgerStore store,
        IProductStore products,
        ICheckoutService checkout,
        ISaleNumberSource numbers,
        IActorContext actors,
        User user,
        Func<DateTimeOffset> clock,
        Trip openTrip)
    {
        _store = store;
        _products = products;
        _checkout = checkout;
        _numbers = numbers;
        _actors = actors;
        _user = user;
        _clock = clock;
        Trip = openTrip.Id;
        Peddler = openTrip.Peddler;
    }

    public CheckoutKey Key => _key;
    public CheckoutSessionState State => _state;
    public TripId Trip { get; }
    public PeddlerId Peddler { get; }
    public IReadOnlyList<SessionLine> Lines => _lines;
    public string ReceiptNumber => _receiptNumber;

    public Money Total
    {
        get
        {
            Money total = Money.Zero;
            foreach (SessionLine line in _lines)
            {
                total = total.Plus(line.UnitPrice.Times(line.Quantity.Units).Minus(line.Discount));
            }

            return total;
        }
    }

    public Money DiscountTotal
    {
        get
        {
            Money total = Money.Zero;
            foreach (SessionLine line in _lines)
            {
                total = total.Plus(line.Discount);
            }

            return total;
        }
    }

    public event EventHandler? Changed;

    public Result<bool> AddProduct(Product product, Quantity quantity)
    {
        if (_state != CheckoutSessionState.Building)
        {
            return Result<bool>.Fail(new DomainError("SESSION_CLOSED", "the session is no longer building"));
        }

        if (quantity.IsZero)
        {
            return Result<bool>.Fail(new DomainError("LINE_ZERO", "a sale line needs a positive quantity", product.Sku));
        }

        LocationId location = TripLocation();
        foreach (SessionLine line in _lines)
        {
            if (line.ProductId != product.Id)
            {
                continue;
            }

            Quantity proposed = line.Quantity.Plus(quantity);
            if (_store.State.StockOnHand(product.Id, location).Units < proposed.Units)
            {
                return Result<bool>.Fail(new DomainError(
                    "STOCK_INSUFFICIENT", "not enough stock on hand", product.Sku));
            }

            ReplaceLine(line with { Quantity = proposed });
            RaiseChanged();
            return Result<bool>.Ok(true);
        }

        if (_store.State.StockOnHand(product.Id, location).Units < quantity.Units)
        {
            return Result<bool>.Fail(new DomainError(
                "STOCK_INSUFFICIENT", "not enough stock on hand", product.Sku));
        }

        _lines.Add(new SessionLine(
            new SaleLineId(Guid.NewGuid()), product.Id, product.Sku, product.Name,
            product.RetailPrice, quantity, Money.Zero, DiscountType.None));
        RaiseChanged();
        return Result<bool>.Ok(true);
    }

    public Result<bool> AdjustQuantity(SaleLineId lineId, int delta)
    {
        if (_state != CheckoutSessionState.Building)
        {
            return Result<bool>.Fail(new DomainError("SESSION_CLOSED", "the session is no longer building"));
        }

        SessionLine? line = FindLine(lineId);
        if (line is null)
        {
            return Result<bool>.Fail(new DomainError("LINE_UNKNOWN", "no such sale line"));
        }

        int proposed = line.Quantity.Units + delta;
        if (proposed < 1)
        {
            return Result<bool>.Fail(new DomainError("LINE_ZERO", "a sale line needs a positive quantity", line.Sku));
        }

        if (delta > 0 && _store.State.StockOnHand(line.ProductId, TripLocation()).Units < proposed)
        {
            return Result<bool>.Fail(new DomainError(
                "STOCK_INSUFFICIENT", "not enough stock on hand", line.Sku));
        }

        ReplaceLine(line with { Quantity = Quantity.Of(proposed) });
        RaiseChanged();
        return Result<bool>.Ok(true);
    }

    public Result<bool> RemoveLine(SaleLineId lineId)
    {
        if (_state != CheckoutSessionState.Building)
        {
            return Result<bool>.Fail(new DomainError("SESSION_CLOSED", "the session is no longer building"));
        }

        if (FindLine(lineId) is null)
        {
            return Result<bool>.Fail(new DomainError("LINE_UNKNOWN", "no such sale line"));
        }

        _lines.RemoveAll(l => l.LineId == lineId);
        RaiseChanged();
        return Result<bool>.Ok(true);
    }

    public Result<bool> ApplyDiscount(SaleLineId lineId, DiscountType type)
    {
        if (_state != CheckoutSessionState.Building)
        {
            return Result<bool>.Fail(new DomainError("SESSION_CLOSED", "the session is no longer building"));
        }

        SessionLine? line = FindLine(lineId);
        if (line is null)
        {
            return Result<bool>.Fail(new DomainError("LINE_UNKNOWN", "no such sale line"));
        }

        Product? product = _products.FindBySkuAsync(line.Sku).GetAwaiter().GetResult();
        if (product is null)
        {
            return Result<bool>.Fail(new DomainError("LINE_UNKNOWN_PRODUCT", "the catalog product no longer exists", line.Sku));
        }

        Result<Money> discount = DiscountPolicy.Allowed(
            product, type, DiscountRates.Statutory, line.UnitPrice, line.Quantity);
        if (discount.IsFailure)
        {
            return Result<bool>.Fail(discount.Errors);
        }

        ReplaceLine(line with { Discount = discount.Value, DiscountType = type });
        RaiseChanged();
        return Result<bool>.Ok(true);
    }

    public Result<bool> SetPayment(IReadOnlyList<PaymentLineDraft> drafts, Money change)
    {
        if (_state != CheckoutSessionState.Building)
        {
            return Result<bool>.Fail(new DomainError("SESSION_CLOSED", "the session is no longer building"));
        }

        if (drafts.Count == 0)
        {
            return Result<bool>.Fail(new DomainError("PAYMENT_EMPTY", "a sale needs at least one payment line"));
        }

        if (change.Centavos < 0)
        {
            return Result<bool>.Fail(new DomainError("CHANGE_MISMATCH", "change cannot be negative"));
        }

        var errors = new List<DomainError>();
        Money sum = Money.Zero;
        int cashLines = 0;
        foreach (PaymentLineDraft draft in drafts)
        {
            if (draft.Amount.Centavos <= 0)
            {
                errors.Add(new DomainError("PAYMENT_NONPOSITIVE", "a payment amount must be positive", draft.Method.DisplayName()));
                continue;
            }

            sum = sum.Plus(draft.Amount);
            if (draft.Method == PaymentMethod.Cash)
            {
                cashLines++;
            }

            if (draft.Method == PaymentMethod.BankTransfer && string.IsNullOrWhiteSpace(draft.Reference?.Trim()))
            {
                errors.Add(new DomainError("BANK_REFERENCE_REQUIRED", "a bank transfer needs the reference", draft.Method.DisplayName()));
            }
        }

        if (cashLines > 1)
        {
            errors.Add(new DomainError("CHANGE_MISMATCH", "change applies to one cash line only"));
        }

        if (sum.Centavos != Total.Centavos)
        {
            errors.Add(new DomainError(
                "PAYMENT_TOTAL",
                $"payments {sum.ToPesos():0.00} do not equal the sale total {Total.ToPesos():0.00}"));
        }

        if (errors.Count > 0)
        {
            return Result<bool>.Fail(errors);
        }

        _payments.Clear();
        _payments.AddRange(drafts);
        _change = change;
        RaiseChanged();
        return Result<bool>.Ok(true);
    }

    public async Task<Result<SavedSaleState>> SubmitAsync(CancellationToken ct = default)
    {
        if (_state == CheckoutSessionState.Saved)
        {
            _store.Bind();
            Sale? committed = _store.Context.FindSale(_savedSaleId);
            return committed is null
                ? Result<SavedSaleState>.Fail(new DomainError("UNKNOWN_SALE", "the committed sale is gone"))
                : Result<SavedSaleState>.Ok(SavedSaleState.From(committed, replayed: true));
        }

        if (_state == CheckoutSessionState.Submitting)
        {
            return Result<SavedSaleState>.Fail(new DomainError("SUBMIT_IN_PROGRESS", "a submit is already running"));
        }

        if (_lines.Count == 0)
        {
            return Result<SavedSaleState>.Fail(new DomainError("SALE_EMPTY", "a sale needs at least one line"));
        }

        _state = CheckoutSessionState.Submitting;
        try
        {
            Result<bool> verified = ActorGuard.Verify(_actors, _user.Id);
            if (verified.IsFailure)
            {
                return BackToBuilding(verified.Errors);
            }

            LocationId location = TripLocation();
            var errors = new List<DomainError>();
            var lines = new List<NewSaleLine>(_lines.Count);
            foreach (SessionLine line in _lines)
            {
                Product? product = await _products.FindBySkuAsync(line.Sku, ct);
                if (product is null)
                {
                    errors.Add(new DomainError("LINE_UNKNOWN_PRODUCT", "the catalog product no longer exists", line.Sku));
                    continue;
                }

                // Price authority: the product record's locked retail price
                // (0001-4) — a caller-supplied price is never trusted; the
                // line's scanned price is display-only.
                Result<Money> discount = DiscountPolicy.Allowed(
                    product, line.DiscountType, DiscountRates.Statutory,
                    product.RetailPrice, line.Quantity);
                if (discount.IsFailure)
                {
                    errors.AddRange(discount.Errors);
                    continue;
                }

                if (_store.State.StockOnHand(product.Id, location).Units < line.Quantity.Units)
                {
                    errors.Add(new DomainError("STOCK_INSUFFICIENT", "not enough stock on hand", line.Sku));
                    continue;
                }

                lines.Add(new NewSaleLine(
                    line.LineId, product.Id, product.Sku, product.Name,
                    product.RetailPrice, line.Quantity, discount.Value, line.DiscountType));
            }

            bool cashSeen = false;
            var payments = new List<NewPaymentLine>(_payments.Count);
            Money changeRemaining = _change;
            foreach (PaymentLineDraft draft in _payments)
            {
                Money tendered = draft.Amount;
                Money change = Money.Zero;
                if (draft.Method == PaymentMethod.Cash)
                {
                    cashSeen = true;
                    tendered = draft.Amount.Plus(changeRemaining);
                    change = changeRemaining;
                    changeRemaining = Money.Zero; // change applies to the one cash line
                }

                payments.Add(new NewPaymentLine(
                    new PaymentLineId(Guid.NewGuid()), draft.Method,
                    draft.Reference?.Trim() ?? "", draft.Amount, tendered, change));
            }

            if (changeRemaining.Centavos > 0 && !cashSeen)
            {
                errors.Add(new DomainError("CHANGE_MISMATCH", "change requires a cash payment"));
            }

            if (errors.Count > 0)
            {
                return BackToBuilding(errors);
            }

            DateTimeOffset now = _clock();
            if (_receiptNumber.Length == 0)
            {
                _receiptNumber = _numbers.Next(now, _attemptedReceipts);
            }

            SaleId saleId = new(Guid.NewGuid());
            var cmd = new NewSaleCommand(saleId, Trip, _receiptNumber, _user.Id, now, lines, payments);

            Result<CheckoutOutcome> outcome = _checkout.Checkout(Key, cmd);
            if (outcome.IsFailure)
            {
                bool collided = outcome.Errors.Any(e => e.Code == "DUP_RECEIPT_NUMBER");
                if (collided && _attemptedReceipts.Count < MaxReceiptRetries)
                {
                    // Collision: the append rolled back (idempotency row never
                    // written); re-count under the SAME key — at most once.
                    _attemptedReceipts.Add(_receiptNumber);
                    _receiptNumber = _numbers.Next(now, _attemptedReceipts);
                    cmd = cmd with { ReceiptNumber = _receiptNumber };
                    outcome = _checkout.Checkout(Key, cmd);
                }

                if (outcome.IsFailure)
                {
                    return BackToBuilding(outcome.Errors);
                }
            }

            bool replayed = outcome.Value.Replayed;
            _savedSaleId = outcome.Value.Reference.SaleId;
            _store.Bind();
            Sale? saved = _store.Context.FindSale(_savedSaleId);
            if (saved is null)
            {
                return BackToBuilding(new[] { new DomainError("UNKNOWN_SALE", "the committed sale is gone") });
            }

            _state = CheckoutSessionState.Saved;
            RaiseChanged();
            return Result<SavedSaleState>.Ok(SavedSaleState.From(saved, replayed));
        }
        finally
        {
            if (_state == CheckoutSessionState.Submitting)
            {
                _state = CheckoutSessionState.Building;
            }
        }
    }

    private LocationId TripLocation()
    {
        _store.Bind();
        foreach (Trip trip in _store.Context.Trips)
        {
            if (trip.Id == Trip)
            {
                return trip.Location;
            }
        }

        throw new InvalidOperationException($"session trip {Trip} is not registered");
    }

    private SessionLine? FindLine(SaleLineId lineId)
    {
        foreach (SessionLine line in _lines)
        {
            if (line.LineId == lineId)
            {
                return line;
            }
        }

        return null;
    }

    private void ReplaceLine(SessionLine line)
    {
        int index = FindIndex(line.LineId);
        _lines[index] = line;
    }

    private int FindIndex(SaleLineId lineId)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].LineId == lineId)
            {
                return i;
            }
        }

        return -1;
    }

    private Result<SavedSaleState> BackToBuilding(IReadOnlyList<DomainError> errors)
    {
        _state = CheckoutSessionState.Building;
        RaiseChanged();
        return Result<SavedSaleState>.Fail(errors);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
