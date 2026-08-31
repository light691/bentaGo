using System.IO;
using System.Text;
using System.Windows;
using BentaGo.App;
using BentaGo.App.Dialogs;
using BentaGo.App.Dialogs.Payment;
using BentaGo.App.Features.Admin;
using BentaGo.App.Features.Sales;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Receipts;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Printing;
using BentaGo.Tests.AppShell;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell.LiveLanes;

/// <summary>
/// BG-007 live lanes L1-L5 (docs/05 "Verify, live", docs/03 §7): the
/// in-process STA driver exercises the software-side print + scanner
/// pipeline against the <see cref="FakeReceiptPrinter"/> adapter (the default
/// host printer). All five lanes are env-gated (BENTAGO_LIVE_LANES=1) so the
/// mandated --filter AppShell run stays headless.
/// </summary>
[Collection("AppShellSta")]
public sealed class Bg007PrintLanes
{
    private const string TestProductName = "Sardines";

    private readonly AppShellStaFixture _sta;

    public Bg007PrintLanes(AppShellStaFixture sta) => _sta = sta;

    private static bool LiveLanesEnabled => Environment.GetEnvironmentVariable("BENTAGO_LIVE_LANES") == "1";

    // ------- Lane 1: test receipt -------

    [Fact]
    public async Task L1_TestReceiptPrintsTwoLineDocument()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await PrintLaneSession.OpenAsync();
            FakeReceiptPrinter printer = session.Printer;

            // Drive the REAL admin-devices screen (the shell resolves the VM
            // from the host provider), activate, then issue the test print.
            session.Navigation.Navigate(ScreenKeys.AdminDevices);
            await WaitUntilAsync(() => session.Navigation.CurrentKey == ScreenKeys.AdminDevices);
            var admin = (AdminDevicesViewModel)session.ViewModel.Current!;
            await admin.OnActivatedAsync();
            admin.TestPrintCommand.Execute(null);
            await WaitUntilAsync(() => printer.Printed.Count == 1);

            _sta.PumpIdle();
            session.ShotOfCurrent("bg-007-test-receipt.png");
            Bg007LaneEvidence.SaveReviewPng("bg-007-test-receipt.png", 1366, 768, session.Window);

