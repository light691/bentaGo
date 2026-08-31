using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;

namespace BentaGo.Application.Services;

/// <summary>
/// The nine store-agnostic use cases (BG-003 §9.3): each is
/// builder -> Register* (stage) -> Bind -> Append(AppendBatch) with one
/// attached audit event. The store's Append transaction IS the use case's
/// transaction, so staging then failing to build commits nothing.
///
/// F12 terminal/reason plumbing (BG-005d): every use-case audit sets Terminal
/// to Environment.MachineName — the same source the login rows use
/// (docs/01:77) — because the terminal is knowable at call time. Reason is
/// set where the command carries one (void, adjustment, reopen, cancel).
/// Audits that are NOT use-case actions keep both null — the scenario
/// diagnostics (LANE9 rows recording rejected operations) and batch-style
/// rows — because no single terminal performed them.
/// </summary>
public sealed class CheckoutService(ILedgerStore store) : ICheckoutService
{
    public Result<CheckoutOutcome> Checkout(CheckoutKey key, NewSaleCommand cmd)
    {
        string keyText = key.Value.ToString("D");

        // Pre-check fast path (D1): an already-committed key returns the SAME
        // receipt reference without touching the store's transaction.
        Result<CheckoutReference?> found = store.FindCheckout(keyText);
        if (found.IsFailure)
        {
            return Result<CheckoutOutcome>.Fail(found.Errors);
        }

        if (found.Value is { } existing)
        {
            return Result<CheckoutOutcome>.Ok(new CheckoutOutcome(existing, Replayed: true, Receipt: null));
        }

        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<SaleDraft> draft = SaleBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<CheckoutOutcome>.Fail(draft.Errors);
        }

        store.RegisterSale(draft.Value.Document);
        store.Bind();

        var reference = new CheckoutReference(draft.Value.Document.ReceiptNumber, draft.Value.Document.Id);
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "SALE",
                    $"receipt {reference.ReceiptNumber} committed", new SaleRef(draft.Value.Document.Id),
                    Terminal: Environment.MachineName),
            },
            Idempotency: new IdempotencyMapping(keyText, reference.ToPayloadString())));

        if (outcome.IsFailure)
        {
            return Result<CheckoutOutcome>.Fail(outcome.Errors);
        }

        if (outcome.Value.Replayed)
        {
            // Authoritative replay: the append detected the key collision
            // inside its transaction and rolled back (D1).
            try
            {
                CheckoutReference replayed = CheckoutReference.FromPayload(outcome.Value.ReplayedPayload!);
                return Result<CheckoutOutcome>.Ok(new CheckoutOutcome(replayed, Replayed: true, Receipt: null));
            }
            catch (InvalidOperationException)
            {
                return Result<CheckoutOutcome>.Fail(new DomainError(
                    "LEDGER_CORRUPT", "stored idempotency payload is not a checkout reference"));
            }
        }

        return Result<CheckoutOutcome>.Ok(new CheckoutOutcome(
            reference, Replayed: false, Receipt: outcome.Value.Receipt));
    }
}

public sealed class VoidService(ILedgerStore store) : IVoidService
{
    public Result<VoidOutcome> Void(VoidCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<VoidDraft> draft = VoidBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<VoidOutcome>.Fail(draft.Errors);
        }

        store.RegisterVoid(draft.Value.Document);
        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "VOID",
                    $"sale {cmd.Sale} voided: {cmd.Reason} (approved by {cmd.ApprovedBy})",
                    new VoidRef(cmd.Id),
                    Terminal: Environment.MachineName, Reason: cmd.Reason),
            }));
        if (outcome.IsFailure)
        {
            return Result<VoidOutcome>.Fail(outcome.Errors);
        }

        return Result<VoidOutcome>.Ok(new VoidOutcome(draft.Value.Document, outcome.Value.Receipt!));
    }
}

public sealed class ReturnService(ILedgerStore store) : IReturnService
{
    public Result<ReturnOutcome> Return(ReturnCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<ReturnDraft> draft = ReturnBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<ReturnOutcome>.Fail(draft.Errors);
        }

