using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;

namespace BentaGo.Application.Contracts;

/// <summary>
/// The nine store-agnostic use cases (BG-003 §9.3). Every service is
/// build -> stage -> bind -> append(+audit) in ONE call; for the SQLite
/// store the append's transaction IS the use case's transaction (lane 3 is
/// structural: staging then failing to build commits nothing). Print state
/// lives OUTSIDE the sale transaction by contract (C5).
///
/// Audit convention: services attach one AuditEvent with the BG-002
/// placeholder sequence (-1) to each batch; the store persists the value as
/// given (AuditEvents has no unique key), so any number of -1 rows is legal.
/// </summary>
public sealed record CheckoutOutcome(CheckoutReference Reference, bool Replayed, AppendReceipt? Receipt);

public sealed record VoidOutcome(SaleVoid Document, AppendReceipt Receipt);

public sealed record ReturnOutcome(Return Document, AppendReceipt Receipt);

public sealed record AdjustmentOutcome(Adjustment Document, AppendReceipt Receipt);

public sealed record CountOutcome(PhysicalCount Document, AppendReceipt Receipt);

public sealed record CloseOutcome(DayClose Document, AppendReceipt Receipt);

public interface ICheckoutService
{
    /// <summary>
    /// Sale, lines, payments, movements, projections, audit, idempotency key —
    /// one transaction. A duplicate <paramref name="key"/> is NOT an error: it
    /// returns the SAME receipt reference with Replayed: true, one sale (D1,
    /// lane 4). Print happens after this returns, through
    /// <see cref="IPrintTicketStore"/> (outside the sale transaction).
    /// </summary>
    Result<CheckoutOutcome> Checkout(CheckoutKey key, NewSaleCommand cmd);
}

public interface IVoidService
{
    /// <summary>Void document + reversing movements + audit — one transaction.</summary>
    Result<VoidOutcome> Void(VoidCommand cmd);
}

public interface IReturnService
{
    /// <summary>Return document + reversing movements + audit — one transaction.</summary>
    Result<ReturnOutcome> Return(ReturnCommand cmd);
}

public interface IAdjustmentService
{
    /// <summary>Adjustment document + movement + audit — one transaction.</summary>
    Result<AdjustmentOutcome> Adjust(AdjustmentCommand cmd);
}

public interface ICountService
{
    /// <summary>Count document + observation movements + audit — one transaction.</summary>
    Result<CountOutcome> Count(CountCommand cmd);
}

public interface ICloseService
{
    /// <summary>Close document (expected cash derived inside the transaction)
    /// + audit — one transaction. Day closes are movement-less documents.</summary>
    Result<CloseOutcome> Close(DayCloseCommand cmd);
}

public interface IReopenService
{
    /// <summary>The DayReopen document + audit — one transaction. A reopen
    /// never erases the prior close (docs/decisions/0004 §6).</summary>
    Result<AppendReceipt> Reopen(DayReopenCommand cmd);
}

/// <summary>
/// Print state, OUTSIDE the sale transaction by contract (C5): the caller
/// renders the committed document after Checkout returns Ok and writes the
/// outcome through this store in its own tiny transaction. A failed print
/// records PRINT_FAILED and changes nothing about the sale.
/// </summary>
public interface IPrintTicketStore
{
    Task<PrintTicketResult> PrintTicketAsync(Sale sale, CancellationToken ct = default);
}

public sealed record PrintTicketResult(bool Printed, string? FailureReason);
