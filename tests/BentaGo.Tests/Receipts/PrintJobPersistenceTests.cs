using System.IO;
using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Receipts;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Receipts;

/// <summary>
/// BG-007a print-job persistence: one sale -> one recoverable job; retry
/// updates in place (attempts+1, never a second row); reprints clone the job
/// with ReprintOf + IsReprint and keep the receipt number. The printable
/// snapshot survives a restart (stored JSON).
/// </summary>
public sealed class PrintJobPersistenceTests
{
    private static ServiceProvider NewHost(string root) =>
        BentaGoHost.Build(development: true, dataRootOverride: root);

    private static async Task<(ServiceProvider Host, string Root)> NewMigratedAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "bentago-printjob-" + Guid.NewGuid().ToString("N"));
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        return (host, root);
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

    private static PrintJob NewJob(string receipt = "R-001") => new(
        new PrintJobId(Guid.NewGuid()),
        new SaleId(Guid.NewGuid()),
        receipt,
        """{"receipt":"R-001"}""",
        PrintJobStatus.Queued,
        Attempts: 0,
        LastErrorCode: null,
        DateTimeOffset.UtcNow,
        LastAttemptAt: null,
        ReprintOf: null);

    [Fact]
    public async Task Enqueue_SurvivesRestart()
    {
        (ServiceProvider host, string root) = await NewMigratedAsync();
        var store = host.GetRequiredService<IPrintJobStore>();
        PrintJob job = NewJob("R-20260830-001");
        await store.EnqueueAsync(job);

        try
        {
            await using var fresh = NewHost(root);
            var store2 = fresh.GetRequiredService<IPrintJobStore>();
            PrintJob? loaded = await store2.FindByIdAsync(job.Id);

            Assert.NotNull(loaded);
            Assert.Equal(job.ReceiptNumber, loaded.ReceiptNumber);
            Assert.Equal(job.ReceiptDocumentJson, loaded.ReceiptDocumentJson);
            Assert.Equal(PrintJobStatus.Queued, loaded.Status);
        }
        finally
        {
            Cleanup(host, root);
        }
    }

    [Fact]
    public async Task Retry_UpdatesSameJob()
    {
        (ServiceProvider host, string root) = await NewMigratedAsync();
        try
        {
            var store = host.GetRequiredService<IPrintJobStore>();
            PrintJob job = NewJob();
            await store.EnqueueAsync(job);

            bool updated = await store.TryUpdateAsync(
                job.Id, PrintJobStatus.Failed, 1, "UNPLUGGED", DateTimeOffset.UtcNow);
            PrintJob? afterFail = await store.FindByIdAsync(job.Id);
            bool retried = await store.TryUpdateAsync(
                job.Id, PrintJobStatus.Printed, 2, null, DateTimeOffset.UtcNow);
            PrintJob? afterRetry = await store.FindByIdAsync(job.Id);

            Assert.True(updated);
            Assert.True(retried);
            Assert.NotNull(afterFail);
            Assert.Equal(PrintJobStatus.Failed, afterFail!.Status);
            Assert.Equal("UNPLUGGED", afterFail.LastErrorCode);
            Assert.NotNull(afterRetry);
            Assert.Equal(PrintJobStatus.Printed, afterRetry!.Status);
            Assert.Equal(2, afterRetry.Attempts);
        }
        finally
        {
            Cleanup(host, root);
        }
    }

    [Fact]
    public async Task Reprint_ClonesJob_KeepsNumber_FlagsLabel()
    {
        (ServiceProvider host, string root) = await NewMigratedAsync();
        try
        {
            var store = host.GetRequiredService<IPrintJobStore>();
            PrintJob original = NewJob("R-20260830-002");
            await store.EnqueueAsync(original);

            var reprint = original with
            {
                Id = new PrintJobId(Guid.NewGuid()),
                Status = PrintJobStatus.Printed,
                Attempts = 1,
                CreatedUtc = original.CreatedUtc.AddMinutes(1),
                LastAttemptAt = DateTimeOffset.UtcNow,
                ReprintOf = original.Id,
            };
            await store.EnqueueAsync(reprint);

            PrintJob? origin = await store.FindBySaleAsync(original.SaleId, reprintInclusive: false);
            PrintJob? clone = await store.FindByIdAsync(reprint.Id);

            Assert.NotNull(origin);
            Assert.False(origin!.IsReprint);
            Assert.Equal(original.Id, origin.Id);
            Assert.NotNull(clone);
            Assert.True(clone!.IsReprint);
            Assert.Equal(original.Id, clone.ReprintOf);
            Assert.Equal(original.ReceiptNumber, clone.ReceiptNumber); // original number kept
        }
        finally
        {
            Cleanup(host, root);
        }
    }

    [Fact]
    public async Task Enqueue_IsIdempotentPerJobId()
    {
        (ServiceProvider host, string root) = await NewMigratedAsync();
        try
        {
            var store = host.GetRequiredService<IPrintJobStore>();
            PrintJob job = NewJob("R-20260830-003");

            await store.EnqueueAsync(job);
            await store.EnqueueAsync(job); // same id: replaced, not duplicated

            IReadOnlyList<PrintJob> all = await store.ListAsync(take: 50);
            Assert.Single(all, j => j.Id == job.Id);
        }
        finally
        {
            Cleanup(host, root);
        }
    }
}
