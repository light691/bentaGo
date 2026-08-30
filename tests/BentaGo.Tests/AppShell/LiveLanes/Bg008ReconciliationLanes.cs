using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BentaGo.App;
using BentaGo.App.Dialogs;
using BentaGo.App.Dialogs.Payment;
using BentaGo.App.Features.Reconciliation;
using BentaGo.App.Features.Sales;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.Application.Contracts;
using BentaGo.Application.Scenario;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Reconciliation;
using BentaGo.Core.Domain.Trips;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using BentaGo.Tests.AppShell;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell.LiveLanes;

/// <summary>
/// BG-008 live lanes L1-L7 (docs/05 471-479): the in-process STA driver opens
/// the REAL reconciliation screen (Alt+5 path) over the working ledger and
/// proves the Slice B wiring — the balanced close, the cash shortage/overage
/// with required notes, the stock variance lines, the draft that locks
/// nothing, the close that locks everything and persists its receipt, and the
/// admin-approved reopen that keeps the prior close visible. Each lane seeds
/// the same SalesSeed root plus approver accounts and drives a real cash sale
/// through the canned payment result. Env-gated (BENTAGO_LIVE_LANES=1) so the
/// mandated --filter AppShell run stays headless.
/// </summary>
[Collection("AppShellSta")]
public sealed class Bg008ReconciliationLanes
{
    private const string ErrorApprover = "A reopen needs an admin approval. Choose an admin or supervisor above.";
    private const string ErrorReason = "A reopen reason is required.";

    private readonly AppShellStaFixture _sta;

    public Bg008ReconciliationLanes(AppShellStaFixture sta) => _sta = sta;

    private static bool LiveLanesEnabled => Environment.GetEnvironmentVariable("BENTAGO_LIVE_LANES") == "1";

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
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

    // ------- L1: balanced close -------

    [Fact]
    public async Task L1_BalancedCloseMatchesCashAndStock()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await ReconciliationLaneSession.OpenAsync();
            await session.OpenSalesAsync();
            await session.SellCash45Async();
            await session.OpenReconciliationAsync();

            // Count every product at its expected units: the count then
            // matches the fold exactly.
            foreach (ReconciliationCountRow row in session.Reconciliation.CountRows)
            {
                row.CountedText = row.ExpectedText;
            }

            session.Reconciliation.SaveDraftCommand.Execute(null);
            session.Reconciliation.ActualCashText = "45.00"; // 1 x Sardines cash

            bool bannerBalanced = session.Reconciliation.IsBalanced
                && session.Reconciliation.BannerText == "BALANCED";
            bool expectedCash45 = session.Reconciliation.ExpectedCashText.Contains("45.00", StringComparison.Ordinal);
            bool draftSaved = session.Reconciliation.DraftSaved;

            session.ShotOfCurrent("bg-008-balanced.png");
            Bg008LaneEvidence.SaveReviewPng("bg-008-balanced.png", 1366, 768, session.Window);

            session.Reconciliation.SubmitCloseCommand.Execute(null);

            bool closed = session.Reconciliation.IsClosed
                && session.Store.State.Trip(session.Trip).Status == TripStatus.Closed;
            ReconciliationReport report = session.Report();
            bool cashMatched = report.Totals.ExpectedCash.Centavos == 4500
                && report.Totals.CountedCash.Centavos == 4500
                && report.Totals.CashLabel == VarianceLabel.Balanced;
            bool stockMatched = report.StockRows.All(r =>
                r.CountedUnits is { } counted && counted == r.ExpectedUnits
                && r.QuantityVariance == 0
                && r.ValueVariance is { } value && value.IsZero);
            bool balancedLabel = session.Reconciliation.Report!.CashLabelText == "BALANCED";

