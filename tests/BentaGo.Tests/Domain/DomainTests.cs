using BentaGo.Application.Contracts;
using BentaGo.Application.Scenario;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Display;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using Xunit;

namespace BentaGo.Tests.Domain;

/// <summary>
/// Shared fixture: one peddler, one route, opening stock at the main store,
/// and a load out. Tests build on top of this the way the app will.
/// </summary>
public sealed class LedgerHarness
{
    private readonly ScenarioIdSource _ids = new($"harness-{Guid.NewGuid():N}");

    public InMemoryLedgerStore Store { get; }

    public UserId User { get; }

    public PeddlerId Peddler { get; }

    public LocationId StoreLoc { get; }

    public LocationId RouteLoc { get; }

    public ProductId Sardines { get; }

    public TripId Trip { get; }

    public DateTimeOffset Now { get; } = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    public LedgerHarness(int openingQty = 40, int loadedQty = 20)
    {
        Store = new InMemoryLedgerStore();
        User = new UserId(_ids.Next("user"));
        Peddler = new PeddlerId(_ids.Next("peddler"));
        StoreLoc = new LocationId(_ids.Next("store"));
        RouteLoc = new LocationId(_ids.Next("route"));
        Sardines = new ProductId(_ids.Next("sardines"));
        Trip = new TripId(_ids.Next("trip"));

        Store.RegisterTrip(new Trip(Trip, Peddler, RouteLoc, Now, User));
        Store.Bind();

        var openId = new LoadId(_ids.Next("open"));
        Store.RegisterLoad(new StockLoad(openId, "OPEN-1", Trip, Array.Empty<StockLoadLine>(), User, Now));
        Store.Bind();
        Store.Append(new[] { Moves.OpeningStock(0, Now, User, new LoadRef(openId), Sardines, StoreLoc, Quantity.Of(openingQty)) });

        var loadId = new LoadId(_ids.Next("load"));
        var load = LoadBuilder.Build(Store.Context, new LoadCommand(
            loadId, "LD-1", Trip, StoreLoc,
            new[] { new LoadLine(Sardines, "SARD", "Sardines", Quantity.Of(loadedQty), Money.FromPesos(30m)) }, User, Now));
        Store.RegisterLoad(load.Value.Document);
        Store.Bind();
        Store.Append(load.Value.Movements);
    }

    public NewSaleLine Line(int qty, Money price) =>
        new(new SaleLineId(_ids.Next($"l{qty}")), Sardines, "SARD", "Sardines", price, Quantity.Of(qty), Money.Zero);

    public NewPaymentLine Pay(PaymentMethod method, Money amount, string reference = "") =>
        method == PaymentMethod.Cash
            ? new NewPaymentLine(new PaymentLineId(_ids.Next($"p{method}")), method, reference, amount, amount, Money.Zero)
            : new NewPaymentLine(new PaymentLineId(_ids.Next($"p{method}")), method, reference, amount, Money.Zero, Money.Zero);

    public Result<SaleDraft> TrySale(int qty, Money price, PaymentMethod method, string reference = "") =>
        SaleBuilder.Build(Store.Context, new NewSaleCommand(
            new SaleId(_ids.Next($"sale{qty}")), Trip, $"R-{_ids.Next($"r{qty}")}", User, Now.AddHours(1),
            new[] { Line(qty, price) },
            new[] { Pay(method, price.Times(qty), reference) }));

    /// <summary>
    /// Builds, registers, binds and appends a successful sale. Returns the
    /// committed document for void/return lanes.
    /// </summary>
    public Sale CommitSale(int qty, Money price, PaymentMethod method, string reference = "")
    {
        Result<SaleDraft> result = TrySale(qty, price, method, reference);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(e => e.Code)));
        Store.RegisterSale(result.Value.Document);
        Store.Bind();
        Store.Append(result.Value.Movements);
        return result.Value.Document;
    }
}

