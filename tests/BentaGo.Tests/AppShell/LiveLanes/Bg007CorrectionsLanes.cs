using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BentaGo.App;
using BentaGo.App.Dialogs;
using BentaGo.App.Dialogs.Corrections;
using BentaGo.App.Dialogs.Payment;
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
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Tests.AppShell;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell.LiveLanes;

/// <summary>
/// BG-007 live lanes L6-L10 (docs/05 "Verify, live"): the in-process STA
/// driver opens the REAL corrections panel through the F8 path
/// (<see cref="SalesViewModel.OpenCorrectionsCommand"/>) and proves the 007b
/// rules against the working ledger — the peddler approval gate, supervisor
/// self-approval, the partial return to peddler stock, the damage/loss
/// adjustment kind, and the TRIP_CLOSED_CORRECTION gate. Each lane seeds the
/// same SalesSeed root and drives the checkout through the canned payment
/// result, then asserts the store movements/audits. Env-gated
/// (BENTAGO_LIVE_LANES=1) so the mandated --filter AppShell run stays
/// headless.
/// </summary>
[Collection("AppShellSta")]
public sealed class Bg007CorrectionsLanes
{
    private const string ErrorApprover = "A void needs an approver. Choose a supervisor or admin above.";
    private const string ErrorClosedTrip = "This trip is closed - corrections need an authorized reopen.";

    private readonly AppShellStaFixture _sta;

    public Bg007CorrectionsLanes(AppShellStaFixture sta) => _sta = sta;

    private static bool LiveLanesEnabled => Environment.GetEnvironmentVariable("BENTAGO_LIVE_LANES") == "1";

    // ------- L6: peddler cannot void without an approver -------

    [Fact]
    public async Task L6_PeddlerVoidNeedsApproval_NoMovementUntilApprover()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await CorrectionsLaneSession.OpenAsync();
            await session.OpenSalesAsync();

            await DoCashSaleAsync(session);
            SaleId saleId = session.QuerySales().Value.Single().Id;

            bool errorShown = false;
            bool noMovementOnReject = false;
            bool voidApproved = false;
            bool approverAudited = false;
            bool stockRestored = false;
            bool cashRestored = false;

            DriveCorrections(session, corrections =>
            {
                corrections.SelectMode(CorrectionsDialogViewModel.ModeVoid);
                corrections.SelectedSale = corrections.Sales.Single(s => s.Id == saleId);
                corrections.Reason = "wrong item scanned";

                int movementsBefore = session.Store.Movements.Count;
                long auditsBefore = session.Store.Audits.Count;
                corrections.SubmitCommand.Execute(null);

                errorShown = corrections.HasErrorBanner
                    && corrections.ErrorBanner == ErrorApprover;
                noMovementOnReject = session.Store.Movements.Count == movementsBefore
                    && session.Store.Audits.Count == auditsBefore
                    && session.Store.Context.Voids.Count == 0;
                session.ShotOfDialog("bg-007-void-approval.png");

                corrections.SelectedApprover =
                    corrections.Approvers.Single(a => a.Id == session.Supervisor);
                corrections.SubmitCommand.Execute(null);
                session.CorrectionsOpDone = true;
            });

            await session.WaitCorrectionsClosedAsync();

            var @void = session.Store.Context.Voids.SingleOrDefault();
            voidApproved = @void is not null && @void.OriginalSaleId == saleId;
            if (@void is not null)
            {
                AuditEvent audit = session.Store.Audits.Single(a => a.Category == "VOID");
                approverAudited = audit.Message.Contains("approved by", StringComparison.Ordinal)
                    && audit.Message.Contains(session.Supervisor.Value.ToString(), StringComparison.Ordinal);
            }

            stockRestored = session.Store.State.StockOnHand(session.Product, session.Route).Units == 50;
            cashRestored = session.Store.State.FundsOnHand(Fund.PhysicalCash).Centavos == 0;

