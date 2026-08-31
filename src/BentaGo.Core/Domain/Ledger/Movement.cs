using BentaGo.Core.Domain.Payments;

namespace BentaGo.Core.Domain.Ledger;

/// <summary>
/// The append-only movement stream. One sealed kind per business fact, each
/// carrying exactly its own payload: a wide enum + optional-fields record
/// was rejected because every nullable field is an illegal combination the
/// fold would have to police. Constructed only through <see cref="Moves"/>,
/// which enforces kind legality (UTC timestamps, positive quantities, no
/// Receipt for Credit, required reversals) at the boundary.
/// </summary>
public abstract record Movement
{
    /// <summary>0 until the store assigns the global append order.</summary>
    public required long Sequence { get; init; }

    /// <summary>Always UTC. Enforced by the factory.</summary>
    public required DateTimeOffset WhenUtc { get; init; }

    public required UserId ActorId { get; init; }

    /// <summary>The document that caused this movement.</summary>
    public required DocumentRef Document { get; init; }

    public abstract MovementKind Kind { get; }

    public abstract MovementEffect Effect { get; }
}

public sealed record GoodsReceived : Movement
{
    public override MovementKind Kind => MovementKind.GoodsReceived;
    public override MovementEffect Effect => MovementEffect.StockIncrease;
    public required ProductId ProductId { get; init; }
    public required LocationId LocationId { get; init; }
    public required Quantity Quantity { get; init; }
}

public sealed record SaleIssue : Movement
{
    public override MovementKind Kind => MovementKind.SaleIssue;
    public override MovementEffect Effect => MovementEffect.StockDecrease;
    public required ProductId ProductId { get; init; }
    public required LocationId LocationId { get; init; }
    public required Quantity Quantity { get; init; }
    public required SaleLineId SaleLineId { get; init; }
}

public sealed record SaleIssueRevoke : Movement
{
    public override MovementKind Kind => MovementKind.SaleIssueRevoke;
    public override MovementEffect Effect => MovementEffect.StockIncrease;
    public required ProductId ProductId { get; init; }
    public required LocationId LocationId { get; init; }
    public required Quantity Quantity { get; init; }
    /// <summary>The SaleIssue sequence being reversed.</summary>
    public required long Reverses { get; init; }
}

public sealed record ReturnToStock : Movement
{
    public override MovementKind Kind => MovementKind.ReturnToStock;
    public override MovementEffect Effect => MovementEffect.StockIncrease;
    public required ProductId ProductId { get; init; }
    public required LocationId LocationId { get; init; }
    public required Quantity Quantity { get; init; }
    /// <summary>The sale line the quantity returns against (limit is per line).</summary>
    public required SaleLineId SaleLineId { get; init; }
    /// <summary>The SaleIssue sequence whose line is being returned against.</summary>
    public required long Reverses { get; init; }
}

public sealed record Damage : Movement
{
    public override MovementKind Kind => MovementKind.Damage;
    public override MovementEffect Effect => MovementEffect.StockDecrease;
    public required ProductId ProductId { get; init; }
    public required LocationId LocationId { get; init; }
    public required Quantity Quantity { get; init; }
    public required string Reason { get; init; }
}

public sealed record Loss : Movement
{
    public override MovementKind Kind => MovementKind.Loss;
    public override MovementEffect Effect => MovementEffect.StockDecrease;
    public required ProductId ProductId { get; init; }
    public required LocationId LocationId { get; init; }
    public required Quantity Quantity { get; init; }
    public required string Reason { get; init; }
}

public sealed record AdjustmentIncrease : Movement
{
    public override MovementKind Kind => MovementKind.AdjustmentIncrease;
    public override MovementEffect Effect => MovementEffect.StockIncrease;
    public required ProductId ProductId { get; init; }
    public required LocationId LocationId { get; init; }
    public required Quantity Quantity { get; init; }
    public required string Reason { get; init; }
}

public sealed record AdjustmentDecrease : Movement
{
    public override MovementKind Kind => MovementKind.AdjustmentDecrease;
    public override MovementEffect Effect => MovementEffect.StockDecrease;
    public required ProductId ProductId { get; init; }
    public required LocationId LocationId { get; init; }
    public required Quantity Quantity { get; init; }
    public required string Reason { get; init; }
}

public sealed record Receipt : Movement
{
    public override MovementKind Kind => MovementKind.Receipt;
    public override MovementEffect Effect => MovementEffect.FundIncrease;
    public required PaymentMethod Method { get; init; }
    public required Money Amount { get; init; }
    /// <summary>The sale payment line this receipt settles.</summary>
    public required PaymentLineId PaymentLineId { get; init; }
}

public sealed record ReceiptRefund : Movement
{
    public override MovementKind Kind => MovementKind.ReceiptRefund;
    public override MovementEffect Effect => MovementEffect.FundDecrease;
    public required PaymentMethod Method { get; init; }
    public required Money Amount { get; init; }
    public required PaymentLineId PaymentLineId { get; init; }
    /// <summary>The Receipt sequence being partially refunded.</summary>
    public required long Reverses { get; init; }
}

public sealed record ReceiptRevoke : Movement
{
    public override MovementKind Kind => MovementKind.ReceiptRevoke;
    public override MovementEffect Effect => MovementEffect.FundDecrease;
    public required PaymentMethod Method { get; init; }
    public required Money Amount { get; init; }
    public required PaymentLineId PaymentLineId { get; init; }
    /// <summary>The Receipt sequence being revoked.</summary>
    public required long Reverses { get; init; }
}

/// <summary>
/// Fund in outside a sale — cash pay-downs received on a customer receivable
/// (docs/decisions/0004 §3). A ledger event on the receivable pairs with this
/// movement in one atomic transaction in a later slice; the cash side is
/// first-class here.
/// </summary>
public sealed record FundIn : Movement
{
    public override MovementKind Kind => MovementKind.FundIn;
    public override MovementEffect Effect => MovementEffect.FundIncrease;
    public required PaymentMethod Method { get; init; }
    public required Money Amount { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// An approved cash expense paid out of the drawer (docs/decisions/0004 §4):
/// a first-class movement with a reason and, where required, an approver —
/// never a day-close text field.
/// </summary>
public sealed record FundOut : Movement
{
    public override MovementKind Kind => MovementKind.FundOut;
    public override MovementEffect Effect => MovementEffect.FundDecrease;
    public required PaymentMethod Method { get; init; }
    public required Money Amount { get; init; }
    public required string Reason { get; init; }
}

public sealed record CountObserved : Movement
{
    public override MovementKind Kind => MovementKind.CountObserved;
    public override MovementEffect Effect => MovementEffect.None;
    public required ProductId ProductId { get; init; }
    public required LocationId LocationId { get; init; }
    public required Quantity ObservedQuantity { get; init; }
}
