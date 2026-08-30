using System.IO;
using System.Text;
using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Receipts;
using BentaGo.Core.Domain.Trips;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Printing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Receipts;

/// <summary>
/// BG-007 print pipeline (docs/05 lanes 2-4, docs/03 §7): commit first, then
/// print; unplugged -> exactly one saved sale + one recoverable job; retry is
/// idempotent (same job, no duplicate sale/movement); reprint is a NEW job
/// with the REPRINT label and the original receipt number.
/// </summary>
public sealed class PrintPipelineTests
{
    private static Sale SampleSale(string receipt = "R-20260830-001")
    {
        var when = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
        return Sale.Create(
            new SaleId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new TripId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            new PeddlerId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
            new LocationId(Guid.Parse("99999999-9999-9999-9999-999999999999")),
            receipt,
            when,
            new UserId(Guid.Parse("44444444-4444-4444-4444-444444444444")),
            new[]
            {
                new SaleLine(
                    new SaleLineId(Guid.Parse("55555555-5555-5555-5555-555555555556")),
                    new ProductId(Guid.Parse("66666666-6666-6666-6666-666666666666")),
                    "SARDINES", "Sardines",
                    Money.FromPesos(45m), Quantity.Of(1), Money.Zero),
            },
            new[]
            {
                new PaymentLine(
                    new PaymentLineId(Guid.Parse("77777777-7777-7777-7777-777777777778")),
                    PaymentMethod.Cash, "", Money.FromPesos(45m), Money.FromPesos(50m), Money.FromPesos(5m)),
            });
    }

