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
/// </summary>
public sealed class OpenTripService(ILedgerStore store) : IOpenTripService
{
    public Result<Trip> OpenTrip(NewTripCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<TripDraft> draft = TripBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<Trip>.Fail(draft.Errors);
        }

        store.RegisterTrip(draft.Value.Document);
        return Result<Trip>.Ok(draft.Value.Document);
    }
}

public sealed class LoadService(ILedgerStore store) : ILoadService
{
    public Result<LoadOutcome> Load(LoadCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<LoadDraft> draft = LoadBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<LoadOutcome>.Fail(draft.Errors);
        }

        store.RegisterLoad(draft.Value.Document);
        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "LOAD",
                    $"load {cmd.LoadNumber} committed", new LoadRef(cmd.Id)),
            }));
        if (outcome.IsFailure)
        {
            return Result<LoadOutcome>.Fail(outcome.Errors);
        }

        return Result<LoadOutcome>.Ok(new LoadOutcome(draft.Value.Document, outcome.Value.Receipt!));
    }
}

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
                    $"receipt {reference.ReceiptNumber} committed", new SaleRef(draft.Value.Document.Id)),
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
                    $"sale {cmd.Sale} voided: {cmd.Reason}", new VoidRef(cmd.Id)),
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
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "RETURN",
                    $"return {cmd.Id} on sale {cmd.OriginalSale} committed", new ReturnRef(cmd.Id)),
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
                    $"adjustment {cmd.Id} committed", new AdjustmentRef(cmd.Id)),
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
                    $"count {cmd.Id} committed", new CountRef(cmd.Id)),
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
    public Result<CloseOutcome> Close(CloseCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<CloseDraft> draft = CloseBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<CloseOutcome>.Fail(draft.Errors);
        }

        store.RegisterClose(draft.Value.Document);
        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "CLOSE",
                    $"trip {cmd.Trip} closed (counted {draft.Value.Document.CountedCash.ToPesos():0.00})", new CloseRef(cmd.Id)),
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
    public Result<AppendReceipt> Reopen(ReopenCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<ReopenDraft> draft = ReopenBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<AppendReceipt>.Fail(draft.Errors);
        }

        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "REOPEN",
                    $"trip {cmd.Trip} reopened: {cmd.Reason}", new ReopenRef(cmd.Trip)),
            }));
        if (outcome.IsFailure)
        {
            return Result<AppendReceipt>.Fail(outcome.Errors);
        }

        return Result<AppendReceipt>.Ok(outcome.Value.Receipt!);
    }
}
