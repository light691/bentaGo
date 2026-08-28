using System.IO;
using System.Text;
using BentaGo.Application.Contracts;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Persistence;

namespace BentaGo.Infrastructure.PrintTickets;

/// <summary>
/// Print-state store for a committed sale, OUTSIDE the sale transaction by
/// contract (C5): the ticket is rendered and written to the prints directory,
/// and the outcome is recorded as an audit row in its own tiny transaction.
/// A failed print (DebugOptions.FailNextPrint simulates hardware failure)
/// records PRINT_FAILED and changes nothing about the sale. Actual hardware
/// printing is deferred; this is the durable print-state seam.
/// </summary>
public sealed class PrintTicketStore : IPrintTicketStore
{
    private readonly ILedgerStore _ledger;
    private readonly IDataPathProvider _paths;
    private readonly DebugOptions _debug;

    public PrintTicketStore(ILedgerStore ledger, IDataPathProvider paths, DebugOptions debug)
    {
        _ledger = ledger;
        _paths = paths;
        _debug = debug;
    }

    public Task<PrintTicketResult> PrintTicketAsync(Sale sale, CancellationToken ct = default)
    {
        _paths.EnsureCreated();
        bool failed = _debug.ConsumeFailNextPrint();
        string? reason = failed ? "simulated print hardware failure" : null;

        if (!failed)
        {
            string printsDir = Path.Combine(_paths.DataDir, "prints");
            Directory.CreateDirectory(printsDir);
            string ticketPath = Path.Combine(printsDir, $"ticket-{sale.ReceiptNumber}-{sale.Id.Value:N}.txt");
            File.WriteAllText(ticketPath, RenderTicket(sale), Encoding.UTF8);
        }

        // Print state lives in the audit stream, committed in its own tiny
        // transaction AFTER the sale committed (C5).
        _ledger.RecordAudit(new AuditEvent(
            -1, sale.CreatedBy, sale.CreatedUtc, "PRINT",
            failed
                ? $"PRINT_FAILED: {reason} ({sale.ReceiptNumber})"
                : $"PRINTED {sale.ReceiptNumber}",
            new SaleRef(sale.Id)));

        return Task.FromResult(new PrintTicketResult(!failed, reason));
    }

    private static string RenderTicket(Sale sale)
    {
        var text = new StringBuilder();
        text.AppendLine("BentaGo receipt");
        text.AppendLine($"  receipt:  {sale.ReceiptNumber}");
        text.AppendLine($"  sale:     {sale.Id.Value:N}");
        text.AppendLine($"  total:    {sale.Total.ToPesos():0.00}");
        return text.ToString();
    }
}
