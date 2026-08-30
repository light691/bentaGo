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
/// BG-007 corrections rules (docs/05 407-428): the approval policy, accepted
/// role gates, return destinations (peddler stock vs main store), damage and
/// loss as distinct kinds, and the closed-trip reopen requirement.
/// </summary>
public sealed class Bg007CorrectionsTests
{
    private static CloseDraft Close(LedgerHarness h, int hours, Money counted) =>
        CloseBuilder.Build(h.Store.Context, new CloseCommand(
            new CloseId(Guid.NewGuid()), h.Trip, counted, null, h.User, h.Now.AddHours(hours))).Value;

    private static ReopenDraft Reopen(LedgerHarness h, int hours) =>
        ReopenBuilder.Build(h.Store.Context, new ReopenCommand(
            h.Trip, "reconciliation", h.User, h.Now.AddHours(hours))).Value;

    [Fact]
    public void Void_RequiresApproval()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(1, Money.FromPesos(45m), PaymentMethod.Cash);

        var unapproved = VoidBuilder.Build(h.Store.Context, new VoidCommand(
            new VoidId(Guid.NewGuid()), sale.Id, "customer changed mind", h.User, h.Now.AddHours(2)));

        Assert.True(unapproved.IsFailure);
        Assert.Contains(unapproved.Errors, e => e.Code == "VOID_APPROVAL_REQUIRED");
        Assert.Equal(0, h.Store.Movements.Count(static x => x is SaleIssueRevoke));
    }

    [Fact]
    public void Void_Approved_RecordsApprover_AndReverses()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash);
        UserId operatorId = h.User;

        var draft = VoidBuilder.Build(h.Store.Context, new VoidCommand(
            new VoidId(Guid.NewGuid()), sale.Id, "customer changed mind", h.User, h.Now.AddHours(2),
            ApprovedBy: operatorId));

        Assert.True(draft.IsSuccess, string.Join("; ", draft.Errors.Select(e => e.Code)));
        Assert.Equal(operatorId, draft.Value.Document.ApprovedBy);
        h.Store.RegisterVoid(draft.Value.Document);
        h.Store.Bind();
        h.Store.Append(draft.Value.Movements);

        Assert.Equal(SaleStatus.Voided, h.Store.State.Sale(sale.Id).Status);
        Assert.Equal(20, h.Store.State.StockOnHand(h.Sardines, h.RouteLoc).Units); // restored
        Assert.Equal(Money.Zero, h.Store.State.FundsOnHand(Fund.PhysicalCash));
    }

    [Fact]
    public void Void_ClosedTrip_NeedsReopen_AndAuditSequencePreserved()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(2, Money.FromPesos(45m), PaymentMethod.Cash);
        h.Store.Append(Close(h, 5, Money.FromPesos(90m)).Movements);
        h.Store.Bind();
        Assert.Equal(TripStatus.Closed, h.Store.State.Trip(h.Trip).Status);

        var onClosed = VoidBuilder.Build(h.Store.Context, new VoidCommand(
            new VoidId(Guid.NewGuid()), sale.Id, "late correction", h.User, h.Now.AddHours(6),
            ApprovedBy: h.User));

        Assert.True(onClosed.IsFailure);
        Assert.Contains(onClosed.Errors, e => e.Code == "TRIP_CLOSED_CORRECTION");

        var reopen = Reopen(h, 7);
        h.Store.Append(reopen.Movements);
        h.Store.Bind();
        Assert.Equal(TripStatus.Open, h.Store.State.Trip(h.Trip).Status);

        var after = VoidBuilder.Build(h.Store.Context, new VoidCommand(
            new VoidId(Guid.NewGuid()), sale.Id, "late correction", h.User, h.Now.AddHours(8),
            ApprovedBy: h.User));
        Assert.True(after.IsSuccess, string.Join("; ", after.Errors.Select(e => e.Code)));
    }

    [Fact]
    public void Return_PartialToPeddlerStock_RestoresAtSaleLocation()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash);

        var draft = ReturnBuilder.Build(h.Store.Context, new ReturnCommand(
            new ReturnId(Guid.NewGuid()), sale.Id,
            new[] { new ReturnLineCommand(sale.Lines[0].Id, Quantity.Of(2), "damaged can", PaymentMethod.Cash, Money.FromPesos(90m)) },
            h.User, h.Now.AddHours(3)));

        Assert.True(draft.IsSuccess, string.Join("; ", draft.Errors.Select(e => e.Code)));
        h.Store.RegisterReturn(draft.Value.Document);
        h.Store.Bind();
        h.Store.Append(draft.Value.Movements);

        // Restored to the peddler's route stock (the sale location), not the store.
        Assert.Equal(17, h.Store.State.StockOnHand(h.Sardines, h.RouteLoc).Units);
        Assert.Equal(Quantity.Of(2), h.Store.State.Returned(sale.Lines[0].Id));
        Assert.Equal(20, h.Store.State.StockOnHand(h.Sardines, h.StoreLoc).Units); // store untouched
    }

    [Fact]
    public void Return_ToMainStore_LandsAtDestination_ReferenceReconciles()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash);
        long before = h.Store.Movements.Count;

        var draft = ReturnBuilder.Build(h.Store.Context, new ReturnCommand(
            new ReturnId(Guid.NewGuid()), sale.Id,
            new[] { new ReturnLineCommand(
                sale.Lines[0].Id, Quantity.Of(1), "sold to main store walk-in", PaymentMethod.Cash, Money.FromPesos(45m),
                Destination: h.StoreLoc) },
            h.User, h.Now.AddHours(3)));

        Assert.True(draft.IsSuccess, string.Join("; ", draft.Errors.Select(e => e.Code)));
        Assert.All(draft.Value.Movements.OfType<ReturnToStock>(), m => Assert.Equal(h.StoreLoc, m.LocationId));
        Assert.All(draft.Value.Movements, m => Assert.IsType<ReturnRef>(m.Document));
        // Reference = the same ReturnRef on the reversal chain.
        h.Store.RegisterReturn(draft.Value.Document);
        h.Store.Bind();
        h.Store.Append(draft.Value.Movements);
        Assert.Equal(before + 2, h.Store.Movements.Count); // ReturnToStock + ReceiptRefund

        // Both locations reconcile: the item went straight to the main store
        // (the issue reversal's destination), so the route keeps 15 and the
        // store gains one.
        Assert.Equal(15, h.Store.State.StockOnHand(h.Sardines, h.RouteLoc).Units);
        Assert.Equal(21, h.Store.State.StockOnHand(h.Sardines, h.StoreLoc).Units);
    }

    [Fact]
    public void Adjustment_Kinds_DamageAndLoss_AreDistinctMovements()
    {
        var h = new LedgerHarness();

        var damage = AdjustmentBuilder.Build(h.Store.Context, new AdjustmentCommand(
            new AdjustmentId(Guid.NewGuid()), h.Trip, h.Sardines, h.RouteLoc, AdjustmentKind.Damage,
            Quantity.Of(1), "crushed can", h.User, h.Now.AddHours(2), ApprovedBy: h.User));
        Assert.True(damage.IsSuccess, string.Join("; ", damage.Errors.Select(e => e.Code)));
        var damageMovement = Assert.IsType<Damage>(Assert.Single(damage.Value.Movements));
        Assert.Equal("crushed can", damageMovement.Reason);

        var loss = AdjustmentBuilder.Build(h.Store.Context, new AdjustmentCommand(
            new AdjustmentId(Guid.NewGuid()), h.Trip, h.Sardines, h.RouteLoc, AdjustmentKind.Loss,
            Quantity.Of(2), "fell from basket", h.User, h.Now.AddHours(2), ApprovedBy: h.User));
        Assert.True(loss.IsSuccess, string.Join("; ", loss.Errors.Select(e => e.Code)));
        var lossMovement = Assert.IsType<Loss>(Assert.Single(loss.Value.Movements));
        Assert.Equal("fell from basket", lossMovement.Reason);

        // Every correction movement carries actor + timestamp.
        Assert.All(damage.Value.Movements.Concat(loss.Value.Movements), m =>
        {
            Assert.Equal(h.User, m.ActorId);
            Assert.Equal(h.Now.AddHours(2), m.WhenUtc);
        });
    }

    [Fact]
    public void Adjustment_RequiresApproval_AndRejectsInsufficientStock()
    {
        var h = new LedgerHarness();

        var unapproved = AdjustmentBuilder.Build(h.Store.Context, new AdjustmentCommand(
            new AdjustmentId(Guid.NewGuid()), h.Trip, h.Sardines, h.RouteLoc, AdjustmentKind.Damage,
            Quantity.Of(1), "crushed can", h.User, h.Now.AddHours(2)));
        Assert.True(unapproved.IsFailure);
        Assert.Contains(unapproved.Errors, e => e.Code == "ADJUSTMENT_APPROVAL_REQUIRED");

        var oversized = AdjustmentBuilder.Build(h.Store.Context, new AdjustmentCommand(
            new AdjustmentId(Guid.NewGuid()), h.Trip, h.Sardines, h.RouteLoc, AdjustmentKind.Loss,
            Quantity.Of(999), "fell", h.User, h.Now.AddHours(2), ApprovedBy: h.User));
        Assert.True(oversized.IsFailure);
        Assert.Contains(oversized.Errors, e => e.Code == "STOCK_INSUFFICIENT");
    }

    [Fact]
    public void Return_DuplicateBaseline_CappedAtSoldQuantity()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash);

        var first = ReturnBuilder.Build(h.Store.Context, new ReturnCommand(
            new ReturnId(Guid.NewGuid()), sale.Id,
            new[] { new ReturnLineCommand(sale.Lines[0].Id, Quantity.Of(3), "damaged", PaymentMethod.Cash, Money.FromPesos(135m)) },
            h.User, h.Now.AddHours(3)));
        Assert.True(first.IsSuccess);
        h.Store.RegisterReturn(first.Value.Document);
        h.Store.Bind();
        h.Store.Append(first.Value.Movements);

        var second = ReturnBuilder.Build(h.Store.Context, new ReturnCommand(
            new ReturnId(Guid.NewGuid()), sale.Id,
            new[] { new ReturnLineCommand(sale.Lines[0].Id, Quantity.Of(3), "more damaged", PaymentMethod.Cash, Money.FromPesos(135m)) },
            h.User, h.Now.AddHours(4)));
        Assert.True(second.IsFailure);
        Assert.Contains(second.Errors, e => e.Code == "RETURN_LIMIT");
    }
}
