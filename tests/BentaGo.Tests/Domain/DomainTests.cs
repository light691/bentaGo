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
/// Shared fixture: one open trading day, one product with opening stock at the
/// single warehouse. Tests build on top of this the way the app will.
/// </summary>
public sealed class LedgerHarness
{
    private readonly ScenarioIdSource _ids = new($"harness-{Guid.NewGuid():N}");

    public InMemoryLedgerStore Store { get; }

    public UserId User { get; }

    public DayKey Day { get; }

    public ProductId Sardines { get; }

    public DateTimeOffset Now { get; } = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    public LedgerHarness(int openingQty = 40)
    {
        Store = new InMemoryLedgerStore();
        User = new UserId(_ids.Next("user"));
        Sardines = new ProductId(_ids.Next("sardines"));
        Day = DayKey.FromUtc(Now);

        // Open the day (float P100 so the close math has an expected to beat),
        // then receive the opening stock into the warehouse on the open day.
        Store.RegisterDayOpen(new DayOpen(new DayId(_ids.Next("day")), Day, 10_000, User, Now));
        Store.Bind();

        var goodsId = new GoodsId(_ids.Next("goods"));
        Result<GoodsReceiptDraft> goods = GoodsReceiptBuilder.Build(Store.Context, new GoodsReceiptCommand(
            goodsId, Day,
            new[] { new GoodsReceiptLineCommand(Sardines, "SARD", "Sardines", Quantity.Of(openingQty), Money.FromPesos(30m)) },
            User, Now));
        Assert.True(goods.IsSuccess, string.Join("; ", goods.Errors.Select(e => e.Code)));
        Store.Append(goods.Value.Movements);
    }

    public NewSaleLine Line(int qty, Money price) =>
        new(new SaleLineId(_ids.Next($"l{qty}")), Sardines, "SARD", "Sardines", price, Quantity.Of(qty), Money.Zero);

    public NewPaymentLine Pay(PaymentMethod method, Money amount, string reference = "") =>
        method == PaymentMethod.Cash
            ? new NewPaymentLine(new PaymentLineId(_ids.Next($"p{method}")), method, reference, amount, amount, Money.Zero)
            : new NewPaymentLine(new PaymentLineId(_ids.Next($"p{method}")), method, reference, amount, Money.Zero, Money.Zero);

    public Result<SaleDraft> TrySale(int qty, Money price, PaymentMethod method, string reference = "") =>
        SaleBuilder.Build(Store.Context, new NewSaleCommand(
            new SaleId(_ids.Next($"sale{qty}")), Day, $"R-{_ids.Next($"r{qty}")}", User, Now.AddHours(1),
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

public sealed class DayKeyTests
{
    [Fact]
    public void FromUtc_ConvertsToManilaDay()
    {
        // 2026-01-15 16:30 UTC is 2026-01-16 00:30 in Asia/Manila.
        var utc = new DateTimeOffset(2026, 1, 15, 16, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DayKey("2026-01-16"), DayKey.FromUtc(utc));
        Assert.Equal("2026-01-16", DayKey.FromUtc(utc).Value);
    }

    [Fact]
    public void FromUtc_SameManilaDay_ForMorningUtc()
    {
        var utc = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DayKey("2026-01-15"), DayKey.FromUtc(utc));
    }
}

public sealed class MovementFactoryTests
{
    [Fact]
    public void Factory_RejectsNonUtcTimestamp() =>
        Assert.Throws<ArgumentException>(() => Moves.GoodsReceived(
            1, new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.FromHours(8)), new UserId(Guid.NewGuid()),
            new GoodsRef(new GoodsId(Guid.NewGuid())), new ProductId(Guid.NewGuid()),
            StoreLocations.Warehouse, Quantity.Of(1)));

    [Fact]
    public void Factory_RejectsCreditReceipt() =>
        Assert.Throws<ArgumentException>(() => Moves.Receipt(
            1, DateTimeOffset.UtcNow, new UserId(Guid.NewGuid()), new SaleRef(new SaleId(Guid.NewGuid())),
            PaymentMethod.Credit, Money.FromPesos(10m), new PaymentLineId(Guid.NewGuid())));

    [Fact]
    public void Factory_RejectsCreditFundMovement() =>
        Assert.Throws<ArgumentException>(() => Moves.FundIn(
            1, DateTimeOffset.UtcNow, new UserId(Guid.NewGuid()), new FundRef(new FundEntryId(Guid.NewGuid())),
            PaymentMethod.Credit, Money.FromPesos(10m), "pay-down"));

    [Fact]
    public void Factory_RejectsZeroQuantity() =>
        Assert.Throws<ArgumentException>(() => Moves.GoodsReceived(
            1, DateTimeOffset.UtcNow, new UserId(Guid.NewGuid()), new GoodsRef(new GoodsId(Guid.NewGuid())),
            new ProductId(Guid.NewGuid()), StoreLocations.Warehouse, Quantity.Zero));
}

public sealed class LedgerLaneTests
{
    [Fact]
    public void Lane1_CashSale_MovesStockAndCashExactly()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash);