            bool pass = errorShown && noMovementOnReject && voidApproved && approverAudited
                && stockRestored && cashRestored;
            await Bg007LaneEvidence.WriteVerdictAsync(
                "bg-007-l6", "L6 peddler void needs approval",
                pass ? Bg007LaneEvidence.VerdictPass : Bg007LaneEvidence.VerdictFail,
                $"errorShown={errorShown} noMovement={noMovementOnReject} voided={voidApproved} " +
                $"approverAudited={approverAudited} stock={stockRestored} cash={cashRestored}",
                "bg-007-void-approval.png");
        });
    }

    // ------- L7: supervisor self-approves -------

    [Fact]
    public async Task L7_SupervisorSelfApprovesAndAuditsOwnId()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await CorrectionsLaneSession.OpenAsync();
            await session.OpenSalesAsync();

            await DoCashSaleAsync(session);
            SaleId saleId = session.QuerySales().Value.Single().Id;

            // The correction panel resolves its actor from IActorContext: sign
            // the supervisor in, who is peddler-linked to this trip.
            session.Actors.Set(session.Supervisor);

            bool selfApproval = false;
            bool voidApproved = false;
            bool auditOwnId = false;
            bool cashRestored = false;

            DriveCorrections(session, corrections =>
            {
                corrections.SelectMode(CorrectionsDialogViewModel.ModeVoid);
                corrections.SelectedSale = corrections.Sales.Single(s => s.Id == saleId);
                corrections.Reason = "customer cancelled";
                selfApproval = corrections.IsSelfApproval && !corrections.NeedsApproverPicker;
                session.ShotOfDialog("bg-007-supervisor-void.png");
                corrections.SubmitCommand.Execute(null);
                session.CorrectionsOpDone = true;
            });

            await session.WaitCorrectionsClosedAsync();

            var @void = session.Store.Context.Voids.SingleOrDefault();
            voidApproved = @void is not null && @void.OriginalSaleId == saleId
                && @void.By == session.Supervisor && @void.ApprovedBy == session.Supervisor;
            if (@void is not null)
            {
                AuditEvent audit = session.Store.Audits.Single(a => a.Category == "VOID");
                auditOwnId = audit.Message.Contains("approved by", StringComparison.Ordinal)
                    && audit.Message.Contains(session.Supervisor.Value.ToString(), StringComparison.Ordinal);
            }

            cashRestored = session.Store.State.FundsOnHand(Fund.PhysicalCash).Centavos == 0;

            bool pass = selfApproval && voidApproved && auditOwnId && cashRestored;
            await Bg007LaneEvidence.WriteVerdictAsync(
                "bg-007-l7", "L7 supervisor self-approve",
                pass ? Bg007LaneEvidence.VerdictPass : Bg007LaneEvidence.VerdictFail,
                $"selfApproval={selfApproval} voided={voidApproved} auditOwnId={auditOwnId} cash={cashRestored}",
                "bg-007-supervisor-void.png");
        });
    }

    // ------- L8: return one to peddler stock -------

    [Fact]
    public async Task L8_ReturnOneToPeddlerStockRestoresOnHand()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await CorrectionsLaneSession.OpenAsync();
            await session.OpenSalesAsync();

            await DoCashSaleAsync(session);
            SaleId saleId = session.QuerySales().Value.Single().Id;
            int afterSale = session.Store.State.StockOnHand(session.Product, session.Route).Units;

            bool returnAtRoute = false;
            bool onHand50 = false;
            bool fullyReturned = false;

            DriveCorrections(session, corrections =>
            {
                corrections.SelectMode(CorrectionsDialogViewModel.ModeReturn);
                corrections.SelectedSale = corrections.Sales.Single(s => s.Id == saleId);
                ReturnLineOption line = corrections.SaleLines.Single(l => l.Sku == SalesSeed.SardinesSku);
                corrections.SelectedLine = line;
                corrections.QuantityText = "1";
                corrections.SelectedDestination =
                    corrections.Destinations.Single(d => d.Location is null);
                corrections.Reason = "changed mind";
                session.ShotOfDialog("bg-007-return-stock.png");
                corrections.SubmitCommand.Execute(null);
                session.CorrectionsOpDone = true;
            });

            await session.WaitCorrectionsClosedAsync();

            ReturnToStock? move = session.Store.Movements.OfType<ReturnToStock>().SingleOrDefault();
            returnAtRoute = move is not null
                && move.LocationId == session.Route
                && move.Quantity.Units == 1;
            onHand50 = session.Store.State.StockOnHand(session.Product, session.Route).Units == 50;
            fullyReturned = session.Store.State.Sale(saleId).Status == SaleStatus.FullyReturned;

            bool pass = returnAtRoute && onHand50 && fullyReturned;
            await Bg007LaneEvidence.WriteVerdictAsync(
                "bg-007-l8", "L8 return to peddler stock",
                pass ? Bg007LaneEvidence.VerdictPass : Bg007LaneEvidence.VerdictFail,
                $"afterSale={afterSale} returnAtRoute={returnAtRoute} onHand50={onHand50} fullyReturned={fullyReturned}",
                "bg-007-return-stock.png");
        });
    }

    // ------- L9: damage/loss adjustment -------

    [Fact]
    public async Task L9_DamageAdjustmentDecrementsOnHand()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await CorrectionsLaneSession.OpenAsync();
            await session.OpenSalesAsync();

            await DoCashSaleAsync(session);

            bool damageKind = false;
            bool onHand48 = false;
            bool auditAdjustment = false;

            DriveCorrections(session, corrections =>
            {
                corrections.SelectMode(CorrectionsDialogViewModel.ModeDamageLoss);
                corrections.SelectedProduct =
                    corrections.Products.Single(p => p.Product == session.Product);
                corrections.QuantityText = "1";
                corrections.Reason = "crushed can";
                corrections.SelectedApprover =
                    corrections.Approvers.Single(a => a.Id == session.Supervisor);
                session.ShotOfDialog("bg-007-damage.png");
                corrections.SubmitCommand.Execute(null);
                session.CorrectionsOpDone = true;
            });

            await session.WaitCorrectionsClosedAsync();

            Damage? damage = session.Store.Movements.OfType<Damage>().SingleOrDefault();
            damageKind = damage is not null
                && damage.LocationId == session.Route
                && damage.Quantity.Units == 1
                && damage.Reason == "crushed can";
            onHand48 = session.Store.State.StockOnHand(session.Product, session.Route).Units == 48;
            AuditEvent? adjustment = session.Store.Audits
                .Where(a => a.Category == "ADJUSTMENT")
                .FirstOrDefault();
            auditAdjustment = adjustment is not null
                && adjustment.Message.Contains("approved by", StringComparison.Ordinal);

            bool pass = damageKind && onHand48 && auditAdjustment;
            await Bg007LaneEvidence.WriteVerdictAsync(
                "bg-007-l9", "L9 damage adjustment",
                pass ? Bg007LaneEvidence.VerdictPass : Bg007LaneEvidence.VerdictFail,
                $"damageKind={damageKind} onHand48={onHand48} auditAdjustment={auditAdjustment}",
                "bg-007-damage.png");
        });
    }

    // ------- L10: closed-trip correction gate -------

    [Fact]
    public async Task L10_ClosedTripRejectsVoidWithNoMovement()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await CorrectionsLaneSession.OpenAsync();
            await session.OpenSalesAsync();

            await DoCashSaleAsync(session);
            SaleId saleId = session.QuerySales().Value.Single().Id;

            // Close the trip with the counted cash equal to its own receipts.
            session.Actors.Set(session.Supervisor);
            Money expectedCash = session.Store.State.TripCash(session.Trip, Fund.PhysicalCash);
            Result<CloseOutcome> closed = session.Services.GetRequiredService<ICloseService>().Close(
                new CloseCommand(
                    new CloseId(Guid.NewGuid()), session.Trip, expectedCash, null,
                    session.Supervisor, CorrectionsLaneSession.Now));
            Assert.True(closed.IsSuccess, closed.Errors.FirstOrDefault()?.Code ?? "close failed");
            Assert.Equal(TripStatus.Closed, session.Store.State.Trip(session.Trip).Status);

            int movementsBefore = session.Store.Movements.Count;
            long auditsBefore = session.Store.Audits.Count;

            bool gateShown = false;
            bool noMovement = false;

            DriveCorrections(session, corrections =>
            {
                corrections.SelectMode(CorrectionsDialogViewModel.ModeVoid);
                corrections.SelectedSale = corrections.Sales.Single(s => s.Id == saleId);
                corrections.Reason = "late correction";
                corrections.SubmitCommand.Execute(null);
                gateShown = corrections.HasErrorBanner
                    && corrections.ErrorBanner == ErrorClosedTrip;
                session.ShotOfDialog("bg-007-closed-trip.png");
                noMovement = session.Store.Movements.Count == movementsBefore
                    && session.Store.Audits.Count == auditsBefore
                    && session.Store.Context.Voids.Count == 0;
                corrections.CancelCommand.Execute(null);
                session.CorrectionsOpDone = true;
            });

            await session.WaitCorrectionsClosedAsync();

            bool pass = gateShown && noMovement
                && session.Store.State.Trip(session.Trip).Status == TripStatus.Closed;
            await Bg007LaneEvidence.WriteVerdictAsync(
                "bg-007-l10", "L10 closed-trip correction gate",
                pass ? Bg007LaneEvidence.VerdictPass : Bg007LaneEvidence.VerdictFail,
                $"gateShown={gateShown} noMovement={noMovement} " +
                $"tripClosed={session.Store.State.Trip(session.Trip).Status == TripStatus.Closed}",
                "bg-007-closed-trip.png");
        });
    }

    // ------- helpers -------

    private static void DriveCorrections(
        CorrectionsLaneSession session, Action<CorrectionsDialogViewModel> op)
    {
        session.Dialogs.NextCorrectionOp = op;
        session.Sales.OpenCorrectionsCommand.Execute(null);
    }

    private static async Task DoCashSaleAsync(CorrectionsLaneSession session)
    {
        TypeScan(session, SalesSeed.SardinesSku);
        await WaitUntilAsync(() => session.Sales.HasCartItems);

        session.Dialogs.CannedResult = new PaymentDialogResult(
            new[] { new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(45m), null) },
            Money.Zero);
        session.Sales.CheckoutCommand.Execute(null);
        try
        {
            await WaitUntilAsync(() => session.Sales.HasReceipt);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"canned checkout did not finish: dialogsOpen={session.Dialogs.IsOpen} " +
                $"error='{session.Sales.ActionError}' state='{session.Sales.SessionStateMessage}' " +
                $"cart={session.Sales.Cart.Count}");
        }
        finally
        {
            session.Dialogs.CannedResult = null;
        }
    }

    private static void TypeScan(CorrectionsLaneSession session, string text)
    {
        session.Sales.ScanBoxText = text;
        session.Sales.ScanCommand.Execute(null);
    }

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

    // ====================================================================
    // Session
    // ====================================================================

    /// <summary>
    /// One lane session: an isolated migrated root seeded by
    /// <see cref="SalesSeed"/> plus the approver accounts, a visible real
    /// MainWindow, seller login, and the services a lane asserts against.
    /// Disposing closes the window and, for temp roots, deletes the data.
    /// </summary>
    private sealed class CorrectionsLaneSession : IDisposable
    {
        private readonly string? _rootToDelete;

        /// <summary>Fixed "now" of the setup day (matches the SalesSeed trip).</summary>
        internal static readonly DateTimeOffset Now = new(2026, 1, 15, 8, 30, 0, TimeSpan.Zero);

        public CorrectionsLaneSession(string root, string? rootToDelete, ServiceProvider services,
            MainWindow window, MainViewModel viewModel, CorrectionLaneDialogService dialogs,
            INavigationService navigation, SqliteLedgerStore store, UserId supervisor,
            UserId admin, ProductId product, LocationId route, LocationId storeLoc, PeddlerId peddler)
        {
            Root = root;
            _rootToDelete = rootToDelete;
            Services = services;
            Window = window;
            ViewModel = viewModel;
            Dialogs = dialogs;
            Navigation = navigation;
            Store = store;
            Supervisor = supervisor;
            Admin = admin;
            Product = product;
            Route = route;
            StoreLoc = storeLoc;
            Peddler = peddler;
            Trip = store.Context.Trips.Single().Id;
        }

        public string Root { get; }
        public ServiceProvider Services { get; }
        public MainWindow Window { get; }
        public MainViewModel ViewModel { get; }
        public CorrectionLaneDialogService Dialogs { get; }
        public INavigationService Navigation { get; }
        public SqliteLedgerStore Store { get; }

        public SalesViewModel Sales => (SalesViewModel)ViewModel.Current!;
        public TripId Trip { get; }
        public PeddlerId Peddler { get; }
        public UserId Supervisor { get; }
        public UserId Admin { get; }
        public ProductId Product { get; }
        public LocationId Route { get; }
        public LocationId StoreLoc { get; }

        /// <summary>Set by the corrections op the instant it finishes driving
        /// the panel; combined with !Dialogs.IsOpen in the close wait.</summary>
        public bool CorrectionsOpDone { get; set; }

        public IActorContext Actors => Services.GetRequiredService<IActorContext>();

        public static async Task<CorrectionsLaneSession> OpenAsync(int width = 1366, int height = 768, string? root = null, bool seed = true)
        {
            string? deleteRoot = null;
            string temp = root ?? Path.Combine(Path.GetTempPath(), "bentago-007-c-" + Guid.NewGuid().ToString("N"));
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
                    s.AddSingleton<Func<DateTimeOffset>>(() => Now);
                    s.AddSingleton<IDialogService, CorrectionLaneDialogService>();
                });
            if (seed)
            {
                SalesSeed.Seed(services);
            }

            var (supervisor, admin) = await AddApproversAsync(services);

            var store = services.GetRequiredService<SqliteLedgerStore>();
            Trip trip = store.Context.Trips.Single();
            var productId = (await services.GetRequiredService<IProductStore>()
                .FindBySkuAsync(SalesSeed.SardinesSku))!.Id;
            LocationId storeLoc = store.Movements.OfType<TransferOut>().First().LocationId;

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

            var dialogs = (CorrectionLaneDialogService)services.GetRequiredService<IDialogService>();
            var nav = services.GetRequiredService<INavigationService>();
            return new CorrectionsLaneSession(temp, deleteRoot, services, window, viewModel,
                dialogs, nav, store, supervisor, admin, productId,
                trip.Location, storeLoc, trip.Peddler);
        }

        private static async Task<(UserId Supervisor, UserId Admin)> AddApproversAsync(ServiceProvider services)
        {
            var ids = new ScenarioIdSource("appshell-corrections-lanes-v1");
            var store = services.GetRequiredService<SqliteLedgerStore>();
            PeddlerId peddler = store.Context.Trips.Single().Peddler;

            IPasswordHasher hasher = services.GetRequiredService<IPasswordHasher>();
            IUserAccountStore accounts = services.GetRequiredService<IUserAccountStore>();
            User seller = (await accounts.FindByUsernameAsync(SalesSeed.SellerUsername))!;

            var supervisor = new UserId(ids.Next("supervisor"));
            var admin = new UserId(ids.Next("admin"));
            await accounts.AddAsync(new User
            {
                Id = supervisor,
                Username = "maysupervisor",
                PasswordHash = hasher.Hash("approver"),
                Role = Role.Supervisor,
                PeddlerLink = peddler,
                CreatedBy = seller.Id,
                CreatedUtc = Now,
            });
            await accounts.AddAsync(new User
            {
                Id = admin,
                Username = "theadmin",
                PasswordHash = hasher.Hash("approver"),
                Role = Role.Admin,
                CreatedBy = seller.Id,
                CreatedUtc = Now,
            });

            return (supervisor, admin);
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

        public async Task WaitCorrectionsClosedAsync()
        {
            await WaitUntilAsync(() => CorrectionsOpDone && !Dialogs.IsOpen);
        }

        public Result<IReadOnlyList<SaleHistoryRow>> QuerySales() =>
            Services.GetRequiredService<ISaleQueryService>().Query(Peddler);

        public void ShotOfCurrent(string fileName) =>
            Bg007LaneEvidence.SaveReviewPng(fileName, 1366, 768, Window);

        /// <summary>Best-effort capture of the open corrections modal.</summary>
        public void ShotOfDialog(string fileName)
        {
            Window? dialog = System.Windows.Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(w => w is DialogHost && w.IsVisible);
            if (dialog is null)
            {
                return;
            }

            dialog.UpdateLayout();
            Bg007LaneEvidence.SaveReviewPng(fileName, 1366, 768, dialog);
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
/// Lane dialog service for the corrections lanes: with <see cref="CannedResult"/>
/// set and type-matched, ShowAsync returns it immediately (the cash-sale
/// persistence lane); otherwise a queued <see cref="NextCorrectionOp"/> drives
/// the real corrections modal from inside its modal loop, and anything else
/// falls through to the real <see cref="DialogService"/>.
/// </summary>
internal sealed class CorrectionLaneDialogService : IDialogService
{
    private readonly DialogService _real;

    public CorrectionLaneDialogService(IFocusService focus) => _real = new DialogService(focus);

    public object? CannedResult { get; set; }

    public Action<CorrectionsDialogViewModel>? NextCorrectionOp { get; set; }

    public bool IsOpen => _real.IsOpen;

    public Task<TResult?> ShowAsync<TResult>(DialogViewModelBase<TResult> viewModel, string? focusAfterClose = null)
        where TResult : class
    {
        if (CannedResult is { } canned && canned is TResult result)
        {
            return Task.FromResult<TResult?>(result);
        }

        if (viewModel is CorrectionsDialogViewModel corrections && NextCorrectionOp is { } next)
        {
            Action<CorrectionsDialogViewModel> op = next;
            NextCorrectionOp = null;
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle, new Action(() => op(corrections)));
        }

        return _real.ShowAsync(viewModel, focusAfterClose);
    }
}