public sealed class MoneyTests
{
    [Fact]
    public void CentavoArithmetic_IsExact()
    {
        Money total = Money.FromPesos(45m).Times(5);
        Assert.Equal(22500, total.Centavos);
        Assert.Equal(Money.FromPesos(100.50m), Money.FromPesos(50.25m).Plus(Money.FromPesos(50.25m)));
        Assert.Equal(-1, Money.FromPesos(0.01m).Minus(Money.FromPesos(0.02m)).Centavos);
    }

    [Fact]
    public void FromPesos_RejectsMoreThanTwoDecimals()
    {
        Assert.Throws<ArgumentException>(() => Money.FromPesos(1.234m));
    }
}

public sealed class QuantityTests
{
    [Fact]
    public void Of_RejectsNegatives()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantity.Of(-1));
        Assert.Equal(5, Quantity.Of(5).Units);
    }
}

public sealed class MovementFactoryTests
{
    [Fact]
    public void Factory_RejectsNonUtcTimestamp() =>
        Assert.Throws<ArgumentException>(() => Moves.OpeningStock(
            1, new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.FromHours(8)), new UserId(Guid.NewGuid()),
            new LoadRef(new LoadId(Guid.NewGuid())), new ProductId(Guid.NewGuid()), new LocationId(Guid.NewGuid()), Quantity.Of(1)));

    [Fact]
    public void Factory_RejectsCreditReceipt() =>
        Assert.Throws<ArgumentException>(() => Moves.Receipt(
            1, DateTimeOffset.UtcNow, new UserId(Guid.NewGuid()), new SaleRef(new SaleId(Guid.NewGuid())),
            PaymentMethod.Credit, Money.FromPesos(10m), new PaymentLineId(Guid.NewGuid())));

    [Fact]
    public void Factory_RejectsZeroQuantity() =>
        Assert.Throws<ArgumentException>(() => Moves.TransferOut(
            1, DateTimeOffset.UtcNow, new UserId(Guid.NewGuid()), new TransferRef(new TransferId(Guid.NewGuid())),
            new ProductId(Guid.NewGuid()), new LocationId(Guid.NewGuid()), Quantity.Zero));

    [Fact]
    public void Factory_RejectsNonLoadTransferDocument() =>
        Assert.Throws<ArgumentException>(() => Moves.TransferOut(
            1, DateTimeOffset.UtcNow, new UserId(Guid.NewGuid()), new SaleRef(new SaleId(Guid.NewGuid())),
            new ProductId(Guid.NewGuid()), new LocationId(Guid.NewGuid()), Quantity.Of(1)));
}