    private static async Task<(ServiceProvider Host, string Root)> NewHostAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "bentago-printpipe-" + Guid.NewGuid().ToString("N"));
        ServiceProvider host = BentaGoHost.Build(development: true, dataRootOverride: root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        return (host, root);
    }

    private static FakeReceiptPrinter Fake(ServiceProvider host) =>
        (FakeReceiptPrinter)host.GetRequiredService<IReceiptPrinter>();

    [Fact]
    public async Task PrintAfterCommit_PrintsOnce_AndPersistsPrintedJob()
    {
        (ServiceProvider host, string root) = await NewHostAsync();
        try
        {
            var printer = Fake(host);
            var service = host.GetRequiredService<IPrintService>();
            Sale sale = SampleSale();

            PrintOutcome outcome = await service.PrintAfterCommitAsync(sale);

            Assert.Equal(PrintJobStatus.Printed, outcome.Status);
            Assert.False(outcome.IsReprint);
            Assert.Single(printer.Printed);
            Assert.Equal("R-20260830-001", outcome.ReceiptNumber);
            PrintJob? job = await host.GetRequiredService<IPrintJobStore>()
                .FindBySaleAsync(sale.Id, reprintInclusive: false);
            Assert.NotNull(job);
            Assert.Equal(PrintJobStatus.Printed, job!.Status);
            Assert.Equal(1, job.Attempts);
            Assert.False(job.IsReprint);
        }
        finally
        {
            host.Dispose();
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Unplugged_SaleSavedOnce_JobRecoverable_ThenRetryIdempotent()
    {
        (ServiceProvider host, string root) = await NewHostAsync();
        try
        {
            var printer = Fake(host);
            var service = host.GetRequiredService<IPrintService>();
            Sale sale = SampleSale("R-20260830-002");

            printer.FailNext();
            PrintOutcome failed = await service.PrintAfterCommitAsync(sale);

            Assert.Equal(PrintJobStatus.Failed, failed.Status);
            Assert.Equal("UNPLUGGED", failed.ErrorCode);
            Assert.Empty(printer.Printed);
            Assert.Single(printer.Encoded); // the bytes were still rendered
            PrintJob? job = await host.GetRequiredService<IPrintJobStore>()
                .FindBySaleAsync(sale.Id, reprintInclusive: false);
            Assert.NotNull(job);
            Assert.Equal(PrintJobStatus.Failed, job!.Status);

            // Reconnect + retry: the SAME job, the SAME printable snapshot.
            printer.ClearFailures();
            PrintOutcome retried = await service.RetryAsync(sale.Id);

            Assert.Equal(PrintJobStatus.Printed, retried.Status);
            Assert.Single(printer.Printed);
            PrintJob? after = await host.GetRequiredService<IPrintJobStore>()
                .FindBySaleAsync(sale.Id, reprintInclusive: false);
            Assert.NotNull(after);
            Assert.Equal(job.Id, after!.Id); // same job row
            Assert.Equal(2, after.Attempts);
        }
        finally
        {
            host.Dispose();
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Reprint_NewJob_LabelledAndKeepsNumber()
    {
        (ServiceProvider host, string root) = await NewHostAsync();
        try
        {
            var printer = Fake(host);
            var service = host.GetRequiredService<IPrintService>();
            Sale sale = SampleSale("R-20260830-003");
            await service.PrintAfterCommitAsync(sale);

            PrintOutcome reprint = await service.ReprintAsync(sale);

            Assert.Equal(PrintJobStatus.Printed, reprint.Status);
            Assert.True(reprint.IsReprint);
            Assert.Equal("R-20260830-003", reprint.ReceiptNumber); // original number kept
            Assert.Equal(2, printer.Printed.Count);

            string text = Encoding.UTF8.GetString(printer.Encoded[1]);
            Assert.Contains("REPRINT", text, StringComparison.Ordinal);

            PrintJob? origin = await host.GetRequiredService<IPrintJobStore>()
                .FindBySaleAsync(sale.Id, reprintInclusive: false);
            IReadOnlyList<PrintJob> all = await host.GetRequiredService<IPrintJobStore>().ListAsync(take: 10);
            Assert.NotNull(origin);
            Assert.False(origin!.IsReprint);
            Assert.True(all[0].IsReprint); // newest job = the reprint clone
            Assert.Equal(origin.Id, all[0].ReprintOf);
        }
        finally
        {
            host.Dispose();
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Codec_RoundTripsSnapshot()
    {
        ReceiptDocument original = ReceiptDocument.FromSale(SampleSale());
        string json = Application.Services.ReceiptCodec.Serialize(original);
        ReceiptDocument restored = Application.Services.ReceiptCodec.Deserialize(json);

        Assert.Equal(original.Meta.ReceiptNumber, restored.Meta.ReceiptNumber);
        Assert.Equal(original.Meta.CreatedUtc, restored.Meta.CreatedUtc);
        Assert.Equal(original.Lines.Count, restored.Lines.Count);
        Assert.Equal(original.Lines[0].Name, restored.Lines[0].Name);
        Assert.Equal(original.Lines[0].UnitPrice.Centavos, restored.Lines[0].UnitPrice.Centavos);
        Assert.Equal(original.Lines[0].Quantity.Units, restored.Lines[0].Quantity.Units);
        Assert.Equal(original.Total.Centavos, restored.Total.Centavos);
        Assert.Equal(original.Change.Centavos, restored.Change.Centavos);
        Assert.Equal(original.IsReprint, restored.IsReprint);
    }

    [Theory]
    [InlineData("4801234567890\r", "\r")]
    [InlineData("4801234567890\n", "\n")]
    [InlineData("4801234567890\r\n", "\r\n")]
    [InlineData("4801234567890", "")]
    public void ScannerDiagnostics_ExtractsExactSuffix(string raw, string expected) =>
        Assert.Equal(expected, ScannerDiagnostics.SuffixOf(raw));

    [Fact]
    public void ScannerDiagnostics_RecordsRawWithSuffixAndProduct()
    {
        var diagnostics = new ScannerDiagnostics();
        diagnostics.RecordScan("4801234567890\r", "Sardines", DateTimeOffset.UtcNow);
        diagnostics.RecordScan("KOPI\r\n", null, DateTimeOffset.UtcNow);

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal("\r\n", diagnostics.Recent[0].Suffix); // newest first
        Assert.Equal("KOPI\r\n", diagnostics.Recent[0].Raw);
        Assert.Null(diagnostics.Recent[0].MatchedProduct);
        Assert.Equal("\r", diagnostics.Recent[1].Suffix);
        Assert.Equal("Sardines", diagnostics.Recent[1].MatchedProduct);
    }
}
