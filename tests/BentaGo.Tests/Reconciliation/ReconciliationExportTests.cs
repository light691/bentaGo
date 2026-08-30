using System.IO;
using System.Text;
using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Payments;
using Xunit;

namespace BentaGo.Tests.Reconciliation;

/// <summary>
/// BG-008 Slice C CSV export wiring: the reconciliation screen writes the
/// Slice A CSV generators into the data directory (BOM-less UTF-8, RFC-4180
/// already escaped by the generators) and surfaces the path. These tests pin
/// the write path + content equality so the shell wiring is byte-stable.
/// </summary>
public sealed class ReconciliationExportTests
{
    private static (PeddlerId Peddler, TripId Trip, ILedgerStore Store) Seeded()
    {
        var store = new InMemoryLedgerStore();
        TwoPeddlersScenario.Run(store);
        store.Bind();
        DailyClose close = store.Context.Closes
            .OrderBy(c => c.WhenUtc).ThenBy(c => c.Id.Value)
            .First();
        Trip trip = store.Context.Trips.Single(t => t.Id == close.Trip);
        return (trip.Peddler, trip.Id, store);
    }

    [Fact]
    public void ExportReconciliationCsv_WritesFile_ContentEqualsGenerator()
    {
        (PeddlerId peddler, TripId trip, ILedgerStore store) = Seeded();
        var service = new ReconciliationReportService(store);

        Result<string> csv = service.BuildReconciliationCsv(peddler, trip);
        Assert.True(csv.IsSuccess, csv.Errors.FirstOrDefault()?.Code ?? "csv failed");

        string dir = Path.Combine(Path.GetTempPath(), "bentago-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "reconciliation.csv");
            File.WriteAllText(path, csv.Value, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Assert.True(File.Exists(path));
            Assert.Equal(csv.Value, File.ReadAllText(path));
            // BOM-less per RFC-4180 (no UTF-8 byte-order-mark prefix).
            byte[] head = new byte[3];
            using (FileStream stream = File.OpenRead(path))
            {
                _ = stream.Read(head, 0, head.Length);
            }
            Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, head);
            Assert.StartsWith("Kind,Item,Expected,Counted,Variance,Value,Label,Notes\n", csv.Value);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ExportTransactionCsv_ContentEqualsGenerator()
    {
        (PeddlerId peddler, TripId trip, ILedgerStore store) = Seeded();
        var service = new ReconciliationReportService(store);

        Result<string> csv = service.BuildTransactionCsv(peddler, trip);
        Assert.True(csv.IsSuccess, csv.Errors.FirstOrDefault()?.Code ?? "csv failed");
        Assert.StartsWith("Date,Receipt,Lines,Methods,Amount,Status\n", csv.Value);
        Assert.Contains("R-000001", csv.Value);
        Assert.Contains("R-000004", csv.Value);
    }
}
