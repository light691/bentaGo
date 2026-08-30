using System.IO;
using System.Text;
using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Receipts;
using BentaGo.Core.Domain.Reconciliation;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Printing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Receipts;

/// <summary>
/// BG-008 Slice C close-summary print (docs/05 480-481): the close summary
/// renders from the report and flows through the SAME BG-007 pipeline —
/// printed after the close commits, a failed print leaves the close facts
/// intact and the job recoverable, a retry reprints the identical snapshot,
/// and a reprint keeps the original close identity under the REPRINT label.
/// The snapshot round-trips the whole close document (kind + close payload).
/// </summary>
public sealed class CloseSummaryPrintTests
{
    // ------------------------------------------------------------------
    // Codec + encoder (pure, no host)
    // ------------------------------------------------------------------

    private static InMemoryLedgerStore SeededInMemory()
    {
        var store = new InMemoryLedgerStore();
        TwoPeddlersScenario.Run(store);
        return store;
    }

    private static (ReconciliationReport Report, DailyClose Close) AliceReport(ILedgerStore store)
    {
        store.Bind();
        DailyClose close = store.Context.Closes
            .OrderBy(c => c.WhenUtc).ThenBy(c => c.Id.Value)
            .First();
        Trip trip = store.Context.Trips.Single(t => t.Id == close.Trip);
        Result<ReconciliationReport> result = new ReconciliationReportService(store).Build(trip.Peddler, trip.Id);
        Assert.True(result.IsSuccess, result.Errors.FirstOrDefault()?.Code ?? "report failed");
        return (result.Value, close);
    }

    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes)
        .Replace("\u001B@", string.Empty, StringComparison.Ordinal)
        .Replace("\u001BE\u0001", string.Empty, StringComparison.Ordinal)
        .Replace("\u001BE\u0000", string.Empty, StringComparison.Ordinal)
        .Replace("\u001DV\u0042", string.Empty, StringComparison.Ordinal);

    private static string[] Lines(byte[] bytes) =>
        Decode(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Encoder_CloseSummary_Fits42Columns()
    {
        (ReconciliationReport report, DailyClose close) = AliceReport(SeededInMemory());
        ReceiptDocument doc = ReceiptDocument.FromCloseSummary(
            report, close.Id, close.WhenUtc,
            "Ace Mini Mart", "123 Rizal St, Pasig City", "0917 000 0000", "000-123-456-000", close.Notes);

        byte[] bytes = new EscPos80mmEncoder().Encode(doc);
        string[] lines = Lines(bytes);

        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.True(
            line.Length <= EscPos80mmEncoder.NominalColumns,
            $"line '{line}' is {line.Length} columns, exceeding {EscPos80mmEncoder.NominalColumns}"));
    }

    [Fact]
    public void Encoder_CloseSummary_HeaderOnlyWhenSupplied()
    {
        (ReconciliationReport report, DailyClose close) = AliceReport(SeededInMemory());

        ReceiptDocument withHeader = ReceiptDocument.FromCloseSummary(
            report, close.Id, close.WhenUtc, "Ace Mini Mart", "123 Rizal St", "0917", "000-123-456-000", null);
        ReceiptDocument withoutHeader = ReceiptDocument.FromCloseSummary(
            report, close.Id, close.WhenUtc, null, null, null, null, null);

        string withText = Decode(new EscPos80mmEncoder().Encode(withHeader));
        string withoutText = Decode(new EscPos80mmEncoder().Encode(withoutHeader));

        Assert.Contains("Ace Mini Mart", withText, StringComparison.Ordinal);
        Assert.Contains("TIN 000-123-456-000", withText, StringComparison.Ordinal);
        Assert.DoesNotContain("Ace Mini Mart", withoutText, StringComparison.Ordinal);
        Assert.DoesNotContain("TIN", withoutText, StringComparison.Ordinal);
        // Both still carry the cash totals (the close facts, not the header).
        Assert.Contains("CASH VARIANCE", withText, StringComparison.Ordinal);
        Assert.Contains("CASH VARIANCE", withoutText, StringComparison.Ordinal);
    }

    [Fact]
    public void Codec_RoundTripsCloseSummary()
    {
        (ReconciliationReport report, DailyClose close) = AliceReport(SeededInMemory());
        ReceiptDocument original = ReceiptDocument.FromCloseSummary(
            report, close.Id, close.WhenUtc,
            "Ace Mini Mart", "123 Rizal St", "0917", "000-123-456-000", "note here");

        string json = ReceiptCodec.Serialize(original);
        ReceiptDocument restored = ReceiptCodec.Deserialize(json);

        Assert.Equal(ReceiptKind.CloseSummary, restored.Kind);
        Assert.Equal("Ace Mini Mart", restored.Close!.StoreName);
        Assert.Equal(close.Id.Value.ToString("D"), restored.Close.CloseId);
        var originalClose = original.Close!;
        Assert.Equal(originalClose.Totals.ExpectedCash, restored.Close.Totals.ExpectedCash);
        Assert.Equal(originalClose.Totals.CashVariance, restored.Close.Totals.CashVariance);
        Assert.Equal(originalClose.FundRows.Count, restored.Close.FundRows.Count);
        Assert.Equal(originalClose.StockRows.Count, restored.Close.StockRows.Count);
        Assert.Equal("note here", restored.Close.Notes);
        Assert.Equal(original.Meta.ReceiptNumber, restored.Meta.ReceiptNumber);
    }

    // ------------------------------------------------------------------
    // Print pipeline (real host, EF print-job store, fake printer)
    // ------------------------------------------------------------------

    private static async Task<(ServiceProvider Host, string Root, SqliteLedgerStore Store)> NewSeededHostAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "bentago-close-" + Guid.NewGuid().ToString("N"));
        ServiceProvider host = BentaGoHost.Build(development: true, dataRootOverride: root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        var store = host.GetRequiredService<SqliteLedgerStore>();
        _ = TwoPeddlersScenario.Run(store);
        return (host, root, store);
    }

    private static void Cleanup(ServiceProvider host, string root)
    {
        host.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task PrintCloseSummary_PrintsOnce_AndPersistsAJob()
    {
        (ServiceProvider host, string root, SqliteLedgerStore store) = await NewSeededHostAsync();
        try
        {
            var printer = (FakeReceiptPrinter)host.GetRequiredService<IReceiptPrinter>();
            IPrintService service = host.GetRequiredService<IPrintService>();
            (ReconciliationReport report, DailyClose close) = AliceReport(store);
            ReceiptDocument doc = ReceiptDocument.FromCloseSummary(
                report, close.Id, close.WhenUtc, "Ace Mini Mart", "123 Rizal St", null, null, close.Notes);

            PrintOutcome outcome = await service.PrintCloseSummaryAsync(doc);

            Assert.Equal(PrintJobStatus.Printed, outcome.Status);
            Assert.False(outcome.IsReprint);
            Assert.Single(printer.Printed);
            Assert.Equal("CLOSE-" + close.Id.Value.ToString("D"), outcome.ReceiptNumber);
            IReadOnlyList<PrintJob> jobs = await host.GetRequiredService<IPrintJobStore>().ListAsync(take: 50);
            PrintJob single = Assert.Single(jobs);
            Assert.Equal(PrintJobStatus.Printed, single.Status);
        }
        finally
        {
            Cleanup(host, root);
        }
    }

    [Fact]
    public async Task FailedPrint_LeavesCloseIntact_AndRetryReprintsSameSummary()
    {
        (ServiceProvider host, string root, SqliteLedgerStore store) = await NewSeededHostAsync();
        try
        {
            var printer = (FakeReceiptPrinter)host.GetRequiredService<IReceiptPrinter>();
            IPrintService service = host.GetRequiredService<IPrintService>();
            (ReconciliationReport report, DailyClose close) = AliceReport(store);
            ReceiptDocument doc = ReceiptDocument.FromCloseSummary(
                report, close.Id, close.WhenUtc, "Ace Mini Mart", "123 Rizal St", null, null, close.Notes);

            printer.FailNext();
            PrintOutcome failed = await service.PrintCloseSummaryAsync(doc);

            Assert.Equal(PrintJobStatus.Failed, failed.Status);
            Assert.Equal("UNPLUGGED", failed.ErrorCode);
            Assert.Empty(printer.Printed);
            Assert.Single(printer.Encoded);

            // The close is ALREADY saved and is not undone by the failed print.
            store.Bind();
            Assert.Contains(store.Context.Closes, c => c.Id == close.Id && c.CountedCash == close.CountedCash);

            // Reconnect + retry: the SAME job prints the SAME snapshot.
            printer.ClearFailures();
            PrintOutcome retried = await service.RetryCloseAsync(close.Id);

            Assert.Equal(PrintJobStatus.Printed, retried.Status);
            Assert.Single(printer.Printed);
            Assert.False(retried.IsReprint);
            string retriedText = Decode(printer.Encoded[^1]);
            Assert.Contains(close.Id.Value.ToString("D"), retriedText, StringComparison.Ordinal);
            Assert.Contains("Ace Mini Mart", retriedText, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(host, root);
        }
    }

    [Fact]
    public async Task ReprintClose_KeepsIdentity_AndLabelsReprint()
    {
        (ServiceProvider host, string root, SqliteLedgerStore store) = await NewSeededHostAsync();
        try
        {
            var printer = (FakeReceiptPrinter)host.GetRequiredService<IReceiptPrinter>();
            IPrintService service = host.GetRequiredService<IPrintService>();
            (ReconciliationReport report, DailyClose close) = AliceReport(store);
            ReceiptDocument doc = ReceiptDocument.FromCloseSummary(
                report, close.Id, close.WhenUtc, "Ace Mini Mart", "123 Rizal St", null, null, close.Notes);

            await service.PrintCloseSummaryAsync(doc);
            IPrintJobStore jobs = host.GetRequiredService<IPrintJobStore>();
            PrintJob original = (await jobs.ListAsync(take: 50)).Single(j => j.ReprintOf == null);

            PrintOutcome reprint = await service.ReprintCloseAsync(close.Id);

            Assert.Equal(PrintJobStatus.Printed, reprint.Status);
            Assert.True(reprint.IsReprint);
            Assert.Equal(original.ReceiptNumber, reprint.ReceiptNumber);
            Assert.Equal(2, printer.Printed.Count);

            string newest = Decode(printer.Encoded[^1]);
            Assert.Contains("REPRINT", newest, StringComparison.Ordinal);
            Assert.Contains(close.Id.Value.ToString("D"), newest, StringComparison.Ordinal); // original close identity kept

            IReadOnlyList<PrintJob> all = await jobs.ListAsync(take: 50);
            Assert.Equal(2, all.Count);
            Assert.True(all[0].IsReprint); // newest = the reprint clone
            Assert.Equal(original.Id, all[0].ReprintOf);
        }
        finally
        {
            Cleanup(host, root);
        }
    }
}
