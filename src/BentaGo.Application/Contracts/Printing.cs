using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Receipts;

namespace BentaGo.Application.Contracts;

/// <summary>
/// BG-007 print-job persistence (docs/03 §7: checkout commits first, the
/// printer adapter reports status after commit, reprint is a separate
/// command). Retry is idempotent — one sale, one job; reprint clones the job
/// and every reprint is labelled REPRINT while keeping the original receipt
/// number. The printable snapshot is stored with the job so a retry never
/// re-renders from mutable state.
/// </summary>
public interface IPrintJobStore
{
    Task<PrintJob> EnqueueAsync(PrintJob job, CancellationToken cancellationToken = default);

    Task<PrintJob?> FindByIdAsync(PrintJobId id, CancellationToken cancellationToken = default);

    Task<PrintJob?> FindBySaleAsync(SaleId saleId, bool reprintInclusive, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PrintJob>> ListAsync(int take = 100, CancellationToken cancellationToken = default);

    /// <summary>Marks the job after an attempt; callers pass the updated row.</summary>
    Task<bool> TryUpdateAsync(
        PrintJobId id,
        PrintJobStatus status,
        int attempts,
        string? lastErrorCode,
        DateTimeOffset lastAttemptAt,
        CancellationToken cancellationToken = default);
}
