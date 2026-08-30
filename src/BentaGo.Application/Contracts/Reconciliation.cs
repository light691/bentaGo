using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Reconciliation;

namespace BentaGo.Application.Contracts;

/// <summary>
/// BG-008 Slice A read surface: builds the per-trip reconciliation report
/// (pure projection over <see cref="ILedgerStore"/> / <see cref="ISaleQueryService"/> —
/// no database access) and renders the transaction and reconciliation CSV
/// exports. The report is deterministic: it explains every difference for a
/// trip from the ledger fold and the submitted close/count documents, and the
/// totals always equal the ledger folds and the transaction-history rows.
/// </summary>
public interface IReconciliationReportService
{
    /// <summary>
    /// Builds the reconciliation report for a trip owned by
    /// <paramref name="peddler"/>. Cross-peddler access -> TRIP_PEDDLER_MISMATCH.
    /// </summary>
    Result<ReconciliationReport> Build(PeddlerId peddler, TripId trip);

    /// <summary>RFC-4180 transaction CSV (date, receipt, lines summary, payment
    /// method(s), amounts; refunds excluded per the existing rules).</summary>
    Result<string> BuildTransactionCsv(PeddlerId peddler, TripId trip);

    /// <summary>RFC-4180 reconciliation CSV (one row per product line plus the
    /// countable-fund cash rows).</summary>
    Result<string> BuildReconciliationCsv(PeddlerId peddler, TripId trip);
}