            bool pass = bannerBalanced && expectedCash45 && draftSaved && closed && cashMatched && stockMatched && balancedLabel;
            await Bg008LaneEvidence.WriteVerdictAsync(
                "bg-008-l1", "L1 balanced close",
                pass ? Bg008LaneEvidence.VerdictPass : Bg008LaneEvidence.VerdictFail,
                $"bannerBalanced={bannerBalanced} expectedCash45={expectedCash45} draftSaved={draftSaved} " +
                $"closed={closed} cashMatched={cashMatched} stockMatched={stockMatched} label={balancedLabel}",
                "bg-008-balanced.png");
        });
    }

    // ------- L2: cash shortage -------

    [Fact]
    public async Task L2_CashShortageShowsAmountSignLabelAndRequiredNote()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await ReconciliationLaneSession.OpenAsync();
            await session.OpenSalesAsync();
            await session.SellCash45Async();
            await session.OpenReconciliationAsync();

            session.Reconciliation.ActualCashText = "35.00"; // 10.00 short

            bool bannerShort = session.Reconciliation.IsShort
                && session.Reconciliation.BannerText == "SHORT \u20B110.00";

            // Submit without the note: refused with copy, nothing moves.
            int movementsBefore = session.Store.Movements.Count;
            session.Reconciliation.SubmitCloseCommand.Execute(null);
            bool noteRequired = session.Reconciliation.HasErrorBanner
                && session.Reconciliation.ErrorBanner == ReconciliationViewModel.NoteRequiredShortCopy;
            bool noMovementOnReject = session.Store.Movements.Count == movementsBefore
                && session.Store.Movements.OfType<TripClose>().IsEmpty();

            session.ShotOfCurrent("bg-008-cash-short.png");
            Bg008LaneEvidence.SaveReviewPng("bg-008-cash-short.png", 1366, 768, session.Window);

            session.Reconciliation.CloseNotesText = "P10 fell behind the drawer";
            session.Reconciliation.SubmitCloseCommand.Execute(null);

            bool closed = session.Reconciliation.IsClosed
                && session.Store.State.Trip(session.Trip).Status == TripStatus.Closed;
            ReconciliationReport report = session.Report();
            bool shortageRecorded = report.Totals.CashVariance.Centavos == 1000
                && report.Totals.CashLabel == VarianceLabel.Shortage
                && report.Totals.CountedCash.Centavos == 3500
                && report.Totals.ExpectedCash.Centavos == 4500
                && session.Store.Context.Closes.Single().Notes == "P10 fell behind the drawer";
            bool labelOnScreen = session.Reconciliation.Report!.CashLabelText == "SHORTAGE"
                && session.Reconciliation.CloseReceipt!.VarianceLabelText == "SHORTAGE";

            bool pass = bannerShort && noteRequired && noMovementOnReject && closed && shortageRecorded && labelOnScreen;
            await Bg008LaneEvidence.WriteVerdictAsync(
                "bg-008-l2", "L2 cash shortage",
                pass ? Bg008LaneEvidence.VerdictPass : Bg008LaneEvidence.VerdictFail,
                $"bannerShort={bannerShort} noteRequired={noteRequired} noMovement={noMovementOnReject} " +
                $"closed={closed} shortage={shortageRecorded} label={labelOnScreen}",
                "bg-008-cash-short.png");
        });
    }

    // ------- L3: cash overage -------

    [Fact]
    public async Task L3_CashOverageShowsAmountSignLabelAndRequiredNote()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await ReconciliationLaneSession.OpenAsync();
            await session.OpenSalesAsync();
            await session.SellCash45Async();
            await session.OpenReconciliationAsync();

            session.Reconciliation.ActualCashText = "55.00"; // 10.00 over

            bool bannerOver = session.Reconciliation.IsOver
                && session.Reconciliation.BannerText == "OVER \u20B110.00";

            session.Reconciliation.SubmitCloseCommand.Execute(null);
            bool noteRequired = session.Reconciliation.HasErrorBanner
                && session.Reconciliation.ErrorBanner == ReconciliationViewModel.NoteRequiredOverCopy;

            session.ShotOfCurrent("bg-008-cash-over.png");
            Bg008LaneEvidence.SaveReviewPng("bg-008-cash-over.png", 1366, 768, session.Window);

            session.Reconciliation.CloseNotesText = "customer overpaid and left";
            session.Reconciliation.SubmitCloseCommand.Execute(null);

            bool closed = session.Reconciliation.IsClosed
                && session.Store.State.Trip(session.Trip).Status == TripStatus.Closed;
            ReconciliationReport report = session.Report();
            bool overageRecorded = report.Totals.CashVariance.Centavos == -1000
                && report.Totals.CashLabel == VarianceLabel.Overage
                && report.Totals.CountedCash.Centavos == 5500;
            bool labelOnScreen = session.Reconciliation.Report!.CashLabelText == "OVERAGE";

            bool pass = bannerOver && noteRequired && closed && overageRecorded && labelOnScreen;
            await Bg008LaneEvidence.WriteVerdictAsync(
                "bg-008-l3", "L3 cash overage",
                pass ? Bg008LaneEvidence.VerdictPass : Bg008LaneEvidence.VerdictFail,
                $"bannerOver={bannerOver} noteRequired={noteRequired} closed={closed} " +
                $"overage={overageRecorded} label={labelOnScreen}",
                "bg-008-cash-over.png");
        });
    }

    // ------- L4: stock variances -------

    [Fact]
    public async Task L4_StockVarianceLinesShowExpectedCountedQtyAndValue()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await ReconciliationLaneSession.OpenAsync();
            await session.OpenSalesAsync();
            await session.SellCash45Async(); // sardines expected 49 now
            await session.OpenReconciliationAsync();

            ReconciliationCountRow sardines = session.Reconciliation.CountRows
                .Single(r => r.Sku == SalesSeed.SardinesSku);
            ReconciliationCountRow kopi = session.Reconciliation.CountRows
                .Single(r => r.Sku == SalesSeed.KopiSku);
            sardines.CountedText = "47"; // 2 missing  -> SHORTAGE -2 x 45 = -90.00
            sardines.Notes = "two broken bottles";
            kopi.CountedText = "52";     // 2 extra    -> OVERAGE  +2 x 30 = +60.00

            bool rowSigned = sardines.VarianceLabel == "SHORTAGE"
                && sardines.QuantityVarianceText == "-2"
                && kopi.VarianceLabel == "OVERAGE"
                && kopi.QuantityVarianceText == "+2";

            session.Reconciliation.SaveDraftCommand.Execute(null);
            bool draftSaved = session.Reconciliation.DraftSaved;

            // The count grid sits below the fold at 768px — bring the variance
            // lines into the capture.
            session.ScrollReconTo(260);
            session.ShotOfCurrent("bg-008-stock-variance.png");
            Bg008LaneEvidence.SaveReviewPng("bg-008-stock-variance.png", 1366, 768, session.Window);

            ReconciliationReport report = session.Report();
            StockVarianceRow reportSardines = report.StockRows.Single(r => r.Sku == SalesSeed.SardinesSku);
            StockVarianceRow reportKopi = report.StockRows.Single(r => r.Sku == SalesSeed.KopiSku);
            bool everyLineComplete = report.StockRows.All(r =>
                r.CountedUnits is not null
                && r.QuantityVariance is not null
                && r.ValueVariance is not null);
            bool sardinesCorrect = reportSardines.ExpectedUnits == 49 && reportSardines.CountedUnits == 47
                && reportSardines.QuantityVariance == -2
                && reportSardines.ValueVariance!.Value.Centavos == -9000
                && reportSardines.Notes == "two broken bottles";
            bool kopiCorrect = reportKopi.ExpectedUnits == 50 && reportKopi.CountedUnits == 52
                && reportKopi.QuantityVariance == 2
                && reportKopi.ValueVariance!.Value.Centavos == 6000;
            // The SCREEN report shows every field per line (expected / counted /
            // qty variance / value / note).
            ReportStockRowView screenSardines = session.Reconciliation.Report!.StockRows
                .Single(r => r.Sku == SalesSeed.SardinesSku);
            ReportStockRowView screenKopi = session.Reconciliation.Report!.StockRows
                .Single(r => r.Sku == SalesSeed.KopiSku);
            bool screenComplete = screenSardines.ExpectedText == "49"
                && screenSardines.CountedText == "47"
                && screenSardines.QuantityVarianceText == "-2"
                && screenSardines.ValueVarianceText.Contains("90.00", StringComparison.Ordinal)
                && screenSardines.NotesText == "two broken bottles"
                && screenKopi.ExpectedText == "50"
                && screenKopi.CountedText == "52"
                && screenKopi.QuantityVarianceText == "+2"
                && screenKopi.ValueVarianceText.Contains("60.00", StringComparison.Ordinal);
            bool countsPersist = session.Store.Context.Counts.Count == 1
                && session.Store.Movements.OfType<CountObserved>().Count() == 2;

            bool pass = rowSigned && draftSaved && everyLineComplete && sardinesCorrect && kopiCorrect
                && screenComplete && countsPersist;
            await Bg008LaneEvidence.WriteVerdictAsync(
                "bg-008-l4", "L4 stock variances",
                pass ? Bg008LaneEvidence.VerdictPass : Bg008LaneEvidence.VerdictFail,
                $"rowSigned={rowSigned} draftSaved={draftSaved} everyLineComplete={everyLineComplete} " +
                $"sardines={sardinesCorrect} kopi={kopiCorrect} screenComplete={screenComplete} countsPersist={countsPersist}",
                "bg-008-stock-variance.png");
        });
    }

    // ------- L5: save + reopen draft (no lock, no final audit) -------

    [Fact]
    public async Task L5_SavedDraftLocksNothingAndSurvivesRevisit()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await ReconciliationLaneSession.OpenAsync();
            await session.OpenSalesAsync();
            await session.SellCash45Async();
            await session.OpenReconciliationAsync();

            ReconciliationCountRow sardines = session.Reconciliation.CountRows
                .Single(r => r.Sku == SalesSeed.SardinesSku);
            sardines.CountedText = "48"; // 1 missing (the sold unit is counted)
            sardines.Notes = "one more to verify";
            session.Reconciliation.SaveDraftCommand.Execute(null);

            bool draftSaved = session.Reconciliation.DraftSaved;
            bool noLock = session.Store.State.Trip(session.Trip).Status == TripStatus.Open
                && session.Store.Movements.OfType<TripClose>().IsEmpty()
                && session.Store.Movements.OfType<TripReopen>().IsEmpty()
                && session.Store.Audits.Where(a =>
                    a.Category == TripAudit.TripCloseCategory
                    || a.Category == TripAudit.TripReopenCategory).IsEmpty();
            bool countFact = session.Store.Context.Counts.Count == 1
                && session.Store.Audits.Any(a => a.Category == "COUNT");

            // Reopen the screen: the draft is rehydrated from the ledger.
            session.Navigation.Navigate(ScreenKeys.Sales);
            await session.OpenReconciliationAsync();
            ReconciliationCountRow rehydrated = session.Reconciliation.CountRows
                .Single(r => r.Sku == SalesSeed.SardinesSku);
            bool draftRehydrated = session.Reconciliation.DraftSaved
                && rehydrated.CountedText == "48"
                && rehydrated.Notes == "one more to verify";

            // The rehydrated count line sits below the fold at 768px.
            session.ScrollReconTo(260);
            session.ShotOfCurrent("bg-008-close-draft.png");
            Bg008LaneEvidence.SaveReviewPng("bg-008-close-draft.png", 1366, 768, session.Window);

            bool pass = draftSaved && noLock && countFact && draftRehydrated;
            await Bg008LaneEvidence.WriteVerdictAsync(
                "bg-008-l5", "L5 save + reopen draft",
                pass ? Bg008LaneEvidence.VerdictPass : Bg008LaneEvidence.VerdictFail,
                $"draftSaved={draftSaved} noLock={noLock} countFact={countFact} draftRehydrated={draftRehydrated}",
                "bg-008-close-draft.png");
        });
    }

    // ------- L6: submit + close (lock + close receipt persists) -------

    [Fact]
    public async Task L6_SubmitCloseLocksTripAndPersistsCloseReceipt()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await ReconciliationLaneSession.OpenAsync();
            await session.OpenSalesAsync();
            await session.SellCash45Async();
            await session.OpenReconciliationAsync();

            session.Reconciliation.ActualCashText = "45.00";
            session.Reconciliation.SubmitCloseCommand.Execute(null);

            bool locked = session.Reconciliation.IsClosed
                && session.Store.State.Trip(session.Trip).Status == TripStatus.Closed
                && session.Store.Movements.OfType<TripClose>().Any()
                && session.Store.Audits.Any(a => a.Category == TripAudit.TripCloseCategory)
                && !session.Reconciliation.CanSubmitClose;
            bool receiptVisible = session.Reconciliation.HasCloseReceipt
                && session.Reconciliation.CloseReceipt!.CountedCashText.Contains("45.00", StringComparison.Ordinal)
                && session.Reconciliation.CloseReceipt.VarianceLabelText == "BALANCED";
            bool gateShown = session.Reconciliation.CorrectionGateText
                == "This trip is closed - corrections need an authorized reopen.";

            // Sales lock: a NEW sale session is refused on the closed trip
            // (NO_OPEN_TRIP — one active trip per peddler, and it is closed).
            Result<ICheckoutSession> newSession = await session.Services
                .GetRequiredService<ICheckoutSessionFactory>()
                .CreateSessionAsync();
            bool salesLocked = newSession.IsFailure
                && newSession.Errors[0].Code == "NO_OPEN_TRIP";

            // Restart persistence: a fresh host over the same root still has
            // the close document, movement, and audit.
            bool persisted;
            string root = session.Root;
            ServiceProvider second = BentaGoHost.Build(
                development: true,
                dataRootOverride: root,
                extra: s =>
                {
                    ObservableObjectFactory.RegisterAppServices(s);
                    s.AddSingleton<Func<DateTimeOffset>>(() => ReconciliationLaneSession.Now);
                });
            using (second)
            {
                var store2 = second.GetRequiredService<SqliteLedgerStore>();
                store2.Bind();
                persisted = store2.Context.Closes.Count == 1
                    && store2.State.Trip(store2.Context.Trips.Single().Id).Status == TripStatus.Closed
                    && store2.Movements.OfType<TripClose>().Any()
                    && store2.Audits.Any(a => a.Category == TripAudit.TripCloseCategory);
            }

            await session.OpenReconciliationAsync();
            bool receiptAfterReturn = session.Reconciliation.HasCloseReceipt;

            // The close receipt sits below the fold at 768px — bring the
            // receipt + report region into the capture.
            session.ScrollReconToEnd();
            session.ShotOfCurrent("bg-008-close-lock.png");
            Bg008LaneEvidence.SaveReviewPng("bg-008-close-lock.png", 1366, 768, session.Window);

            bool pass = locked && receiptVisible && gateShown && salesLocked && persisted && receiptAfterReturn;
            await Bg008LaneEvidence.WriteVerdictAsync(
                "bg-008-l6", "L6 submit + close",
                pass ? Bg008LaneEvidence.VerdictPass : Bg008LaneEvidence.VerdictFail,
                $"locked={locked} receiptVisible={receiptVisible} gateShown={gateShown} " +
                $"salesLocked={salesLocked} persisted={persisted} receiptAfterReturn={receiptAfterReturn}",
                "bg-008-close-lock.png");
        });
    }

    // ------- L7: reopen with admin approval -------

    [Fact]
    public async Task L7_ReopenRecordsReasonApproverAndKeepsPriorClose()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await ReconciliationLaneSession.OpenAsync();
            await session.OpenSalesAsync();
            await session.SellCash45Async();
            await session.OpenReconciliationAsync();

            session.Reconciliation.ActualCashText = "45.00";
            session.Reconciliation.SubmitCloseCommand.Execute(null);
            Assert.True(session.Reconciliation.IsClosed, "close failed before the reopen");
            DailyClose priorClose = session.Store.Context.Closes.Single();

            // Gate: reason first, then the admin approval.
            session.Reconciliation.ReopenCommand.Execute(null);
            bool reasonGate = session.Reconciliation.ErrorBanner == ErrorReason;
            session.Reconciliation.ReopenReasonText = "a sale was missed";
            session.Reconciliation.ReopenCommand.Execute(null);
            bool approvalGate = session.Reconciliation.ErrorBanner == ErrorApprover
                && session.Reconciliation.NeedsApproverPicker;

            // The reopen panel sits below the fold at 768px.
            session.ScrollReconToEnd();
            session.ShotOfCurrent("bg-008-reopen.png");
            Bg008LaneEvidence.SaveReviewPng("bg-008-reopen.png", 1366, 768, session.Window);

            session.Reconciliation.SelectedApprover =
                session.Reconciliation.Approvers.Single(a => a.Id == session.Admin);
            session.Reconciliation.ReopenCommand.Execute(null);

            bool reopened = !session.Reconciliation.IsClosed
                && session.Store.State.Trip(session.Trip).Status == TripStatus.Open;
            AuditEvent reopen = session.Store.Audits.Single(a => a.Category == TripAudit.TripReopenCategory);
            bool reasonAndActor = reopen.Message.Contains("a sale was missed", StringComparison.Ordinal)
                && reopen.Message.Contains("(approved by theadmin)", StringComparison.Ordinal)
                && reopen.Reason == "a sale was missed (approved by theadmin)"
                && reopen.Actor == session.Seller
                && session.Store.Movements.OfType<TripReopen>().Any();
            bool priorCloseVisible = session.Store.Context.Closes.Single().Id == priorClose.Id
                && session.Reconciliation.HasCloseReceipt
                && session.Reconciliation.CloseReceipt!.CountedCashText.Contains("45.00", StringComparison.Ordinal);
            bool saleResumes = session.Reconciliation.CanSubmitClose;

            bool pass = reasonGate && approvalGate && reopened && reasonAndActor && priorCloseVisible && saleResumes;
            await Bg008LaneEvidence.WriteVerdictAsync(
                "bg-008-l7", "L7 reopen with admin approval",
                pass ? Bg008LaneEvidence.VerdictPass : Bg008LaneEvidence.VerdictFail,
                $"reasonGate={reasonGate} approvalGate={approvalGate} reopened={reopened} " +
                $"reasonAndActor={reasonAndActor} priorCloseVisible={priorCloseVisible} saleResumes={saleResumes}",
                "bg-008-reopen.png");
        });
    }

    // ====================================================================
    // Session
    // ====================================================================

    /// <summary>
    /// One lane session: an isolated migrated root seeded by
    /// <see cref="SalesSeed"/> plus the approver accounts, a visible real
    /// MainWindow, seller login, and the services a lane asserts against.
    /// Disposing closes the window and, for temp roots, deletes the data.
    /// </summary>
    private sealed class ReconciliationLaneSession : IDisposable
    {
        private readonly string? _rootToDelete;

        /// <summary>Fixed "now" of the setup day (matches the SalesSeed trip).</summary>
        internal static readonly DateTimeOffset Now = new(2026, 1, 15, 8, 30, 0, TimeSpan.Zero);

        public ReconciliationLaneSession(string root, string? rootToDelete, ServiceProvider services,
            MainWindow window, MainViewModel viewModel, ReconciliationLaneDialogService dialogs,
            INavigationService navigation, SqliteLedgerStore store, UserId seller,
            UserId supervisor, UserId admin, PeddlerId peddler)
        {
            Root = root;
            _rootToDelete = rootToDelete;
            Services = services;
            Window = window;
            ViewModel = viewModel;
            Dialogs = dialogs;
            Navigation = navigation;
            Store = store;
            Seller = seller;
            Supervisor = supervisor;
            Admin = admin;
            Peddler = peddler;
            Trip = store.Context.Trips.Single().Id;
        }

        public string Root { get; }
        public ServiceProvider Services { get; }
        public MainWindow Window { get; }
        public MainViewModel ViewModel { get; }
        public ReconciliationLaneDialogService Dialogs { get; }
        public INavigationService Navigation { get; }
        public SqliteLedgerStore Store { get; }

        public SalesViewModel Sales => (SalesViewModel)ViewModel.Current!;
        public ReconciliationViewModel Reconciliation => (ReconciliationViewModel)ViewModel.Current!;
        public TripId Trip { get; }
        public PeddlerId Peddler { get; }
        public UserId Seller { get; }
        public UserId Supervisor { get; }
        public UserId Admin { get; }

        public IActorContext Actors => Services.GetRequiredService<IActorContext>();

        public static async Task<ReconciliationLaneSession> OpenAsync(int width = 1366, int height = 768)
        {
            string temp = Path.Combine(Path.GetTempPath(), "bentago-008-r-" + Guid.NewGuid().ToString("N"));
            var services = BentaGoHost.Build(
                development: true,
                dataRootOverride: temp,
                extra: s =>
                {
                    ObservableObjectFactory.RegisterAppServices(s);
                    s.AddSingleton<Func<DateTimeOffset>>(() => Now);
                    s.AddSingleton<IDialogService, ReconciliationLaneDialogService>();
                });
            SalesSeed.Seed(services);

            var (seller, supervisor, admin) = await AddAccountsAsync(services);

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

            var dialogs = (ReconciliationLaneDialogService)services.GetRequiredService<IDialogService>();
            var nav = services.GetRequiredService<INavigationService>();
            var store = services.GetRequiredService<SqliteLedgerStore>();
            return new ReconciliationLaneSession(temp, temp, services, window, viewModel,
                dialogs, nav, store, seller, supervisor, admin, store.Context.Trips.Single().Peddler);
        }

        private static async Task<(UserId Seller, UserId Supervisor, UserId Admin)> AddAccountsAsync(
            ServiceProvider services)
        {
            var ids = new ScenarioIdSource("appshell-reconciliation-lanes-v1");
            var store = services.GetRequiredService<SqliteLedgerStore>();
            PeddlerId peddler = store.Context.Trips.Single().Peddler;

            IPasswordHasher hasher = services.GetRequiredService<IPasswordHasher>();
            IUserAccountStore accounts = services.GetRequiredService<IUserAccountStore>();
            UserId seller = services.GetRequiredService<IActorContext>().CurrentUserId!.Value;

            var supervisor = new UserId(ids.Next("supervisor"));
            var admin = new UserId(ids.Next("admin"));
            await accounts.AddAsync(new User
            {
                Id = supervisor,
                Username = "maysupervisor",
                PasswordHash = hasher.Hash("approver"),
                Role = Role.Supervisor,
                PeddlerLink = peddler,
                CreatedBy = seller,
                CreatedUtc = Now,
            });
            await accounts.AddAsync(new User
            {
                Id = admin,
                Username = "theadmin",
                PasswordHash = hasher.Hash("approver"),
                Role = Role.Admin,
                CreatedBy = seller,
                CreatedUtc = Now,
            });

            return (seller, supervisor, admin);
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
                    $"trips={Store.Context.Trips.Count}");
            }
        }

        public async Task OpenReconciliationAsync()
        {
            Navigation.Navigate(ScreenKeys.Reconciliation);
            try
            {
                await WaitUntilAsync(() => Reconciliation.HasTrip && Reconciliation.CountRows.Count > 0);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"key={Navigation.CurrentKey} hasTrip={Reconciliation.HasTrip} " +
                    $"fatal='{Reconciliation.FatalMessage}'");
            }
        }

        /// <summary>One exact 45.00 cash sale (1 x Sardines) through the REAL
        /// checkout path with the canned payment result.</summary>
        public async Task SellCash45Async()
        {
            TypeScan(SalesSeed.SardinesSku);
            await WaitUntilAsync(() => Sales.HasCartItems);

            Dialogs.CannedResult = new PaymentDialogResult(
                new[] { new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(45m), null) },
                Money.Zero);
            Sales.CheckoutCommand.Execute(null);
            try
            {
                await WaitUntilAsync(() => Sales.HasReceipt);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"canned checkout did not finish: dialogsOpen={Dialogs.IsOpen} " +
                    $"error='{Sales.ActionError}' state='{Sales.SessionStateMessage}' " +
                    $"cart={Sales.Cart.Count}");
            }
            finally
            {
                Dialogs.CannedResult = null;
            }
        }

        private void TypeScan(string text)
        {
            Sales.ScanBoxText = text;
            Sales.ScanCommand.Execute(null);
        }

        public ReconciliationReport Report()
        {
            Result<ReconciliationReport> report = Services
                .GetRequiredService<IReconciliationReportService>()
                .Build(Peddler, Trip);
            Assert.True(report.IsSuccess, report.Errors.FirstOrDefault()?.Code ?? "report failed");
            return report.Value;
        }

        public void ShotOfCurrent(string fileName)
        {
            // WPF composes asynchronously: without a layout + render pump the
            // capture races the first frame and lands as a blank window.
            PumpRender();
            Bg008LaneEvidence.SavePng(fileName, 1366, 768, Window);
        }

        public void PumpRender()
        {
            Window.UpdateLayout();
            System.Windows.Application.Current.Dispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { }));
        }

        /// <summary>Scrolls the reconciliation screen's own viewport (count
        /// grid, receipt, and reopen panel sit below the fold at 768px)
        /// before a capture. The pump must run BEFORE the search: the shell's
        /// DataTemplate inflates on the first forced layout pass, so a
        /// pre-pump visual walk finds an empty content presenter.</summary>
        public void ScrollReconTo(double offset)
        {
            PumpRender();
            ReconciliationView view = FindDescendant<ReconciliationView>(Window)
                ?? throw new TimeoutException("reconciliation view not found in the shell after render pump");
            ScrollViewer viewport = FindDescendant<ScrollViewer>(view)
                ?? throw new TimeoutException("reconciliation scroll viewport not found after render pump");
            viewport.ScrollToVerticalOffset(offset);
            PumpRender();
        }

        /// <summary>Scrolls the reconciliation viewport to its bottom (the
        /// close receipt / reopen panel / report region).</summary>
        public void ScrollReconToEnd()
        {
            PumpRender();
            ReconciliationView view = FindDescendant<ReconciliationView>(Window)
                ?? throw new TimeoutException("reconciliation view not found in the shell after render pump");
            ScrollViewer viewport = FindDescendant<ScrollViewer>(view)
                ?? throw new TimeoutException("reconciliation scroll viewport not found after render pump");
            viewport.ScrollToEnd();
            PumpRender();
        }

        private static T? FindDescendant<T>(DependencyObject root)
            where T : DependencyObject
        {
            if (root is T match)
            {
                return match;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T found)
                {
                    return found;
                }

                T? deeper = FindDescendant<T>(child);
                if (deeper is not null)
                {
                    return deeper;
                }
            }

            return null;
        }

        public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15_000)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
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

/// <summary>
/// Lane dialog service: with <see cref="CannedResult"/> set and type-matched,
/// ShowAsync returns it immediately (the cash-sale setup); anything else
/// falls through to the real <see cref="DialogService"/>.
/// </summary>
internal sealed class ReconciliationLaneDialogService : IDialogService
{
    private readonly DialogService _real;

    public ReconciliationLaneDialogService(IFocusService focus) => _real = new DialogService(focus);

    public object? CannedResult { get; set; }

    public bool IsOpen => _real.IsOpen;

    public Task<TResult?> ShowAsync<TResult>(DialogViewModelBase<TResult> viewModel, string? focusAfterClose = null)
        where TResult : class
    {
        if (CannedResult is { } canned && canned is TResult result)
        {
            return Task.FromResult<TResult?>(result);
        }

        return _real.ShowAsync(viewModel, focusAfterClose);
    }
}

internal static class LaneEnumerableExtensions
{
    public static bool IsEmpty<T>(this IEnumerable<T> source) => !source.Any();
}
