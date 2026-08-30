using System.IO;
using System.Text;
using System.Windows;
using BentaGo.App;
using BentaGo.App.Dialogs;
using BentaGo.App.Features.Reconciliation;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Receipts;
using BentaGo.Core.Domain.Reconciliation;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Printing;
using BentaGo.Infrastructure.Security;
using BentaGo.Tests.AppShell;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell.LiveLanes;

/// <summary>
/// BG-008 live lanes L8-L9 (docs/05 480-481): the reconciliation screen exports
/// transaction + reconciliation CSV whose totals equal the screen and the
/// ledger, and prints a close summary whose payment/variance totals fit the
/// configured 42-column paper. Env-gated (BENTAGO_LIVE_LANES=1) like BG-007.
/// </summary>
[Collection("AppShellSta")]
public sealed class Bg008Lanes
{
    private const string AliceUsername = "bg008-alice";
    private const string AlicePassword = "bg008-alice";

    private readonly AppShellStaFixture _sta;

    public Bg008Lanes(AppShellStaFixture sta) => _sta = sta;

    private static bool LiveLanesEnabled => Environment.GetEnvironmentVariable("BENTAGO_LIVE_LANES") == "1";

    private static string CsvCell(string row, int index)
    {
        // RFC-4180 cell read (no embedded newlines in the assertion rows).
        var cells = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < row.Length; i++)
        {
            char c = row[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < row.Length && row[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        cells.Add(current.ToString());
        return index < cells.Count ? cells[index] : "";
    }

    // ------- Lane 8: exports totals == screen == ledger -------

    [Fact]
    public async Task L8_ExportsTotalsEqualScreenAndLedger()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await Bg008LaneSession.OpenAsync();
            session.Navigation.Navigate(ScreenKeys.Reconciliation);
            await WaitUntilAsync(() => session.Navigation.CurrentKey == ScreenKeys.Reconciliation);
            var vm = (ReconciliationViewModel)session.ViewModel.Current!;

            // The operator counts 180.00: the banner must show SHORT 104.00.
            vm.ActualCashText = "180";
            _sta.PumpIdle();
            bool bannerShort = vm.IsShort && vm.BannerText.Contains("SHORT", StringComparison.Ordinal) &&
                vm.BannerText.Contains("104", StringComparison.Ordinal);

            vm.ExportReconciliationCsvCommand.Execute(null);
            vm.ExportTransactionsCsvCommand.Execute(null);
            await WaitUntilAsync(() => vm.HasExportStatus && vm.ExportStatusText.Contains("Exported", StringComparison.Ordinal));

            string reconPath = Path.Combine(session.Paths.DataDir, "reconciliation.csv");
            string txnPath = Path.Combine(session.Paths.DataDir, "transactions.csv");
            bool filesExist = File.Exists(reconPath) && File.Exists(txnPath);

            // Report + ledger assertions.
            Result<ReconciliationReport> report = session.Reports.Build(session.Alice, session.AliceTrip);
            bool reportOk = report.IsSuccess;
            var totals = report.Value.Totals;
            bool ledgerCash = session.Store.State.TripCash(session.AliceTrip, Fund.PhysicalCash) == totals.ExpectedCash;

            // Reconciliation CSV: the Physical Cash row carries 284 / 180 / 104.
            string reconCsv = filesExist ? File.ReadAllText(reconPath) : "";
            string[] reconLines = reconCsv.TrimEnd('\n').Split('\n');
            int cashRow = -1;
            for (int i = 0; i < reconLines.Length; i++)
            {
                if (reconLines[i].StartsWith("Cash,", StringComparison.Ordinal)
                    && CsvCell(reconLines[i], 1) == "Physical Cash")
                {
                    cashRow = i;
                    break;
                }
            }
            bool reconTotals = cashRow >= 0
                && CsvCell(reconLines[cashRow], 2) == totals.ExpectedCash.ToPesos().ToString("0.00")
                && CsvCell(reconLines[cashRow], 3) == totals.CountedCash.ToPesos().ToString("0.00")
                && CsvCell(reconLines[cashRow], 4) == totals.CashVariance.ToPesos().ToString("0.00");

            // Transaction CSV header + at least one committed sale row.
            string txnCsv = filesExist ? File.ReadAllText(txnPath) : "";
            bool txnOk = txnCsv.StartsWith("Date,Receipt,Lines,Methods,Amount,Status\n", StringComparison.Ordinal)
                && txnCsv.Contains("R-000001", StringComparison.Ordinal)
                && txnCsv.Contains("R-000004", StringComparison.Ordinal);

            _sta.PumpIdle();
            session.ShotOfCurrent("bg-008-exports.png");
            Bg008LaneEvidence.SaveReviewPng("bg-008-exports.png", 1366, 768, session.Window);

            bool pass = bannerShort && filesExist && reportOk && ledgerCash && reconTotals && txnOk;
            await Bg008LaneEvidence.WriteVerdictAsync(
                "bg-008-l8", "L8 exports totals == screen == ledger",
                pass ? Bg008LaneEvidence.VerdictPass : Bg008LaneEvidence.VerdictFail,
                $"screenShort={bannerShort} files={filesExist} reportOk={reportOk} ledgerCash={ledgerCash} " +
                $"reconTotals={reconTotals} txn={txnOk}",
                "bg-008-exports.png");
        });
    }

    // ------- Lane 9: close summary print fits 42 columns -------

    [Fact]
    public async Task L9_CloseSummaryPrintFits42Columns()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await Bg008LaneSession.OpenAsync();
            FakeReceiptPrinter printer = session.Printer;

            session.Navigation.Navigate(ScreenKeys.Reconciliation);
            await WaitUntilAsync(() => session.Navigation.CurrentKey == ScreenKeys.Reconciliation);
            var vm = (ReconciliationViewModel)session.ViewModel.Current!;
            vm.ActualCashText = "180";
            _sta.PumpIdle();

            vm.PrintSummaryCommand.Execute(null);
            await WaitUntilAsync(() => printer.Printed.Count == 1);

            _sta.PumpIdle();
            session.ShotOfCurrent("bg-008-close-print.png");
            Bg008LaneEvidence.SaveReviewPng("bg-008-close-print.png", 1366, 768, session.Window);

            // Decode the LAST encoded buffer, split into print lines, and assert
            // the longest line is within the configured 42-column paper width.
            byte[] encoded = printer.Encoded.Count > 0
                ? printer.Encoded[^1]
                : Array.Empty<byte>();
            string[] printLines = encoded.Length > 0
                ? Encoding.UTF8.GetString(encoded)
                    .Replace("\u001B@", string.Empty, StringComparison.Ordinal)
                    .Replace("\u001BE\u0001", string.Empty, StringComparison.Ordinal)
                    .Replace("\u001BE\u0000", string.Empty, StringComparison.Ordinal)
                    .Replace("\u001DV\u0042", string.Empty, StringComparison.Ordinal)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();
            int longest = printLines.Length == 0 ? 0 : printLines.Max(l => l.Length);
            bool fits = longest > 0 && longest <= EscPos80mmEncoder.NominalColumns;

            bool isCloseSummary = printer.Printed.Count == 1
                && printer.Printed[0].Kind == ReceiptKind.CloseSummary
                && printer.Printed[0].Close is not null;
            bool hasTotals = printLines.Any(l => l.Contains("CASH VARIANCE", StringComparison.Ordinal));

            bool pass = fits && isCloseSummary && hasTotals;
            await Bg008LaneEvidence.WriteVerdictAsync(
                "bg-008-l9", "L9 close summary print fits 42 columns",
                pass ? Bg008LaneEvidence.VerdictPass : Bg008LaneEvidence.VerdictFail,
                $"longest={longest} fits={fits} isClose={isCloseSummary} hasTotals={hasTotals} lines={printLines.Length}",
                "bg-008-close-print.png");
        });
    }

    // ------- Lane 10: the complete two-peddler scenario explains every
    // difference from the screen (report rows + notes + exports) -------

    [Fact]
    public async Task L10_TwoPeddlersReconcile_ExplainsEveryDifferenceFromScreen()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await Bg008LaneSession.OpenAsync();
            session.Navigation.Navigate(ScreenKeys.Reconciliation);
            await WaitUntilAsync(() => session.Navigation.CurrentKey == ScreenKeys.Reconciliation);
            var vm = (ReconciliationViewModel)session.ViewModel.Current!;

            // The full two-peddler day is folded: Alice's trip is closed (+reopened)
            // with counted cash 180.00 vs expected 284.00, so it SHORTs by 104.00.
            bool hasReport = vm.HasReport;
            bool hasClose = session.Store.Context.Closes.Any(c => c.Trip == session.AliceTrip);

            vm.ActualCashText = "180";
            _sta.PumpIdle();
            bool bannerShort = vm.IsShort
                && vm.BannerText.Contains("SHORT", StringComparison.Ordinal)
                && vm.BannerText.Contains("104", StringComparison.Ordinal);

            // Domain report: exactly the folds a reviewer reads.
            ReconciliationReport report = session.Reports.Build(session.Alice, session.AliceTrip).Value;
            bool cashExplained = report.Totals.ExpectedCash.Centavos == 28400
                && report.Totals.CountedCash.Centavos == 18000
                && report.Totals.CashVariance.Centavos == 10400
                && report.Totals.CashLabel == VarianceLabel.Shortage;
            bool stockExplained = report.StockRows.Single(r => r.Sku == "Sardines").CountedUnits == 38;
            bool everyLineSigned = report.StockRows.All(r =>
                (r.CountedUnits is null) == (r.QuantityVariance is null)
                && (r.CountedUnits is null) == (r.ValueVariance is null));

            // Screen report: the reviewer sees the same rows + notes.
            bool screenCash = hasReport
                && vm.Report!.CashLabelText == "SHORTAGE"
                && vm.Report.ExpectedCashText.Contains("284", StringComparison.Ordinal)
                && vm.Report.CountedCashText.Contains("180", StringComparison.Ordinal)
                && vm.Report.CashVarianceText.Contains("104", StringComparison.Ordinal);
            ReportStockRowView? sardines = hasReport
                ? vm.Report!.StockRows.SingleOrDefault(r => r.Sku == "Sardines")
                : null;
            bool screenStock = sardines is not null
                && sardines.ExpectedText == "36"
                && sardines.CountedText == "38"
                && sardines.QuantityVarianceText == "+2"
                && sardines.ValueVarianceText.Contains("90.00", StringComparison.Ordinal);

            // Exports surface the same explained differences.
            vm.ExportReconciliationCsvCommand.Execute(null);
            vm.ExportTransactionsCsvCommand.Execute(null);
            await WaitUntilAsync(() => vm.HasExportStatus && vm.ExportStatusText.Contains("Exported", StringComparison.Ordinal));
            bool filesExist = File.Exists(Path.Combine(session.Paths.DataDir, "reconciliation.csv"))
                && File.Exists(Path.Combine(session.Paths.DataDir, "transactions.csv"));
            string reconCsv = filesExist ? File.ReadAllText(Path.Combine(session.Paths.DataDir, "reconciliation.csv")) : "";
            bool reconExplains = reconCsv.Contains("Cash,Physical Cash,284.00,180.00,104.00,,SHORTAGE,", StringComparison.Ordinal)
                && reconCsv.Contains("Stock,Sardines - Sardines,36,38,2,90.00,,", StringComparison.Ordinal);

            _sta.PumpIdle();
            session.ShotOfCurrent("bg-008-two-peddlers.png");
            Bg008LaneEvidence.SaveReviewPng("bg-008-two-peddlers.png", 1366, 768, session.Window);

            bool pass = hasReport && hasClose && bannerShort && cashExplained && stockExplained
                && everyLineSigned && screenCash && screenStock && filesExist && reconExplains;
            await Bg008LaneEvidence.WriteVerdictAsync(
                "bg-008-l10", "L10 two-peddler reconcile explains every difference",
                pass ? Bg008LaneEvidence.VerdictPass : Bg008LaneEvidence.VerdictFail,
                $"hasReport={hasReport} hasClose={hasClose} bannerShort={bannerShort} " +
                $"cash={cashExplained} stock={stockExplained} everyLineSigned={everyLineSigned} " +
                $"screenCash={screenCash} screenStock={screenStock} files={filesExist} recon={reconExplains}",
                "bg-008-two-peddlers.png");
        });
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

    private sealed class Bg008LaneSession : IDisposable
    {
        private readonly string _rootToDelete;

        private Bg008LaneSession(string root, ServiceProvider services, MainWindow window, MainViewModel viewModel)
        {
            Root = root;
            _rootToDelete = root;
            Services = services;
            Window = window;
            ViewModel = viewModel;
            Navigation = services.GetRequiredService<INavigationService>();
            Store = services.GetRequiredService<SqliteLedgerStore>();
            Reports = services.GetRequiredService<IReconciliationReportService>();
            Paths = services.GetRequiredService<IDataPathProvider>();

            Store.Bind();
            DailyClose close = Store.Context.Closes
                .OrderBy(c => c.WhenUtc).ThenBy(c => c.Id.Value)
                .First();
            Trip trip = Store.Context.Trips.Single(t => t.Id == close.Trip);
            Alice = trip.Peddler;
            AliceTrip = trip.Id;
        }

        public string Root { get; }
        public ServiceProvider Services { get; }
        public MainWindow Window { get; }
        public MainViewModel ViewModel { get; }
        public INavigationService Navigation { get; }
        public SqliteLedgerStore Store { get; }
        public IReconciliationReportService Reports { get; }
        public IDataPathProvider Paths { get; }
        public PeddlerId Alice { get; }
        public TripId AliceTrip { get; }

        public FakeReceiptPrinter Printer => (FakeReceiptPrinter)Services.GetRequiredService<IReceiptPrinter>();

        public static async Task<Bg008LaneSession> OpenAsync(int width = 1366, int height = 768)
        {
            string root = Path.Combine(Path.GetTempPath(), "bentago-008-" + Guid.NewGuid().ToString("N"));
            var services = BentaGoHost.Build(
                development: true,
                dataRootOverride: root,
                extra: s => ObservableObjectFactory.RegisterAppServices(s));
            await services.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

            var store = services.GetRequiredService<SqliteLedgerStore>();
            _ = TwoPeddlersScenario.Run(store);

            // A peddler-linked account so the reconciliation screen's export +
            // close print resolve Alice's trip/close, matching the ledger fold.
            store.Bind();
            DailyClose close = store.Context.Closes
                .OrderBy(c => c.WhenUtc).ThenBy(c => c.Id.Value)
                .First();
            Trip trip = store.Context.Trips.Single(t => t.Id == close.Trip);
            await services.GetRequiredService<IUserAccountStore>().AddAsync(new User
            {
                Id = new UserId(Guid.NewGuid()),
                Username = AliceUsername,
                PasswordHash = services.GetRequiredService<IPasswordHasher>().Hash(AlicePassword),
                Role = Role.Peddler,
                PeddlerLink = trip.Peddler,
                CreatedBy = trip.CreatedBy,
                CreatedUtc = DateTimeOffset.UtcNow,
            });

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

            viewModel.Login.Username = AliceUsername;
            viewModel.Login.Password = AlicePassword;
            viewModel.Login.LoginCommand.Execute(null);
            await WaitUntilAsync(() => services.GetRequiredService<INavigationService>().CurrentKey == ScreenKeys.Home);

            return new Bg008LaneSession(root, services, window, viewModel);
        }

        public void ShotOfCurrent(string fileName)
        {
            Bg008LaneEvidence.SavePng(fileName, 1366, 768, Window);
        }

        public void Dispose()
        {
            Window.Close();
            Services.Dispose();
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