        store.RegisterReturn(draft.Value.Document);
        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                // No top-level reason on ReturnCommand (reasons live per line);
                // the reason column stays null on purpose.
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "RETURN",
                    $"return {cmd.Id} on sale {cmd.OriginalSale} committed", new ReturnRef(cmd.Id),
                    Terminal: Environment.MachineName),
            }));
        if (outcome.IsFailure)
        {
            return Result<ReturnOutcome>.Fail(outcome.Errors);
        }

        return Result<ReturnOutcome>.Ok(new ReturnOutcome(draft.Value.Document, outcome.Value.Receipt!));
    }
}

public sealed class AdjustmentService(ILedgerStore store) : IAdjustmentService
{
    public Result<AdjustmentOutcome> Adjust(AdjustmentCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<AdjustmentDraft> draft = AdjustmentBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<AdjustmentOutcome>.Fail(draft.Errors);
        }

        store.RegisterAdjustment(draft.Value.Document);
        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "ADJUSTMENT",
                    $"adjustment {cmd.Id} ({cmd.Kind}) on day {cmd.Day} approved by {cmd.ApprovedBy}: {cmd.Reason}",
                    new AdjustmentRef(cmd.Id),
                    Terminal: Environment.MachineName, Reason: cmd.Reason),
            }));
        if (outcome.IsFailure)
        {
            return Result<AdjustmentOutcome>.Fail(outcome.Errors);
        }

        return Result<AdjustmentOutcome>.Ok(new AdjustmentOutcome(draft.Value.Document, outcome.Value.Receipt!));
    }
}

public sealed class CountService(ILedgerStore store) : ICountService
{
    public Result<CountOutcome> Count(CountCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<CountDraft> draft = CountBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<CountOutcome>.Fail(draft.Errors);
        }

        store.RegisterCount(draft.Value.Document);
        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "COUNT",
                    $"count {cmd.Id} committed", new CountRef(cmd.Id),
                    Terminal: Environment.MachineName),
            }));
        if (outcome.IsFailure)
        {
            return Result<CountOutcome>.Fail(outcome.Errors);
        }

        return Result<CountOutcome>.Ok(new CountOutcome(draft.Value.Document, outcome.Value.Receipt!));
    }
}

public sealed class CloseService(ILedgerStore store) : ICloseService
{
    public Result<CloseOutcome> Close(DayCloseCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process documents included)
        Result<DayCloseDraft> draft = DayBuilder.Close(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<CloseOutcome>.Fail(draft.Errors);
        }

        store.RegisterDayClose(draft.Value.Document);
        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            Array.Empty<Movement>(),
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "DAY.CLOSE",
                    $"day {cmd.Day} closed (counted {new Money(draft.Value.Document.CountedCashCentavos).ToPesos():0.00})", null,
                    Terminal: Environment.MachineName),
            }));
        if (outcome.IsFailure)
        {
            return Result<CloseOutcome>.Fail(outcome.Errors);
        }

        return Result<CloseOutcome>.Ok(new CloseOutcome(draft.Value.Document, outcome.Value.Receipt!));
    }
}

public sealed class ReopenService(ILedgerStore store) : IReopenService
{
    public Result<AppendReceipt> Reopen(DayReopenCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process documents included)
        Result<DayReopenDraft> draft = DayBuilder.Reopen(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<AppendReceipt>.Fail(draft.Errors);
        }

        store.RegisterDayReopen(draft.Value.Document);
        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            Array.Empty<Movement>(),
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "DAY.REOPEN",
                    $"day {cmd.Day} reopened: {cmd.Reason}", null,
                    Terminal: Environment.MachineName, Reason: cmd.Reason),
            }));
        if (outcome.IsFailure)
        {
            return Result<AppendReceipt>.Fail(outcome.Errors);
        }

        return Result<AppendReceipt>.Ok(outcome.Value.Receipt!);
    }
}