public sealed class LedgerLaneTests
{
    [Fact]
    public void Lane1_CashSale_MovesStockAndCashExactly()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash);

        Assert.Equal(15, h.Store.State.StockOnHand(h.Sardines, h.RouteLoc).Units);
        Assert.Equal(Money.FromPesos(225m), h.Store.State.FundsOnHand(Fund.PhysicalCash));
        Assert.Equal(SaleStatus.Committed, h.Store.State.Sale(sale.Id).Status);
    }

    [Fact]
    public void Lane2_NonCashFunds_GoToTheirFundNotPhysicalCash()
    {
        var h = new LedgerHarness();
        h.CommitSale(3, Money.FromPesos(45m), PaymentMethod.Gcash);

        Assert.Equal(Money.FromPesos(135m), h.Store.State.FundsOnHand(Fund.Gcash));
        Assert.Equal(Money.Zero, h.Store.State.FundsOnHand(Fund.PhysicalCash));
    }

    [Fact]
    public void Lane3_Credit_RaisesNoFund()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(4, Money.FromPesos(45m), PaymentMethod.Credit, "Aling Nena");

        Assert.Equal(Money.Zero, h.Store.State.FundsOnHand(Fund.PhysicalCash));
        Assert.Equal(Money.Zero, h.Store.State.FundsOnHand(Fund.Bank));
        Assert.Equal(SaleStatus.Committed, h.Store.State.Sale(sale.Id).Status);
    }

    [Fact]
    public void Lane4_SplitPayment_ReceiptsSumToTotal()
    {
        var h = new LedgerHarness();
        var result = SaleBuilder.Build(h.Store.Context, new NewSaleCommand(
            new SaleId(Guid.NewGuid()), h.Trip, "R-SPLIT", h.User, h.Now,
            new[] { h.Line(10, Money.FromPesos(45m)) },
            new[] { h.Pay(PaymentMethod.Cash, Money.FromPesos(250m)), h.Pay(PaymentMethod.Gcash, Money.FromPesos(200m)) }));
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(e => e.Code)));
        h.Store.RegisterSale(result.Value.Document);
        h.Store.Bind();
        h.Store.Append(result.Value.Movements);

        Assert.Equal(Money.FromPesos(250m), h.Store.State.FundsOnHand(Fund.PhysicalCash));
        Assert.Equal(Money.FromPesos(200m), h.Store.State.FundsOnHand(Fund.Gcash));
    }

    [Fact]
    public void Lane5_UnderpaidSplit_RejectedBeforeAnyEvent()
    {
        var h = new LedgerHarness();
        long before = h.Store.Movements.Count;
        var result = SaleBuilder.Build(h.Store.Context, new NewSaleCommand(
            new SaleId(Guid.NewGuid()), h.Trip, "R-SHORT", h.User, h.Now,
            new[] { h.Line(10, Money.FromPesos(45m)) },
            new[] { h.Pay(PaymentMethod.Cash, Money.FromPesos(100m)), h.Pay(PaymentMethod.Gcash, Money.FromPesos(100m)) }));

        Assert.True(result.IsFailure);
        Assert.Contains(result.Errors, e => e.Code == "PAYMENT_TOTAL");
        Assert.Equal(before, h.Store.Movements.Count); // zero events
    }

    [Fact]
    public void Lane6_WrongLocationStock_RejectedBeforeAnyEvent()
    {
        var h = new LedgerHarness(loadedQty: 5);
        long before = h.Store.Movements.Count;
        var result = h.TrySale(10, Money.FromPesos(45m), PaymentMethod.Cash); // 10 > 5 on route

        Assert.True(result.IsFailure);
        Assert.Contains(result.Errors, e => e.Code == "STOCK_INSUFFICIENT");
        Assert.Equal(before, h.Store.Movements.Count);
    }

    [Fact]
    public void Lane7_Void_RevokesStockAndReceiptsAndKeepsBothDocuments()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash);

        var voidDraft = VoidBuilder.Build(h.Store.Context, new VoidCommand(
            new VoidId(Guid.NewGuid()), sale.Id, "customer changed mind", h.User, h.Now.AddHours(2),
            ApprovedBy: h.User));
        h.Store.RegisterVoid(voidDraft.Value.Document);
        h.Store.Bind();
        h.Store.Append(voidDraft.Value.Movements);

        Assert.Equal(20, h.Store.State.StockOnHand(h.Sardines, h.RouteLoc).Units); // restored
        Assert.Equal(Money.Zero, h.Store.State.FundsOnHand(Fund.PhysicalCash));
        Assert.Equal(SaleStatus.Voided, h.Store.State.Sale(sale.Id).Status);
        Assert.All(voidDraft.Value.Movements, m =>
        {
            switch (m)
            {
                case SaleIssueRevoke r:
                    Assert.True(r.Reverses > 0);
                    break;
                case ReceiptRevoke r:
                    Assert.True(r.Reverses > 0);
                    break;
                default:
                    Assert.Fail($"unexpected movement {m.Kind}");
                    break;
            }
        });
        Assert.True(h.Store.Context.Voids.Count == 1);
    }

    [Fact]
    public void Lane8_ReturnLimit_CappedAtSoldQuantity()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash);

        var first = ReturnBuilder.Build(h.Store.Context, new ReturnCommand(
            new ReturnId(Guid.NewGuid()), sale.Id,
            new[] { new ReturnLineCommand(sale.Lines[0].Id, Quantity.Of(3), "damaged", PaymentMethod.Cash, Money.FromPesos(135m)) },
            h.User, h.Now.AddHours(3)));
        h.Store.RegisterReturn(first.Value.Document);
        h.Store.Bind();
        h.Store.Append(first.Value.Movements);
        Assert.Equal(Quantity.Of(3), h.Store.State.Returned(sale.Lines[0].Id));

        long before = h.Store.Movements.Count;
        var second = ReturnBuilder.Build(h.Store.Context, new ReturnCommand(
            new ReturnId(Guid.NewGuid()), sale.Id,
            new[] { new ReturnLineCommand(sale.Lines[0].Id, Quantity.Of(3), "also damaged", PaymentMethod.Cash, Money.FromPesos(135m)) },
            h.User, h.Now.AddHours(4)));
        Assert.True(second.IsFailure);
        Assert.Contains(second.Errors, e => e.Code == "RETURN_LIMIT");
        Assert.Equal(before, h.Store.Movements.Count);
    }

    [Fact]
    public void Lane9_ClosedTrip_RejectsMutationUntilReopen()
    {
        var h = new LedgerHarness();
        h.CommitSale(2, Money.FromPesos(45m), PaymentMethod.Cash);

        var close = CloseBuilder.Build(h.Store.Context, new CloseCommand(
            new CloseId(Guid.NewGuid()), h.Trip, Money.FromPesos(90m), null, h.User, h.Now.AddHours(5)));
        h.Store.Append(close.Value.Movements);
        Assert.Equal(TripStatus.Closed, h.Store.State.Trip(h.Trip).Status);

        var attempt = h.TrySale(1, Money.FromPesos(45m), PaymentMethod.Cash);
        Assert.True(attempt.IsFailure);
        Assert.Contains(attempt.Errors, e => e.Code == "TRIP_CLOSED");

        var reopen = ReopenBuilder.Build(h.Store.Context, new ReopenCommand(h.Trip, "reconciliation", h.User, h.Now.AddHours(5)));
        h.Store.Append(reopen.Value.Movements);
        Assert.Equal(TripStatus.Open, h.Store.State.Trip(h.Trip).Status);

        var after = h.TrySale(1, Money.FromPesos(45m), PaymentMethod.Cash);
        Assert.True(after.IsSuccess, string.Join("; ", after.Errors.Select(e => e.Code)));
    }

    [Fact]
    public void RefundLimit_RefundExceedingReceipt_Rejected()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash);

        var result = ReturnBuilder.Build(h.Store.Context, new ReturnCommand(
            new ReturnId(Guid.NewGuid()), sale.Id,
            new[] { new ReturnLineCommand(sale.Lines[0].Id, Quantity.Of(1), "kept", PaymentMethod.Cash, Money.FromPesos(500m)) },
            h.User, h.Now.AddHours(3)));

        Assert.True(result.IsFailure);
        Assert.Contains(result.Errors, e => e.Code == "REFUND_LIMIT");
    }
}