        Assert.Equal(35, h.Store.State.StockOnHand(h.Sardines, StoreLocations.Warehouse).Units);
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
            new SaleId(Guid.NewGuid()), h.Day, "R-SPLIT", h.User, h.Now,
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
            new SaleId(Guid.NewGuid()), h.Day, "R-SHORT", h.User, h.Now,
            new[] { h.Line(10, Money.FromPesos(45m)) },
            new[] { h.Pay(PaymentMethod.Cash, Money.FromPesos(100m)), h.Pay(PaymentMethod.Gcash, Money.FromPesos(100m)) }));

        Assert.True(result.IsFailure);
        Assert.Contains(result.Errors, e => e.Code == "PAYMENT_TOTAL");
        Assert.Equal(before, h.Store.Movements.Count); // zero events
    }

    [Fact]
    public void Lane6_NotEnoughStock_RejectedBeforeAnyEvent()
    {
        var h = new LedgerHarness(openingQty: 5);
        long before = h.Store.Movements.Count;
        var result = h.TrySale(10, Money.FromPesos(45m), PaymentMethod.Cash); // 10 > 5 on hand

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

        Assert.Equal(40, h.Store.State.StockOnHand(h.Sardines, StoreLocations.Warehouse).Units); // restored
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
    public void Lane9_ClosedDay_RejectsMutationUntilReopen()
    {
        var h = new LedgerHarness();
        h.CommitSale(2, Money.FromPesos(45m), PaymentMethod.Cash);

        var close = DayBuilder.Close(h.Store.Context, new DayCloseCommand(
            new CloseId(Guid.NewGuid()), h.Day, CountedCashCentavos: 10_000, null, h.User, h.Now.AddHours(5)));
        Assert.True(close.IsSuccess, string.Join("; ", close.Errors.Select(e => e.Code)));
        h.Store.RegisterDayClose(close.Value.Document);
        h.Store.Bind();
        Assert.Equal(DayLifecycleState.Closed, h.Store.State.Day(h.Day));

        var attempt = h.TrySale(1, Money.FromPesos(45m), PaymentMethod.Cash);
        Assert.True(attempt.IsFailure);
        Assert.Contains(attempt.Errors, e => e.Code == "DAY_NOT_OPEN");

        var reopen = DayBuilder.Reopen(h.Store.Context, new DayReopenCommand(
            new DayId(Guid.NewGuid()), h.Day, "reconciliation", h.User, h.User, h.Now.AddHours(5)));
        Assert.True(reopen.IsSuccess, string.Join("; ", reopen.Errors.Select(e => e.Code)));
        h.Store.RegisterDayReopen(reopen.Value.Document);
        h.Store.Bind();
        Assert.Equal(DayLifecycleState.Open, h.Store.State.Day(h.Day));

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

/// <summary>
/// Day lifecycle rules (BG-P1a): the DAY_NOT_OPEN gate and the close verdict
/// label math (expected minus counted; zero balanced, positive shortage,
/// negative overage).
/// </summary>
public sealed class DayLifecycleTests
{
    [Fact]
    public void Sale_OnANewDay_IsRejectedWithDayNotOpen()
    {
        var h = new LedgerHarness();
        var freshDay = new DayKey("2030-06-01");

        Result<SaleDraft> attempt = SaleBuilder.Build(h.Store.Context, new NewSaleCommand(
            new SaleId(Guid.NewGuid()), freshDay, "R-FRESH", h.User, h.Now,
            new[] { h.Line(1, Money.FromPesos(45m)) },
            new[] { h.Pay(PaymentMethod.Cash, Money.FromPesos(45m)) }));

        Assert.True(attempt.IsFailure);
        Assert.Contains(attempt.Errors, e => e.Code == "DAY_NOT_OPEN");
    }

    [Fact]
    public void DayClose_LabelMath_IsExpectedMinusCounted()
    {
        var h = new LedgerHarness(); // float 100.00 recorded by the day open
        var balanced = DayBuilder.Close(h.Store.Context, new DayCloseCommand(
            new CloseId(Guid.NewGuid()), h.Day, CountedCashCentavos: 10_000, null, h.User, h.Now.AddHours(1)));
        Assert.Equal(DayBalanceLabel.Balanced, balanced.Value.Document.Label);
        Assert.Equal(10_000, balanced.Value.Document.ExpectedCashCentavos);
        Assert.Equal(0, balanced.Value.Document.DifferenceCentavos);

        var shortage = DayBuilder.Close(h.Store.Context, new DayCloseCommand(
            new CloseId(Guid.NewGuid()), h.Day, CountedCashCentavos: 9_000, null, h.User, h.Now.AddHours(1)));
        Assert.Equal(DayBalanceLabel.Shortage, shortage.Value.Document.Label);
        Assert.Equal(1_000, shortage.Value.Document.DifferenceCentavos);

        var overage = DayBuilder.Close(h.Store.Context, new DayCloseCommand(
            new CloseId(Guid.NewGuid()), h.Day, CountedCashCentavos: 12_000, null, h.User, h.Now.AddHours(1)));
        Assert.Equal(DayBalanceLabel.Overage, overage.Value.Document.Label);
        Assert.Equal(-2_000, overage.Value.Document.DifferenceCentavos);
    }

    [Fact]
    public void FundIn_And_FundOut_MoveTheDrawer()
    {
        var h = new LedgerHarness();

        Result<FundEntryDraft> fundIn = FundEntryBuilder.Build(h.Store.Context, new FundEntryCommand(
            new FundEntryId(Guid.NewGuid()), h.Day, MovementKind.FundIn, PaymentMethod.Cash,
            Money.FromPesos(500m), "pay-down received", h.User, h.Now.AddMinutes(30)));
        Assert.True(fundIn.IsSuccess, string.Join("; ", fundIn.Errors.Select(e => e.Code)));
        h.Store.Append(fundIn.Value.Movements);
        Assert.Equal(Money.FromPesos(500m), h.Store.State.FundsOnHand(Fund.PhysicalCash));

        Result<FundEntryDraft> fundOut = FundEntryBuilder.Build(h.Store.Context, new FundEntryCommand(
            new FundEntryId(Guid.NewGuid()), h.Day, MovementKind.FundOut, PaymentMethod.Cash,
            Money.FromPesos(200m), "tricycle fare", h.User, h.Now.AddMinutes(40), ApprovedBy: h.User));
        Assert.True(fundOut.IsSuccess, string.Join("; ", fundOut.Errors.Select(e => e.Code)));
        h.Store.Append(fundOut.Value.Movements);
        Assert.Equal(Money.FromPesos(300m), h.Store.State.FundsOnHand(Fund.PhysicalCash));

        // A fund out without an approver is rejected before any movement.
        Result<FundEntryDraft> unapproved = FundEntryBuilder.Build(h.Store.Context, new FundEntryCommand(
            new FundEntryId(Guid.NewGuid()), h.Day, MovementKind.FundOut, PaymentMethod.Cash,
            Money.FromPesos(10m), "no approver", h.User, h.Now.AddMinutes(50)));
        Assert.True(unapproved.IsFailure);
        Assert.Contains(unapproved.Errors, e => e.Code == "FUND_OUT_APPROVAL_REQUIRED");

        // Overdrawing the drawer is loud at append time.
        Result<FundEntryDraft> overdraw = FundEntryBuilder.Build(h.Store.Context, new FundEntryCommand(
            new FundEntryId(Guid.NewGuid()), h.Day, MovementKind.FundOut, PaymentMethod.Cash,
            Money.FromPesos(1_000m), "too much", h.User, h.Now.AddMinutes(60), ApprovedBy: h.User));
        Assert.True(overdraw.IsSuccess);
        Result<AppendReceipt> append = h.Store.Append(overdraw.Value.Movements);
        Assert.True(append.IsFailure);
        Assert.Contains(append.Errors, e => e.Code == "FUND_NEGATIVE");
    }

    [Fact]
    public void GoodsReceived_StockAndRefold_Agree()
    {
        var h = new LedgerHarness();
        var product = new ProductId(Guid.NewGuid());

        Result<GoodsReceiptDraft> goods = GoodsReceiptBuilder.Build(h.Store.Context, new GoodsReceiptCommand(
            new GoodsId(Guid.NewGuid()), h.Day,
            new[] { new GoodsReceiptLineCommand(product, "KOPI", "Kopi", Quantity.Of(7), Money.FromPesos(50m)) },
            h.User, h.Now.AddMinutes(15)));
        Assert.True(goods.IsSuccess, string.Join("; ", goods.Errors.Select(e => e.Code)));
        h.Store.Append(goods.Value.Movements);
        Assert.Equal(Quantity.Of(7), h.Store.State.StockOnHand(product, StoreLocations.Warehouse));

        LedgerState rebuilt = LedgerState.Fold(
            h.Store.Movements, h.Store.Context.Sales, h.Store.Context.Returns, h.Store.Context.Voids);
        Assert.Equal(Quantity.Of(7), rebuilt.StockOnHand(product, StoreLocations.Warehouse));
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
        var h = new LedgerHarness(openingQty: 20); // 20 units on hand
        long before = h.Store.Movements.Count;
        long seqBefore = h.Store.State.LastSequence;

        // Two lines of the same product: each passes the builder's per-line
        // check against the pre-sale stock, but the batch sum overdraws.
        Result<SaleDraft> draft = SaleBuilder.Build(h.Store.Context, new NewSaleCommand(
            new SaleId(Guid.NewGuid()), h.Day, "R-OVER", h.User, h.Now,
            new[] { h.Line(15, Money.FromPesos(45m)), h.Line(10, Money.FromPesos(45m)) },
            new[] { h.Pay(PaymentMethod.Cash, Money.FromPesos(1125m)) }));
        Assert.True(draft.IsSuccess, string.Join("; ", draft.Errors.Select(e => e.Code)));

        Result<AppendReceipt> append = h.Store.Append(draft.Value.Movements);
        Assert.True(append.IsFailure);
        Assert.Contains(append.Errors, e => e.Code == "STOCK_NEGATIVE");

        Assert.Equal(20, h.Store.State.StockOnHand(h.Sardines, StoreLocations.Warehouse).Units); // untouched
        Assert.Equal(before, h.Store.Movements.Count); // nothing committed
        Assert.Equal(seqBefore, h.Store.State.LastSequence); // no sequence gap either
    }

    [Fact]
    public void Store_StillCommitsAfterABatchRefusal()
    {
        var h = new LedgerHarness(openingQty: 20);
        var overdraw = SaleBuilder.Build(h.Store.Context, new NewSaleCommand(
            new SaleId(Guid.NewGuid()), h.Day, "R-OVER", h.User, h.Now,
            new[] { h.Line(15, Money.FromPesos(45m)), h.Line(10, Money.FromPesos(45m)) },
            new[] { h.Pay(PaymentMethod.Cash, Money.FromPesos(1125m)) }));
        _ = h.Store.Append(overdraw.Value.Movements);

        Sale sale = h.CommitSale(2, Money.FromPesos(45m), PaymentMethod.Cash); // still works
        Assert.Equal(18, h.Store.State.StockOnHand(h.Sardines, StoreLocations.Warehouse).Units);
        Assert.Equal(Money.FromPesos(90m), h.Store.State.FundsOnHand(Fund.PhysicalCash));
        Assert.Equal(SaleStatus.Committed, h.Store.State.Sale(sale.Id).Status);
    }

    [Fact]
    public void CountObserved_RecordsTheObservedQuantity()
    {
        var h = new LedgerHarness();
        h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash); // 40 -> 35

        var count = CountBuilder.Build(h.Store.Context, new CountCommand(
            new CountId(Guid.NewGuid()), StoreLocations.Warehouse,
            new[] { new CountLineCommand(h.Sardines, Quantity.Of(3), null) }, h.User, h.Now.AddHours(2)));
        h.Store.Append(count.Value.Movements);
        Assert.Equal(3, h.Store.State.ObservedUnits(h.Sardines, StoreLocations.Warehouse).Units);

        // observed 30 (overage) must overwrite the earlier observation too
        var countUp = CountBuilder.Build(h.Store.Context, new CountCommand(
            new CountId(Guid.NewGuid()), StoreLocations.Warehouse,
            new[] { new CountLineCommand(h.Sardines, Quantity.Of(30), null) }, h.User, h.Now.AddHours(3)));
        h.Store.Append(countUp.Value.Movements);
        Assert.Equal(30, h.Store.State.ObservedUnits(h.Sardines, StoreLocations.Warehouse).Units);
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
            stream, h.Store.Context.Sales, h.Store.Context.Returns, h.Store.Context.Voids));
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
            stream, h.Store.Context.Sales, h.Store.Context.Returns, h.Store.Context.Voids));
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
            LocationId = StoreLocations.Warehouse,
            Quantity = Quantity.Of(1),
            SaleLineId = new SaleLineId(Guid.NewGuid()),
        };

        LedgerIntegrityException ex = Assert.Throws<LedgerIntegrityException>(() => LedgerState.Fold(
            h.Store.Movements.Append(forged).ToList(),
            h.Store.Context.Sales, h.Store.Context.Returns, h.Store.Context.Voids));
        Assert.Contains("cannot carry", ex.Message);
    }
}

