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

/// <summary>
/// BG-007 print pipeline (docs/03 §7: commit first, printer reports after).
/// The app renders from the STORED model (the job's snapshot), never from
/// mutable state; a retry uses the SAME job (idempotent — no second sale);
/// reprint clones the job with the REPRINT label and the original number.
/// </summary>
public interface IPrintService
{
    /// <summary>Enqueues the job for the committed sale and prints it once.</summary>
    Task<PrintOutcome> PrintAfterCommitAsync(Sale sale, CancellationToken ct = default);

    /// <summary>Prints the SAME job again (idempotent retry of a failed print).</summary>
    Task<PrintOutcome> RetryAsync(SaleId saleId, CancellationToken ct = default);

    /// <summary>Prints a NEW labelled copy (REPRINT) of a successful receipt.</summary>
    Task<PrintOutcome> ReprintAsync(Sale sale, CancellationToken ct = default);

    /// <summary>
    /// BG-008 Slice C: prints a CLOSE summary through the SAME pipeline after
    /// the close commits (the close is already saved; a failed print leaves the
    /// facts intact). The job snapshot is the close-summary document, so a
    /// retry reprints the identical copy and a reprint keeps the original close
    /// identity under the REPRINT label.
    /// </summary>
    Task<PrintOutcome> PrintCloseSummaryAsync(ReceiptDocument closeDocument, CancellationToken ct = default);

    /// <summary>Prints the SAME close job again (idempotent retry).</summary>
    Task<PrintOutcome> RetryCloseAsync(CloseId closeId, CancellationToken ct = default);

    /// <summary>Prints a NEW labelled close copy (REPRINT) with the original close identity.</summary>
    Task<PrintOutcome> ReprintCloseAsync(CloseId closeId, CancellationToken ct = default);
}

/// <summary>The outcome the UI renders ("Sale saved. Printer needs attention." on failure).</summary>
public sealed record PrintOutcome(
    string ReceiptNumber,
    PrintJobStatus Status,
    string? ErrorCode,
    bool IsReprint,
    int Attempts)
{
    public string UserMessage => Status == PrintJobStatus.Printed
        ? (IsReprint ? "Receipt reprinted." : "Receipt printed.")
        : "Sale saved. Printer needs attention. You can retry or reprint later.";
}

/// <summary>The selected printer profile (software-first: fake is the default; USB ESC/POS on device).</summary>
public enum PrinterProfile
{
    /// <summary>Captures documents without hardware (the default until the device pass).</summary>
    Fake,

    /// <summary>Raw USB ESC/POS 80 mm (the operator's Epson TM-class device).</summary>
    UsbEscPos,
}

public interface IPrinterSettingsStore
{
    Task<PrinterProfile> GetProfileAsync(CancellationToken ct = default);

    Task<bool> SetProfileAsync(PrinterProfile profile, CancellationToken ct = default);
}

/// <summary>Raw scanner input diagnostics (BG-007 lane 5): the most recent
/// untrimmed inputs with the exact suffix and the matched product.</summary>
public sealed record ScanInputEntry(int Ordinal, string Raw, string Suffix, string? MatchedProduct, DateTimeOffset AtUtc);

public interface IScannerDiagnostics
{
    /// <summary>Records the RAW box text before any trim (the suffix included).</summary>
    void RecordScan(string rawText, string? matchedProduct, DateTimeOffset atUtc);

    IReadOnlyList<ScanInputEntry> Recent { get; }

    int Count { get; }
}