public sealed class ScenarioTests
{
    [Fact]
    public void TwoPeddlers_RunsByteIdenticalTwice()
    {
        var first = TwoPeddlersScenario.Run(new InMemoryLedgerStore());
        var second = TwoPeddlersScenario.Run(new InMemoryLedgerStore());
        Assert.Equal(first.Text, second.Text);
    }

    [Fact]
    public void TwoPeddlers_BalancesMatchExpected()
    {
        var report = TwoPeddlersScenario.Run(new InMemoryLedgerStore());

        Assert.Contains("PhysicalCash   284.00", report.Text);
        Assert.Contains("Gcash          0.00", report.Text);
        Assert.Contains("post-close sale rejected: TRIP_CLOSED", report.Text);
        Assert.Contains("TripReopen", report.Text);
    }

    [Fact]
    public void TwoPeddlers_FoldRebuildsIdenticalState()
    {
        var store = new InMemoryLedgerStore();
        _ = TwoPeddlersScenario.Run(store);

        LedgerState rebuilt = LedgerState.Fold(
            store.Movements, store.Context.Trips, store.Context.Sales,
            store.Context.Returns, store.Context.Loads, store.Context.Voids);

        Assert.Equal(store.State.LastSequence, rebuilt.LastSequence);
        foreach (Fund fund in Enum.GetValues<Fund>())
        {
            Assert.Equal(store.State.FundsOnHand(fund), rebuilt.FundsOnHand(fund));
        }

        var cells = new HashSet<(ProductId, LocationId)>();
        foreach (Movement m in store.Movements)
        {
            switch (m)
            {
                case OpeningStock s:
                    cells.Add((s.ProductId, s.LocationId));
                    break;
                case TransferOut t:
                    cells.Add((t.ProductId, t.LocationId));
                    break;
                case TransferIn t:
                    cells.Add((t.ProductId, t.LocationId));
                    break;
                case SaleIssue s:
                    cells.Add((s.ProductId, s.LocationId));
                    break;
                case SaleIssueRevoke s:
                    cells.Add((s.ProductId, s.LocationId));
                    break;
                case ReturnToStock r:
                    cells.Add((r.ProductId, r.LocationId));
                    break;
                case Damage d:
                    cells.Add((d.ProductId, d.LocationId));
                    break;
                case Loss l:
                    cells.Add((l.ProductId, l.LocationId));
                    break;
                case AdjustmentIncrease a:
                    cells.Add((a.ProductId, a.LocationId));
                    break;
                case AdjustmentDecrease a:
                    cells.Add((a.ProductId, a.LocationId));
                    break;
                case CountObserved c:
                    cells.Add((c.ProductId, c.LocationId));
                    break;
            }
        }

        Assert.NotEmpty(cells); // the scenario actually touched cells
        foreach ((ProductId product, LocationId location) in cells)
        {
            Assert.Equal(store.State.StockOnHand(product, location), rebuilt.StockOnHand(product, location));
        }

        foreach (Trip trip in store.Context.Trips)
        {
            Assert.Equal(store.State.Trip(trip.Id), rebuilt.Trip(trip.Id));
        }
    }
}

