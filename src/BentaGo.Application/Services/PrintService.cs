using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Receipts;

namespace BentaGo.Application.Services;

/// <summary>
/// BG-007 print pipeline. The flow is strictly: sale committed first (the
/// checkout session owns that), then this service renders from a STORED
/// snapshot (ReceiptCodec JSON on the job), enqueues the job, and prints
/// once. A failed print leaves the job Failed/Queued and the UI offers
/// retry (same job — no second sale/movement) and reprint (a clone with the
/// REPRINT label and the original receipt number) — docs/03 §7, docs/05 404-411.
/// </summary>
public sealed class PrintService(
    IPrintJobStore jobs,
    IReceiptPrinter printer,
    Func<DateTimeOffset> now) : IPrintService
{
    public async Task<PrintOutcome> PrintAfterCommitAsync(Sale sale, CancellationToken ct = default)
    {
        ReceiptDocument document = ReceiptDocument.FromSale(sale);
        var job = new PrintJob(
            new PrintJobId(Guid.NewGuid()),
            sale.Id,
            sale.ReceiptNumber,
            ReceiptCodec.Serialize(document),
            PrintJobStatus.Queued,
            Attempts: 0,
            LastErrorCode: null,
            CreatedUtc: now(),
            LastAttemptAt: null,
            ReprintOf: null);
        await jobs.EnqueueAsync(job, ct);
        return await PrintJobOnceAsync(job, document, ct);
    }

    public async Task<PrintOutcome> RetryAsync(SaleId saleId, CancellationToken ct = default)
    {
        PrintJob? job = await jobs.FindBySaleAsync(saleId, reprintInclusive: false, ct);
        if (job is null)
        {
            return new PrintOutcome("", PrintJobStatus.Failed, "NO_PRINT_JOB", IsReprint: false, 0);
        }

        ReceiptDocument document = ReceiptCodec.Deserialize(job.ReceiptDocumentJson);
        return await PrintJobOnceAsync(job with { }, document, ct);
    }

    public async Task<PrintOutcome> ReprintAsync(Sale sale, CancellationToken ct = default)
    {
        // A reprint renders from the ORIGINAL job's stored snapshot and clones
        // it (ReprintOf set -> IsReprint -> the REPRINT label, original number kept).
        PrintJob? original = await jobs.FindBySaleAsync(sale.Id, reprintInclusive: false, ct);
        if (original is null)
        {
            return new PrintOutcome("", PrintJobStatus.Failed, "NO_PRINT_JOB", IsReprint: true, 0);
        }

        ReceiptDocument document = ReceiptCodec.Deserialize(original.ReceiptDocumentJson) with { IsReprint = true };
        var clone = original with
        {
            Id = new PrintJobId(Guid.NewGuid()),
            Status = PrintJobStatus.Queued,
            Attempts = 1,
            CreatedUtc = now(),
            LastAttemptAt = now(),
            ReprintOf = original.Id,
        };
        await jobs.EnqueueAsync(clone, ct);
        return await PrintJobOnceAsync(clone, document, ct);
    }

    private async Task<PrintOutcome> PrintJobOnceAsync(PrintJob job, ReceiptDocument document, CancellationToken ct)
    {
        ReceiptPrintResult print = await printer.PrintAsync(document, ct);
        int attempts = job.Attempts + 1;
        // The store's idempotent update keeps the job row's history; the
        // printable snapshot stays the same JSON so a retry prints the same copy.
        await jobs.TryUpdateAsync(
            job.Id,
            print.Status,
            attempts,
            print.ErrorCode,
            print.AtUtc,
            ct);
        return new PrintOutcome(job.ReceiptNumber, print.Status, print.ErrorCode, job.IsReprint, attempts);
    }
}
