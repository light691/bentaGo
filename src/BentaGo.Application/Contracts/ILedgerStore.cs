using System.Text.Json;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;

namespace BentaGo.Application.Contracts;

public sealed record AppendReceipt(long FirstSequence, long LastSequence);

/// <summary>
/// Result of a ledger rebuild: the watermark the rebuild landed on and the
/// row/document counts the rebuilt state was derived from.
/// </summary>
public sealed record RebuildReceipt(long LastSequence, int MovementCount, int DocumentCount);

/// <summary>
/// Outcome of an <see cref="AppendBatch"/>: either the committed range, or —
/// when the batch carried an <see cref="IdempotencyMapping"/> whose key
/// already exists — a replay (nothing was written this call; ReplayedPayload
/// is the payload stored by the first commit).
/// </summary>
public sealed record AppendOutcome(
    AppendReceipt? Receipt,          // null when Replayed
    bool Replayed,
    string? ReplayedPayload);

/// <summary>
/// The batch is the ONE durable write unit: movements + audits that must land
/// in the SAME transaction + an optional idempotency mapping (generic
/// key => payload, UNIQUE in the store). BG-003's SQLite store commits the
/// whole batch in one transaction; the in-memory store applies it atomically.
/// </summary>
public sealed record AppendBatch(
    IReadOnlyList<Movement> Movements,
    IReadOnlyList<AuditEvent>? Audits = null,
    IdempotencyMapping? Idempotency = null);

/// <summary>
/// A generic key => payload mapping row written in the SAME transaction as
/// the batch it belongs to. The key is UNIQUE: a second batch carrying an
/// existing key is rolled back and reported as a replay, never an error.
/// </summary>
public sealed record IdempotencyMapping(string Key, string Payload);

/// <summary>
/// The receipt reference a checkout key maps to (BG-003 D1, lane 4). The
/// store stays use-case-agnostic, so the reference is serialized into the
/// <see cref="IdempotencyMapping"/> payload and round-tripped verbatim by
/// <see cref="ILedgerStore.FindCheckout"/> after any restart.
/// </summary>
public sealed record CheckoutReference(string ReceiptNumber, SaleId SaleId)
{
    /// <summary>The canonical payload form stored under a checkout key.</summary>
    public string ToPayloadString() =>
        JsonSerializer.Serialize(new CheckoutReferenceDto(ReceiptNumber, SaleId.Value));

    /// <summary>
    /// Rebuilds a reference from a stored payload. Throws
    /// <see cref="InvalidOperationException"/> when the payload is not a
    /// reference; the store maps that to LEDGER_CORRUPT.
    /// </summary>
    public static CheckoutReference FromPayload(string payload)
    {
        try
        {
            CheckoutReferenceDto? dto = JsonSerializer.Deserialize<CheckoutReferenceDto>(payload);
            if (dto is null || string.IsNullOrWhiteSpace(dto.ReceiptNumber))
            {
                throw new InvalidOperationException("checkout reference payload is empty");
            }

            return new CheckoutReference(dto.ReceiptNumber, new SaleId(dto.SaleId));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"stored idempotency payload is not a checkout reference: '{payload}'", ex);
        }
    }

    private sealed record CheckoutReferenceDto(string ReceiptNumber, Guid SaleId);
}

/// <summary>
/// The single mutation seam. <see cref="Append(AppendBatch)"/> runs the
/// state's guard per movement, assigns global monotonic sequences in batch
/// order, and advances the fold. BG-003 implements this contract with one
/// SQLite transaction, so "checkout is one database transaction" becomes a
/// storage detail behind an unchanged public surface.
///
/// Call order contract: register documents
/// (RegisterTrip/RegisterSale/...), then <see cref="Bind"/>, then
/// <see cref="Append(AppendBatch)"/>. The append validates against the bound
/// snapshot, so a document registered after the last <see cref="Bind"/> is
/// not visible to the next append. At each append result the open-attempt set
/// (documents staged for an append that did not commit) is cleared: a failed
/// attempt's documents never leak into a later bound snapshot.
/// </summary>
public interface ILedgerStore
{
    LedgerContext Context { get; }
    LedgerState State { get; }
    IReadOnlyList<Movement> Movements { get; }

    // Register stages a document. Append is the ONLY durable writer and
    // inserts the staged documents in ITS transaction (C13, D5).
    void RegisterTrip(Trip trip);       // trips are movement-less -> RegisterTrip commits its own one-row txn (D5 asymmetry, flagged for the audit)
    void RegisterSale(Sale sale);
    void RegisterReturn(Return @return);
    void RegisterLoad(StockLoad load);
    void RegisterVoid(SaleVoid @void);
    void RegisterAdjustment(Adjustment adjustment);
    void RegisterCount(PhysicalCount count);
    void RegisterClose(DailyClose @close);
    void Bind();

    /// <summary>The batch append: movements + audits + optional idempotency
    /// mapping, one transaction. A duplicate idempotency key is rolled back
    /// and surfaced as Ok(Replayed: true, ReplayedPayload: stored payload) —
    /// never a DomainError.</summary>
    Result<AppendOutcome> Append(AppendBatch batch);

    /// <summary>BG-002 call shape kept (scenario/tests): a movement-only
    /// batch mapped to its committed range.</summary>
    Result<AppendReceipt> Append(IReadOnlyList<Movement> movements);

    /// <summary>A standalone tiny transaction (lane-9 style audit rows).</summary>
    void RecordAudit(AuditEvent audit);

    IReadOnlyList<AuditEvent> Audits { get; }

    /// <summary>
    /// The explicit idempotency lookup (D1, lane 4): the receipt reference a
    /// checkout key maps to, or null when the key has never been committed.
    /// </summary>
    Result<CheckoutReference?> FindCheckout(string idempotencyKey);

    /// <summary>
    /// Recomputes every balance from the persisted movement stream and
    /// rewrites the durable projections (lane 6). After any rebuild the
    /// store's state must equal the projections exactly.
    /// </summary>
    Task<RebuildReceipt> RebuildAsync(CancellationToken ct = default);
}