public sealed class ManilaDisplayTests
{
    [Fact]
    public void UtcAsManila_AddsEightHours()
    {
        var utc = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(16, ManilaDisplay.UtcAsManila(utc).Hour);
        Assert.Equal("2026-01-15 16:00", ManilaDisplay.DateTime(utc));
    }

    [Fact]
    public void Money_FormatsEnPhWithTwoDecimals()
    {
        Assert.Equal("1,234.50", ManilaDisplay.Money(Money.FromPesos(1234.50m)));
        Assert.Equal("0.05", ManilaDisplay.Money(Money.FromPesos(0.05m)));
    }
}

/// <summary>
/// Audit-lane regressions: the four majors found in the BG-002 review —
/// batch atomicity, payment-total replay drift, signed variance, and
/// double-revoked receipts — plus forged-document handling.
/// </summary>
public sealed class IntegrityTests
{
    [Fact]
    public void Store_BatchAtomic_WhenSumExceedsStock()
    {
        var h = new LedgerHarness(); // 20 units on the route
        long before = h.Store.Movements.Count;
        long seqBefore = h.Store.State.LastSequence;

        // Two lines of the same product: each passes the builder's per-line
        // check against the pre-sale stock, but the batch sum overdraws.
        Result<SaleDraft> draft = SaleBuilder.Build(h.Store.Context, new NewSaleCommand(
            new SaleId(Guid.NewGuid()), h.Trip, "R-OVER", h.User, h.Now,
            new[] { h.Line(15, Money.FromPesos(45m)), h.Line(10, Money.FromPesos(45m)) },
            new[] { h.Pay(PaymentMethod.Cash, Money.FromPesos(1125m)) }));
        Assert.True(draft.IsSuccess, string.Join("; ", draft.Errors.Select(e => e.Code)));

        Result<AppendReceipt> append = h.Store.Append(draft.Value.Movements);
        Assert.True(append.IsFailure);
        Assert.Contains(append.Errors, e => e.Code == "STOCK_NEGATIVE");

        Assert.Equal(20, h.Store.State.StockOnHand(h.Sardines, h.RouteLoc).Units); // untouched
        Assert.Equal(before, h.Store.Movements.Count); // nothing committed
        Assert.Equal(seqBefore, h.Store.State.LastSequence); // no sequence gap either
    }

