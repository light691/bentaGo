using BentaGo.Application.Contracts;
using BentaGo.Application.Scenario;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using Xunit;

namespace BentaGo.Tests.Domain;

/// <summary>
/// The ADR-0004 expected-cash fold (docs/decisions/0004 §1) at the unit
/// boundary: expected = opening float + cash sales − cash refunds/revoked
/// cash − approved cash expenses (FundOut) + cash pay-downs received
/// (FundIn), each term day-keyed in the <see cref="LedgerState"/> fold and
/// recorded verbatim on the <see cref="DayClose"/> document.
/// </summary>
public sealed class DayFoldMathTests
{
    private static readonly DateTimeOffset When = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static DayKey Day => DayKey.FromUtc(When);

    /// <summary>
    /// The program acceptance proof: open with a P5,000.00 float, sell
    /// P120.00 cash, pay out a P50.00 approved cash expense, and the close
    /// expects 5,070.00 and reads BALANCED when the drawer counts it.
    /// </summary>
    [Fact]
    public void FloatPlusCashSaleMinusExpense_IsTheExpectedCash_AndCloseIsBalanced()
    {
        var ids = new ScenarioIdSource("day-fold-acceptance-v1");
        var store = new InMemoryLedgerStore();
        var user = new UserId(ids.Next("user"));
        var approver = new UserId(ids.Next("approver"));
        var sardines = new ProductId(ids.Next("sardines"));

        // Open the day with a 5000.00 float.
        store.RegisterDayOpen(new DayOpen(new DayId(ids.Next("day")), Day, 500_000, user, When));
        store.Bind();

        // Receive stock so the sale can commit, then sell 120.00 cash.
        Result<GoodsReceiptDraft> goods = GoodsReceiptBuilder.Build(store.Context, new GoodsReceiptCommand(
            new GoodsId(ids.Next("goods")), Day,
            new[] { new GoodsReceiptLineCommand(sardines, "SARD", "Sardines", Quantity.Of(50), Money.FromPesos(30m)) },
            user, When));
        Assert.True(goods.IsSuccess, goods.Errors.FirstOrDefault()?.Code ?? "goods build failed");
        Assert.True(store.Append(goods.Value.Movements).IsSuccess, "goods append failed");

        var lineId = new SaleLineId(ids.Next("line"));
        var paymentId = new PaymentLineId(ids.Next("payment"));
        Result<SaleDraft> sale = SaleBuilder.Build(store.Context, new NewSaleCommand(
            new SaleId(ids.Next("sale")), Day, "R-000001", user, When.AddHours(1),
            new[] { new NewSaleLine(lineId, sardines, "SARD", "Sardines", Money.FromPesos(120m), Quantity.Of(1), Money.Zero) },
            new[] { new NewPaymentLine(paymentId, PaymentMethod.Cash, "", Money.FromPesos(120m), Money.FromPesos(120m), Money.Zero) }));
        Assert.True(sale.IsSuccess, sale.Errors.FirstOrDefault()?.Code ?? "sale build failed");
        store.RegisterSale(sale.Value.Document);
        store.Bind();
        Assert.True(store.Append(sale.Value.Movements).IsSuccess, "sale append failed");

        // Pay out a 50.00 approved cash expense (FundOut).
        Result<FundEntryDraft> expense = FundEntryBuilder.Build(store.Context, new FundEntryCommand(
            new FundEntryId(ids.Next("fund-out")), Day, MovementKind.FundOut, PaymentMethod.Cash,
            Money.FromPesos(50m), "tricycle fare", user, When.AddHours(2), ApprovedBy: approver));
        Assert.True(expense.IsSuccess, expense.Errors.FirstOrDefault()?.Code ?? "fund out build failed");
        Assert.True(store.Append(expense.Value.Movements).IsSuccess, "fund out append failed");

        // The fold: 5000.00 float + 120.00 cash sale − 50.00 expense.
        Assert.Equal(507_000, store.State.ExpectedCash(Day));

        // The close records the fold's expected, and counting the drawer at
        // exactly that amount labels the day BALANCED.
        Result<DayCloseDraft> close = DayBuilder.Close(store.Context, new DayCloseCommand(
            new CloseId(ids.Next("close")), Day, CountedCashCentavos: 507_000, "balanced day", user, When.AddHours(3)));
        Assert.True(close.IsSuccess, close.Errors.FirstOrDefault()?.Code ?? "close build failed");
        Assert.Equal(507_000, close.Value.Document.ExpectedCashCentavos);
        Assert.Equal(0, close.Value.Document.DifferenceCentavos);
        Assert.Equal(DayBalanceLabel.Balanced, close.Value.Document.Label);
    }