            bool twoLine = printer.Printed.Count == 1 && printer.Printed[0].Lines.Count == 2;
            bool statusShown = admin.StatusText is not null && admin.StatusText.Contains("Test receipt", StringComparison.Ordinal);
            bool pass = twoLine && statusShown;
            await Bg007LaneEvidence.WriteVerdictAsync(
                "bg-007-l1", "L1 test receipt",
                pass ? Bg007LaneEvidence.VerdictPass : Bg007LaneEvidence.VerdictFail,
                $"lines={(printer.Printed.Count == 1 ? printer.Printed[0].Lines.Count : 0)} status='{admin.StatusText}'",
                "bg-007-test-receipt.png");
        });
    }

    // ------- Lane 2: unplugged at checkout -------

    [Fact]
    public async Task L2_UnpluggedPrinterLeavesOneSaleAndAttention()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await PrintLaneSession.OpenAsync();
            await session.OpenSalesAsync();
            FakeReceiptPrinter printer = session.Printer;
            IPrintJobStore jobs = session.PrintJobs;

            TypeScan(session, SalesSeed.SardinesSku);
            await WaitUntilAsync(() => session.Sales.HasCartItems);

            // Unplug the printer, then complete one cash sale.
            printer.FailNext();
            await DriveCannedCashSaleAsync(session);

            _sta.PumpIdle();
            session.ShotOfCurrent("bg-007-printer-attention.png");
            Bg007LaneEvidence.SaveReviewPng("bg-007-printer-attention.png", 1366, 768, session.Window);

            Result<IReadOnlyList<SaleHistoryRow>> rows = session.QuerySales();
            bool oneSale = rows.IsSuccess && rows.Value.Count == 1;
            SaleId saleId = oneSale ? rows.Value[0].Id : default;
            PrintJob? job = await jobs.FindBySaleAsync(saleId, reprintInclusive: false);
            bool oneJob = job is not null && job.Status is PrintJobStatus.Failed or PrintJobStatus.Queued;
            bool attention = session.Sales.HasReceipt
                && session.Sales.HasPrintAttention
                && session.Sales.PrintStatusText is not null
                && session.Sales.PrintStatusText.Contains("Printer needs attention", StringComparison.Ordinal);
            ProductHit? sardines = (await session.Search(SalesSeed.SardinesSku)).Value[0];
            bool stockStill49 = sardines.OnHand.Units == 49;
            bool cash4500 = session.Store.Context.State.FundsOnHand(Fund.PhysicalCash).Centavos == 4500;

            bool pass = oneSale && oneJob && attention && stockStill49 && cash4500;
            await Bg007LaneEvidence.WriteVerdictAsync(
                "bg-007-l2", "L2 unplugged printer",
                pass ? Bg007LaneEvidence.VerdictPass : Bg007LaneEvidence.VerdictFail,
                $"oneSale={oneSale} jobStatus={job?.Status} attn={attention} stock={stockStill49} cash={cash4500}",
                "bg-007-printer-attention.png");
        });
    }

    // ------- Lane 3: reconnect + retry -------

    [Fact]
    public async Task L3_ReconnectRetryPrintsSameJob()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await PrintLaneSession.OpenAsync();
            await session.OpenSalesAsync();
            FakeReceiptPrinter printer = session.Printer;
            IPrintJobStore jobs = session.PrintJobs;

            TypeScan(session, SalesSeed.SardinesSku);
            await WaitUntilAsync(() => session.Sales.HasCartItems);

            // Establish a failed print, capture its job id, then reconnect
            // (clear the failure) and retry the SAME job.
            printer.FailNext();
            await DriveCannedCashSaleAsync(session);
            SaleId saleId = session.QuerySales().Value[0].Id;
            PrintJob before = (await jobs.FindBySaleAsync(saleId, reprintInclusive: false))!;

            int encodedBefore = printer.Encoded.Count; // the failed attempt's bytes (recorded by the adapter)
            printer.ClearFailures();
            PrintOutcome outcome = await session.Print.RetryAsync(saleId);

            _sta.PumpIdle();
            session.ShotOfCurrent("bg-007-retry.png");
            Bg007LaneEvidence.SaveReviewPng("bg-007-retry.png", 1366, 768, session.Window);

            PrintJob after = (await jobs.FindBySaleAsync(saleId, reprintInclusive: false))!;
            bool onePrint = printer.Printed.Count == 1;
            bool jobUnchanged = after.Id == before.Id;
            bool oneSale = session.QuerySales().Value.Count == 1;
            bool exactlyOneNewEncode = printer.Encoded.Count - encodedBefore == 1;
            bool encodedMatches = printer.Encoded.Count >= 1
                && printer.Encoded[^1].SequenceEqual(new EscPos80mmEncoder().Encode(printer.Printed[0]));
            bool printed = after.Status == PrintJobStatus.Printed;

            bool pass = onePrint && jobUnchanged && oneSale && exactlyOneNewEncode
                && encodedMatches && printed && outcome.Status == PrintJobStatus.Printed;
            await Bg007LaneEvidence.WriteVerdictAsync(
                "bg-007-l3", "L3 reconnect + retry",
                pass ? Bg007LaneEvidence.VerdictPass : Bg007LaneEvidence.VerdictFail,
                $"onePrint={onePrint} jobUnchanged={jobUnchanged} oneSale={oneSale} " +
                $"newEncode={printer.Encoded.Count - encodedBefore} encodedMatches={encodedMatches} printed={printed}",
                "bg-007-retry.png");
        });
    }

    // ------- Lane 4: reprint a successful receipt -------

    [Fact]
    public async Task L4_ReprintClonesJobWithLabel()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await PrintLaneSession.OpenAsync();
            await session.OpenSalesAsync();
            FakeReceiptPrinter printer = session.Printer;
            IPrintJobStore jobs = session.PrintJobs;

            TypeScan(session, SalesSeed.SardinesSku);
            await WaitUntilAsync(() => session.Sales.HasCartItems);

            // First a clean successful sale -> job 1 (Printed).
            await DriveCannedCashSaleAsync(session);
            SaleId saleId = session.QuerySales().Value[0].Id;
            string receiptNumber = session.QuerySales().Value[0].ReceiptNumber;
            PrintJob original = (await jobs.FindBySaleAsync(saleId, reprintInclusive: false))!;

            session.Sales.ReprintCommand.Execute(null);
            PrintJob clone = await WaitForPrintAsync(async () =>
            {
                IReadOnlyList<PrintJob> list = await jobs.ListAsync();
                return list.FirstOrDefault(j => j.ReprintOf is not null);
            }, PrintJobStatus.Printed);

            _sta.PumpIdle();
            session.ShotOfCurrent("bg-007-reprint.png");
            Bg007LaneEvidence.SaveReviewPng("bg-007-reprint.png", 1366, 768, session.Window);

            IReadOnlyList<PrintJob> all = await jobs.ListAsync();
            bool twoJobs = all.Count == 2;
            bool clonePointsAtOriginal = clone.ReprintOf == original.Id;
            bool sameReceipt = clone.ReceiptNumber == original.ReceiptNumber
                && clone.ReceiptNumber == receiptNumber;
            string newest = Encoding.UTF8.GetString(printer.Encoded[^1]);
            bool hasReprint = newest.Contains("REPRINT", StringComparison.Ordinal);
            bool hasReceiptNumber = newest.Contains(receiptNumber, StringComparison.Ordinal);
            bool oneSale = session.QuerySales().Value.Count == 1;

            bool pass = twoJobs && clonePointsAtOriginal && sameReceipt
                && hasReprint && hasReceiptNumber && oneSale;
            await Bg007LaneEvidence.WriteVerdictAsync(
                "bg-007-l4", "L4 reprint",
                pass ? Bg007LaneEvidence.VerdictPass : Bg007LaneEvidence.VerdictFail,
                $"jobs={all.Count} cloneReprintOf={clone.ReprintOf == original.Id} " +
                $"receiptEqual={sameReceipt} hasREPRINT={hasReprint} oneSale={oneSale}",
                "bg-007-reprint.png");
        });
    }

    // ------- Lane 5: scanner raw diagnostics -------

    [Fact]
    public async Task L5_ScannerDiagnosticsRecordRawSuffix()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await PrintLaneSession.OpenAsync();
            await session.OpenSalesAsync();
            IScannerDiagnostics scanner = session.Scanner;

            // The scanner wedge delivers the exact suffix in the box text;
            // the diagnostics must record it verbatim before any trim.
            string typed = SalesSeed.SardinesSku + "\r";
            TypeScan(session, typed);
            await WaitUntilAsync(() => session.Sales.HasCartItems);

            session.Navigation.Navigate(ScreenKeys.AdminDevices);
            await WaitUntilAsync(() => session.Navigation.CurrentKey == ScreenKeys.AdminDevices);
            var admin = (AdminDevicesViewModel)session.ViewModel.Current!;
            await admin.OnActivatedAsync();
            _sta.PumpIdle();
            session.ShotOfCurrent("bg-007-scanner-diagnostics.png");
            Bg007LaneEvidence.SaveReviewPng("bg-007-scanner-diagnostics.png", 1366, 768, session.Window);

            IReadOnlyList<ScanInputEntry> recent = scanner.Recent;
            bool one = recent.Count == 1;
            bool rawExact = one && recent[0].Raw == typed;
            bool suffixCr = one && recent[0].Suffix == "\r";
            bool matched = one && recent[0].MatchedProduct == TestProductName;
            string rawShown = one ? recent[0].Raw : "<none>";

            bool pass = one && rawExact && suffixCr && matched;
            await Bg007LaneEvidence.WriteVerdictAsync(
                "bg-007-l5", "L5 scanner diagnostics",
                pass ? Bg007LaneEvidence.VerdictPass : Bg007LaneEvidence.VerdictFail,
                $"one={one} raw='{rawShown}' suffix='{(one ? recent[0].Suffix : "")}' matched='{matched}'",
                "bg-007-scanner-diagnostics.png");
        });
    }

    // ------- helpers -------

    private static void TypeScan(PrintLaneSession session, string text)
    {
        session.Sales.ScanBoxText = text;
        session.Sales.ScanCommand.Execute(null);
    }

    private static async Task DriveCannedCashSaleAsync(PrintLaneSession session)
    {
        session.Dialogs.CannedResult = new PaymentDialogResult(
            new[] { new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(45m), null) },
            Money.Zero);
        session.Sales.CheckoutCommand.Execute(null);
        try
        {
            await WaitUntilAsync(() => session.Sales.HasReceipt && session.Sales.PrintStatusText is not null);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"checkout did not finish: dialogsOpen={session.Dialogs.IsOpen} " +
                $"error='{session.Sales.ActionError}' state='{session.Sales.SessionStateMessage}' " +
                $"cart={session.Sales.Cart.Count}");
        }
    }

    private static async Task<PrintJob> WaitForPrintAsync(
        Func<Task<PrintJob?>> probe, PrintJobStatus status, int timeoutMs = 15_000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            PrintJob? job = await probe();
            if (job is not null && job.Status == status)
            {
                return job;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("print job wait timed out");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("lane wait timed out");
    }

    // ====================================================================
    // Session
    // ====================================================================

    /// <summary>
    /// One lane session: an isolated migrated root seeded by <see cref="SalesSeed"/>,
    /// a visible real MainWindow, seller login, and the services a lane asserts
    /// against. Disposing closes the window and, for temp roots, deletes the data.
    /// </summary>
    private sealed class PrintLaneSession : IDisposable
    {
        private readonly string? _rootToDelete;

        public PrintLaneSession(string root, string? rootToDelete, ServiceProvider services, MainWindow window,
            MainViewModel viewModel, LaneDialogService dialogs, INavigationService navigation, SqliteLedgerStore store)
        {
            Root = root;
            _rootToDelete = rootToDelete;
            Services = services;
            Window = window;
            ViewModel = viewModel;
            Dialogs = dialogs;
            Navigation = navigation;
            Store = store;
        }

        public string Root { get; }
        public ServiceProvider Services { get; }
        public MainWindow Window { get; }
        public MainViewModel ViewModel { get; }
        public LaneDialogService Dialogs { get; }
        public INavigationService Navigation { get; }
        public SqliteLedgerStore Store { get; }

        public SalesViewModel Sales => (SalesViewModel)ViewModel.Current!;
        public DayKey Day => DayKey.FromUtc(DateTimeOffset.UtcNow);

        public FakeReceiptPrinter Printer => (FakeReceiptPrinter)Services.GetRequiredService<IReceiptPrinter>();
        public IPrintJobStore PrintJobs => Services.GetRequiredService<IPrintJobStore>();
        public IPrintService Print => Services.GetRequiredService<IPrintService>();
        public IScannerDiagnostics Scanner => Services.GetRequiredService<IScannerDiagnostics>();

        public static async Task<PrintLaneSession> OpenAsync(int width = 1366, int height = 768, string? root = null, bool seed = true)
        {
            string? deleteRoot = null;
            string temp = root ?? Path.Combine(Path.GetTempPath(), "bentago-007-" + Guid.NewGuid().ToString("N"));
            if (root is null)
            {
                deleteRoot = temp;
            }

            var services = BentaGoHost.Build(
                development: true,
                dataRootOverride: temp,
                extra: s =>
                {
                    ObservableObjectFactory.RegisterAppServices(s);
                    s.AddSingleton<IDialogService, LaneDialogService>();
                });
            if (seed)
            {
                SalesSeed.Seed(services);
            }

            var viewModel = services.GetRequiredService<MainViewModel>();
            var window = new MainWindow(viewModel)
            {
                Width = width,
                Height = height,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 60,
                Top = 40,
            };
            window.Show();
            viewModel.Boot();
            window.Activate();

            viewModel.Login.Username = SalesSeed.SellerUsername;
            viewModel.Login.Password = SalesSeed.SellerPassword;
            viewModel.Login.LoginCommand.Execute(null);
            await WaitUntilAsync(() => services.GetRequiredService<INavigationService>().CurrentKey == ScreenKeys.Home);

            var store = services.GetRequiredService<SqliteLedgerStore>();
            var dialogs = (LaneDialogService)services.GetRequiredService<IDialogService>();
            var nav = services.GetRequiredService<INavigationService>();
            return new PrintLaneSession(temp, deleteRoot, services, window, viewModel, dialogs, nav, store);
        }

        public async Task OpenSalesAsync()
        {
            Navigation.Navigate(ScreenKeys.Sales);
            try
            {
                await WaitUntilAsync(() => Sales.IsSessionReady);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"key={Navigation.CurrentKey} ready={Sales.IsSessionReady} " +
                    $"state='{Sales.SessionStateMessage}' error='{Sales.ActionError}' " +
                    $"day={Day}");
            }
        }

        public Task<Result<IReadOnlyList<ProductHit>>> Search(string query) =>
            Services.GetRequiredService<IProductSearchService>().SearchAsync(query);

        public Result<IReadOnlyList<SaleHistoryRow>> QuerySales() =>
            Services.GetRequiredService<ISaleQueryService>().Query();

        public void ShotOfCurrent(string fileName)
        {
            Bg007LaneEvidence.SavePng(fileName, 1366, 768, Window);
        }

        public void Dispose()
        {
            Window.Close();
            Services.Dispose();
            if (_rootToDelete is not null)
            {
                try
                {
                    Directory.Delete(_rootToDelete, recursive: true);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }
}