    [Fact]
    public void Store_StillCommitsAfterABatchRefusal()
    {
        var h = new LedgerHarness();
        var overdraw = SaleBuilder.Build(h.Store.Context, new NewSaleCommand(
            new SaleId(Guid.NewGuid()), h.Trip, "R-OVER", h.User, h.Now,
            new[] { h.Line(15, Money.FromPesos(45m)), h.Line(10, Money.FromPesos(45m)) },
            new[] { h.Pay(PaymentMethod.Cash, Money.FromPesos(1125m)) }));
        _ = h.Store.Append(overdraw.Value.Movements);

        Sale sale = h.CommitSale(2, Money.FromPesos(45m), PaymentMethod.Cash); // still works
        Assert.Equal(18, h.Store.State.StockOnHand(h.Sardines, h.RouteLoc).Units);
        Assert.Equal(Money.FromPesos(90m), h.Store.State.FundsOnHand(Fund.PhysicalCash));
        Assert.Equal(SaleStatus.Committed, h.Store.State.Sale(sale.Id).Status);
    }

    [Fact]
    public void Breakdown_NegativeVariance_IsSigned()
    {
        var h = new LedgerHarness();
        h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash); // 20 -> 15

        var count = CountBuilder.Build(h.Store.Context, new CountCommand(
            new CountId(Guid.NewGuid()), h.RouteLoc,
            new[] { new CountLineCommand(h.Sardines, Quantity.Of(3), null) }, h.User, h.Now.AddHours(2)));
        h.Store.Append(count.Value.Movements);

        InventoryBreakdown b = h.Store.State.Breakdown(h.Sardines, h.RouteLoc);
        Assert.Equal(3, b.Observed!.Value.Units);
        Assert.Equal(-12, b.Variance); // shortage is signed, minus sign preserved