    [Fact]
    public void CashRefund_AndVoidRevoke_ReduceExpected_WhileGcashNeverTouchesIt()
    {
        var ids = new ScenarioIdSource("day-fold-mixed-v1");
        var store = new InMemoryLedgerStore();
        var user = new UserId(ids.Next("user"));
        var approver = new UserId(ids.Next("approver"));
        var sardines = new ProductId(ids.Next("sardines"));

        store.RegisterDayOpen(new DayOpen(new DayId(ids.Next("day")), Day, 100_000, user, When));
        store.Bind();

        Result<GoodsReceiptDraft> goods = GoodsReceiptBuilder.Build(store.Context, new GoodsReceiptCommand(
            new GoodsId(ids.Next("goods")), Day,
            new[] { new GoodsReceiptLineCommand(sardines, "SARD", "Sardines", Quantity.Of(50), Money.FromPesos(30m)) },
            user, When));
        Assert.True(store.Append(goods.Value.Movements).IsSuccess, "goods append failed");

        // +225.00 cash.
        Sale cashSale = CommitSale(store, ids, user, "R-000001",
            new[] { new NewSaleLine(new SaleLineId(ids.Next("l1")), sardines, "SARD", "Sardines", Money.FromPesos(45m), Quantity.Of(5), Money.Zero) },
            new[] { new NewPaymentLine(new PaymentLineId(ids.Next("p1")), PaymentMethod.Cash, "", Money.FromPesos(225m), Money.FromPesos(225m), Money.Zero) });
        Assert.Equal(122_500, store.State.ExpectedCash(Day));

        // +100.00 GCash: a wallet sale never enters the drawer fold.
        _ = CommitSale(store, ids, user, "R-000002",
            new[] { new NewSaleLine(new SaleLineId(ids.Next("l2")), sardines, "SARD", "Sardines", Money.FromPesos(50m), Quantity.Of(2), Money.Zero) },
            new[] { new NewPaymentLine(new PaymentLineId(ids.Next("p2")), PaymentMethod.Gcash, "G-1", Money.FromPesos(100m), Money.Zero, Money.Zero) });
        Assert.Equal(122_500, store.State.ExpectedCash(Day));

        // A voided cash sale hands the 45.00 back: the cash side of the fold
        // drops via the ReceiptRevoke (voids consume the sale entirely).
        Sale voidable = CommitSale(store, ids, user, "R-000003",
            new[] { new NewSaleLine(new SaleLineId(ids.Next("l3")), sardines, "SARD", "Sardines", Money.FromPesos(45m), Quantity.Of(1), Money.Zero) },
            new[] { new NewPaymentLine(new PaymentLineId(ids.Next("p3")), PaymentMethod.Cash, "", Money.FromPesos(45m), Money.FromPesos(45m), Money.Zero) });
        Assert.Equal(127_000, store.State.ExpectedCash(Day));
        Result<VoidDraft> voidDraft = VoidBuilder.Build(store.Context, new VoidCommand(
            new VoidId(ids.Next("void")), voidable.Id, "customer changed mind", user, When.AddHours(2), ApprovedBy: approver));
        Assert.True(voidDraft.IsSuccess, voidDraft.Errors.FirstOrDefault()?.Code ?? "void build failed");
        store.RegisterVoid(voidDraft.Value.Document);
        store.Bind();
        Assert.True(store.Append(voidDraft.Value.Movements).IsSuccess, "void append failed");
        Assert.Equal(122_500, store.State.ExpectedCash(Day));

        // A 45.00 cash refund on the first sale: −45.00 from the day fold.
        Result<ReturnDraft> returnDraft = ReturnBuilder.Build(store.Context, new ReturnCommand(
            new ReturnId(ids.Next("return")), cashSale.Id,
            new[] { new ReturnLineCommand(cashSale.Lines[0].Id, Quantity.Of(1), "damaged can", PaymentMethod.Cash, Money.FromPesos(45m)) },
            user, When.AddHours(3)));
        Assert.True(returnDraft.IsSuccess, returnDraft.Errors.FirstOrDefault()?.Code ?? "return build failed");
        store.RegisterReturn(returnDraft.Value.Document);
        store.Bind();
        Assert.True(store.Append(returnDraft.Value.Movements).IsSuccess, "return append failed");
        Assert.Equal(118_000, store.State.ExpectedCash(Day));

        // A 60.00 cash pay-down received (FundIn): +60.00 to the day fold.
        Result<FundEntryDraft> paydown = FundEntryBuilder.Build(store.Context, new FundEntryCommand(
            new FundEntryId(ids.Next("fund-in")), Day, MovementKind.FundIn, PaymentMethod.Cash,
            Money.FromPesos(60m), "pay-down from Aling Nena", user, When.AddHours(4), ApprovedBy: approver));
        Assert.True(paydown.IsSuccess, paydown.Errors.FirstOrDefault()?.Code ?? "fund in build failed");
        Assert.True(store.Append(paydown.Value.Movements).IsSuccess, "fund in append failed");
        Assert.Equal(124_000, store.State.ExpectedCash(Day));

        // The full formula, one close: 100 + 225 − 45(refund) + 60 = 340.00.
        Result<DayCloseDraft> close = DayBuilder.Close(store.Context, new DayCloseCommand(
            new CloseId(ids.Next("close")), Day, CountedCashCentavos: 124_000, "balanced", user, When.AddHours(5)));
        Assert.True(close.IsSuccess, close.Errors.FirstOrDefault()?.Code ?? "close build failed");
        Assert.Equal(124_000, close.Value.Document.ExpectedCashCentavos);
        Assert.Equal(DayBalanceLabel.Balanced, close.Value.Document.Label);
    }

    private static Sale CommitSale(
        InMemoryLedgerStore store, ScenarioIdSource ids, UserId user, string receipt,
        NewSaleLine[] lines, NewPaymentLine[] payments)
    {
        Result<SaleDraft> draft = SaleBuilder.Build(store.Context, new NewSaleCommand(
            new SaleId(ids.Next($"sale-{receipt}")), Day, receipt, user, When.AddHours(1), lines, payments));
        Assert.True(draft.IsSuccess, draft.Errors.FirstOrDefault()?.Code ?? $"{receipt} build failed");
        store.RegisterSale(draft.Value.Document);
        store.Bind();
        Assert.True(store.Append(draft.Value.Movements).IsSuccess, $"{receipt} append failed");
        return draft.Value.Document;
    }
}
