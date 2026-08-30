using System.Diagnostics;
using System.IO;
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

namespace BentaGo.Tools;

/// <summary>
/// BG-007 perf probe (docs/05 lines 430-435): baselines FIRST (plain-text
/// 100-line render, fake-adapter transport), then the ESC/POS 100-line render
/// and the print-command→first-write path (enqueue + render + adapter write),
/// 30 runs after ONE warm-up. Rule: P95 first write ≤ 1 s, 100-line render
/// ≤ 200 ms.
/// </summary>
public static class PrintPerfRunner
{
    private const int Runs = 30;
    private const int WarmUp = 1;

    private static ReceiptDocument HugeDocument()
    {
        var when = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
        var lines = Enumerable.Range(0, 100)
            .Select(i => new ReceiptLine($"Sardines canned in tomato sauce 155g pack (item {i + 1})",
                Money.FromPesos(45m), Quantity.Of(1 + (i % 3)), Money.Zero))
            .ToList();
        return new ReceiptDocument(
            new ReceiptMeta("R-20260830-100", when, new PeddlerId(Guid.NewGuid()), new TripId(Guid.NewGuid())),
            lines,
            Money.FromPesos(4500m), Money.Zero, Money.FromPesos(4500m), Money.Zero,
            IsReprint: false, ReferenceNote: null);
    }

    public static async Task<int> RunAsync(string root, string outPath, string head)
    {
        Console.WriteLine("PRINT-PERF: baselines FIRST (docs/05 line 434)");
        var lines = new List<string>
        {
            "# BG-007 perf probe — 30 runs after 1 warm-up (fake adapter; real-device runs in the device pass)",
            $"head\t{head}",
        };

        ReceiptDocument doc = HugeDocument();

        // Baseline A: plain-text 100-line render (StringBuilder join).
        double plainMs = MeasureIgnoring(Runs, () =>
        {
            var sb = new System.Text.StringBuilder();
            foreach (ReceiptLine line in doc.Lines)
            {
                sb.Append(line.Name).Append(' ').Append(line.UnitPrice.ToPesos().ToString("0.00")).Append('\n');
            }
        });
        Console.WriteLine($"BASELINE_PLAIN_100_RENDER_P95_MS: {plainMs:F2}");
        lines.Add($"baseline\tplain-text 100-line render P95\t{plainMs:F2} ms");

        string root2 = Path.Combine(Path.GetTempPath(), "bentago-printperf-" + Guid.NewGuid().ToString("N"));
        ServiceProvider host = BentaGoHost.Build(development: true, dataRootOverride: root2);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        try
        {
            var printer = (FakeReceiptPrinter)host.GetRequiredService<IReceiptPrinter>();

            // Baseline B: fake-adapter transport (write only, no encode).
            double transportMs = MeasureIgnoring(Runs, () => printer.PrintAsync(doc).GetAwaiter().GetResult());
            Console.WriteLine($"BASELINE_FAKE_TRANSPORT_P95_MS: {transportMs:F2}");
            lines.Add($"baseline\tfake-adapter transport P95\t{transportMs:F2} ms");

            // Metric 1: 100-line ESC/POS encode.
            printer.Reset();
            var encoder = new EscPos80mmEncoder();
            double encodeMs = MeasureIgnoring(Runs, () => encoder.Encode(doc));
            Console.WriteLine($"METRIC_ESCPOS_100_LINE_RENDER_P95_MS: {encodeMs:F2}");
            lines.Add($"metric\tbg-007 ESC/POS 100-line render P95\t{encodeMs:F2} ms\trule <= 200 ms");

            // Metric 2: print command → first write (enqueue + render + write).
            var service = host.GetRequiredService<IPrintService>();
            var jobs = host.GetRequiredService<IPrintJobStore>();
            Sale sale = SaleOf();
            await service.PrintAfterCommitAsync(sale); // warm-up (1)

            // Diagnostic: the job enqueue alone (the suspected latency source).
            double enqueueMs = MeasureIgnoring(Runs, () =>
            {
                var probe = new PrintJob(new PrintJobId(Guid.NewGuid()), new SaleId(Guid.NewGuid()),
                    "R-DIAG", """{"diag":1}""", PrintJobStatus.Queued, 0, null, DateTimeOffset.UtcNow, null, null);
                jobs.EnqueueAsync(probe).GetAwaiter().GetResult();
            });
            Console.WriteLine($"DIAGNOSTIC_ENQUEUE_ALONE_P95_MS: {enqueueMs:F2}");

            double firstWriteMs = MeasureIgnoring(Runs, () =>
            {
                Sale fresh = SaleOf();
                service.PrintAfterCommitAsync(fresh).GetAwaiter().GetResult();
            });
            Console.WriteLine($"METRIC_PRINT_CMD_FIRST_WRITE_P95_MS: {firstWriteMs:F2}");
            lines.Add($"metric\tbg-007 print-command→first-write P95\t{firstWriteMs:F2} ms\trule <= 1000 ms");

            double renderP95 = encodeMs; // documented; the rule checks the bound
            bool ok = encodeMs <= 200 && firstWriteMs <= 1000;
            lines.Add($"verdict\tprint perf\t{(ok ? "PASS" : "FAIL")}");
            Console.WriteLine($"VERDICT: {(ok ? "PASS" : "FAIL")}");
        }
        finally
        {
            host.Dispose();
            try
            {
                Directory.Delete(root2, true);
            }
            catch (IOException)
            {
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllLinesAsync(outPath, lines);
        return 0;
    }

    private static Sale SaleOf(string receipt = "R-PERF")
    {
        var when = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
        return Sale.Create(
            new SaleId(Guid.NewGuid()), new TripId(Guid.NewGuid()), new PeddlerId(Guid.NewGuid()),
            new LocationId(Guid.NewGuid()), receipt, when, new UserId(Guid.NewGuid()),
            new[] { new SaleLine(new SaleLineId(Guid.NewGuid()), new ProductId(Guid.NewGuid()), "SKU", "Item",
                Money.FromPesos(45m), Quantity.Of(1), Money.Zero) },
            new[] { new PaymentLine(new PaymentLineId(Guid.NewGuid()), PaymentMethod.Cash, "",
                Money.FromPesos(45m), Money.FromPesos(50m), Money.FromPesos(5m)) });
    }

    private static double MeasureIgnoring(int runs, Action action)
    {
        var samples = new List<double>(runs - 1);
        for (int i = 0; i < runs; i++)
        {
            if (i < WarmUp)
            {
                action();
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
    }
}