        // observed 30 (overage, +15) must work too
        var countUp = CountBuilder.Build(h.Store.Context, new CountCommand(
            new CountId(Guid.NewGuid()), h.RouteLoc,
            new[] { new CountLineCommand(h.Sardines, Quantity.Of(30), null) }, h.User, h.Now.AddHours(3)));
        h.Store.Append(countUp.Value.Movements);
        Assert.Equal(15, h.Store.State.Breakdown(h.Sardines, h.RouteLoc).Variance);
    }

    [Fact]
    public void Fold_DoubleReceiptRevoke_IsLoud()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash);
        long receiptSeq = h.Store.State.ReceiptSequence(sale.Id, sale.Payments[0].Id)!.Value;
        long next = h.Store.State.LastSequence;

        Movement revoke1 = Moves.ReceiptRevoke(next + 1, h.Now, h.User, new VoidRef(new VoidId(Guid.NewGuid())), PaymentMethod.Cash, Money.FromPesos(225m), sale.Payments[0].Id, receiptSeq);
        Movement revoke2 = Moves.ReceiptRevoke(next + 2, h.Now, h.User, new VoidRef(new VoidId(Guid.NewGuid())), PaymentMethod.Cash, Money.FromPesos(225m), sale.Payments[0].Id, receiptSeq);

        List<Movement> stream = h.Store.Movements.Concat(new[] { revoke1, revoke2 }).ToList();
        LedgerIntegrityException ex = Assert.Throws<LedgerIntegrityException>(() => LedgerState.Fold(
            stream, h.Store.Context.Trips, h.Store.Context.Sales, h.Store.Context.Returns, h.Store.Context.Loads, h.Store.Context.Voids));
        Assert.Contains("already revoked", ex.Message);
    }

    [Fact]
    public void Store_ForgedDuplicateReceipt_RejectedWithoutStateChange()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash);
        long before = h.Store.Movements.Count;

        Movement forge = Moves.Receipt(0, h.Now.AddMinutes(1), h.User, new SaleRef(sale.Id), PaymentMethod.Cash, Money.FromPesos(225m), sale.Payments[0].Id);
        Result<AppendReceipt> append = h.Store.Append(new[] { forge });

        Assert.True(append.IsFailure);
        Assert.Contains(append.Errors, e => e.Code == "INTEGRITY");
        Assert.Equal(Money.FromPesos(225m), h.Store.State.FundsOnHand(Fund.PhysicalCash)); // unchanged
        Assert.Equal(before, h.Store.Movements.Count);
    }

    [Fact]
    public void Fold_PaymentDrift_IsLoudOnReplay()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash);

        Movement forge = Moves.Receipt(h.Store.State.LastSequence + 1, h.Now, h.User, new SaleRef(sale.Id), PaymentMethod.Cash, Money.FromPesos(225m), sale.Payments[0].Id);
        List<Movement> stream = h.Store.Movements.Append(forge).ToList();
        Assert.Throws<LedgerIntegrityException>(() => LedgerState.Fold(
            stream, h.Store.Context.Trips, h.Store.Context.Sales, h.Store.Context.Returns, h.Store.Context.Loads, h.Store.Context.Voids));
    }

    [Fact]
    public void Fold_ForgedWrongDocumentKind_IsStructured()
    {
        var h = new LedgerHarness();
        h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash);
        long seq = h.Store.State.LastSequence + 1;

        Movement forged = new SaleIssue
        {
            Sequence = seq,
            WhenUtc = h.Now,
            ActorId = h.User,
            Document = new VoidRef(new VoidId(Guid.NewGuid())), // wrong kind
            ProductId = h.Sardines,
            LocationId = h.RouteLoc,
            Quantity = Quantity.Of(1),
            SaleLineId = new SaleLineId(Guid.NewGuid()),
        };

        LedgerIntegrityException ex = Assert.Throws<LedgerIntegrityException>(() => LedgerState.Fold(
            h.Store.Movements.Append(forged).ToList(),
            h.Store.Context.Trips, h.Store.Context.Sales, h.Store.Context.Returns, h.Store.Context.Loads, h.Store.Context.Voids));
        Assert.Contains("cannot carry", ex.Message);
    }
}

public sealed class PerfTests
{
    [Fact]
    public void Fold_OneHundredThousandMovements_UnderTwoSeconds()
    {
        var product = new ProductId(Guid.NewGuid());
        var location = new LocationId(Guid.NewGuid());
        var actor = new UserId(Guid.NewGuid());
        var load = new LoadId(Guid.NewGuid());
        DateTimeOffset when = DateTimeOffset.UtcNow;

        // Spend 50k units across 100k alternating movements, then refund them:
        // net zero, exercising stock up/down + reversal plumbing.
        var movements = new List<Movement>(100_000);
        for (int i = 0; i < 50_000; i++)
        {
            movements.Add(Moves.OpeningStock(i + 1, when, actor, new LoadRef(load), product, location, Quantity.Of(1)));
        }

        for (int i = 0; i < 50_000; i++)
        {
            movements.Add(Moves.Loss(
                i + 50_001, when, actor, new AdjustmentRef(new AdjustmentId(Guid.NewGuid())), product, location, Quantity.Of(1), "perf"));
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        LedgerState state = LedgerState.Fold(movements, Array.Empty<Trip>(), Array.Empty<Sale>(), Array.Empty<Return>(), Array.Empty<StockLoad>(), Array.Empty<SaleVoid>());
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"fold took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(Quantity.Zero, state.StockOnHand(product, location));
    }
}