public sealed class PerfTests
{
    [Fact]
    public void Fold_OneHundredThousandMovements_UnderTwoSeconds()
    {
        var product = new ProductId(Guid.NewGuid());
        var location = StoreLocations.Warehouse;
        var actor = new UserId(Guid.NewGuid());
        var goods = new GoodsId(Guid.NewGuid());
        DateTimeOffset when = DateTimeOffset.UtcNow;

        // Spend 50k units across 100k alternating movements, then lose them:
        // net zero, exercising stock up/down + fold plumbing.
        var movements = new List<Movement>(100_000);
        for (int i = 0; i < 50_000; i++)
        {
            movements.Add(Moves.GoodsReceived(i + 1, when, actor, new GoodsRef(goods), product, location, Quantity.Of(1)));
        }

        for (int i = 0; i < 50_000; i++)
        {
            movements.Add(Moves.Loss(
                i + 50_001, when, actor, new AdjustmentRef(new AdjustmentId(Guid.NewGuid())), product, location, Quantity.Of(1), "perf"));
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        LedgerState state = LedgerState.Fold(movements, Array.Empty<Sale>(), Array.Empty<Return>(), Array.Empty<SaleVoid>());
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"fold took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(Quantity.Zero, state.StockOnHand(product, location));
    }
}
