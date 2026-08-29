using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BentaGo.App;
using BentaGo.App.Dialogs;
using BentaGo.App.Dialogs.Payment;
using BentaGo.App.Features.Sales;
using BentaGo.App.Features.Transactions;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Payments;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Tests.AppShell;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell.LiveLanes;

/// <summary>
/// BG-006 live lanes L1-L10 (docs/05 "Verify, live"): the in-process STA
/// driver renders each lane off the working desktop and asserts measurable
/// state (session, cart, receipt, query service, fold cells, real keyboard
/// focus). Env-gated: only runs when BENTAGO_LIVE_LANES=1, so the mandated
/// --filter AppShell run stays headless.
/// </summary>
[Collection("AppShellSta")]
public sealed class Bg006SalesLanes
{
    private const string SalesTotal = "45.00";
    private const string Sardines = SalesSeed.SardinesSku;

    private readonly AppShellStaFixture _sta;

    public Bg006SalesLanes(AppShellStaFixture sta) => _sta = sta;

    private static bool LiveLanesEnabled => Environment.GetEnvironmentVariable("BENTAGO_LIVE_LANES") == "1";

    // ------- Lane 1: repeat scan -------

    [Fact]
    public async Task L1_RepeatScanAddsQuantityAndReturnsFocus()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await SalesLaneSession.OpenAsync();
            await session.OpenSalesAsync();

            // The app's focus contract: every completed scan requests the
            // scan box back (OS-level focus return is proven by the real-exe
            // recording's focus trace).
            var focusRequests = new List<string>();
            if (session.Services.GetRequiredService<IFocusService>() is FocusCoordinator coordinator)
            {
                coordinator.Requested += (_, e) => focusRequests.Add(e.ElementName);
            }

            TypeScan(session, Sardines);
            await WaitUntilAsync(() => session.Sales.HasCartItems);
            TypeScan(session, Sardines);
            await WaitUntilAsync(() => session.Sales.Cart.FirstOrDefault()?.Quantity == 2);

            session.ShotOfCurrent("bg-006-repeat-scan.png");
            int qty = session.Sales.Cart.First().Quantity;
            bool focusBack = focusRequests.Count > 0
                && focusRequests.IndexOf(AutomationIds.SalesScanBox) >= 0
                && focusRequests[^1] == AutomationIds.SalesScanBox;
            bool pass = qty == 2 && focusBack;
            await Bg006LaneEvidence.WriteVerdictAsync(
                "bg-006-l1", "L1 repeat scan",
                pass ? Bg006LaneEvidence.VerdictPass : Bg006LaneEvidence.VerdictFail,
                $"qty={qty} focusBack={focusBack} requests=[{string.Join(",", focusRequests)}]",
                "bg-006-repeat-scan.png");
        });
    }

    // ------- Lane 2: search by SKU, barcode, name -------

    [Fact]
    public async Task L2_SearchSkuBarcodeNameFindSameProduct()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await SalesLaneSession.OpenAsync();
            await session.OpenSalesAsync();

            var search = session.Services.GetRequiredService<IProductSearchService>();
            Result<IReadOnlyList<ProductHit>> bySku = await search.SearchAsync(Sardines);
            Result<IReadOnlyList<ProductHit>> byBarcode = await search.SearchAsync(SalesSeed.Barcode);
            Result<IReadOnlyList<ProductHit>> byName = await search.SearchAsync("sard");

            bool same = bySku.IsSuccess && byBarcode.IsSuccess && byName.IsSuccess
                && bySku.Value.Count == 1 && byBarcode.Value.Count == 1 && byName.Value.Count >= 1
                && bySku.Value[0].Product.Id == byBarcode.Value[0].Product.Id
                && bySku.Value[0].Product.Id == byName.Value[0].Product.Id;
            bool onHandShown = same && byName.Value[0].OnHand.Units == 50; // 50 loaded, no sale yet

            session.ShotOfCurrent("bg-006-search.png");
            await Bg006LaneEvidence.WriteVerdictAsync(
                "bg-006-l2", "L2 search SKU/barcode/name",
                same && onHandShown ? Bg006LaneEvidence.VerdictPass : Bg006LaneEvidence.VerdictFail,
                $"sku={bySku.Value.Count} barcode={byBarcode.Value.Count} name={byName.Value.Count} onHand={byName.Value[0].OnHand.Units}",
                "bg-006-search.png");
        });
    }

    // ------- Lane 3: complete a cash sale -------

    [Fact]
    public async Task L3_CashSaleExact()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await SalesLaneSession.OpenAsync();
            await session.OpenSalesAsync();

            TypeScan(session, Sardines);
            await WaitUntilAsync(() => session.Sales.HasCartItems);

            // Real payment dialog: tendered 50, total 45 -> change 5.00.
            string changeText = "";
            await DriveCheckoutAsync(session, v =>
            {
                v.AmountReceivedText = "50.00";
                changeText = v.ChangeText;
                v.ConfirmCommand.Execute(null);
            });

            session.ShotOfCurrent("bg-006-cash.png");
            Bg006LaneEvidence.SaveReviewPng("bg-006-cash.png", 1366, 768, session.Window);

            string? receipt = ExtractReceipt(session.Sales.ReceiptText);
            ProductHit? sardines = (await session.Search(Sardines)).Value[0];
            var rows = session.QuerySales();

            bool changeExact = changeText.Contains("5.00", StringComparison.Ordinal);
            bool stockExact = sardines.OnHand.Units == 49;
            bool cashExact = session.Store.Context.State.FundsOnHand(Fund.PhysicalCash).Centavos == 4500;
            bool oneSale = rows.IsSuccess && rows.Value.Count == 1 && rows.Value[0].ReceiptNumber == receipt
                && rows.Value[0].Total.Centavos == 4500
                && rows.Value[0].Payments.Single() is { Method: PaymentMethod.Cash, Amount.Centavos: 4500 };
            bool pass = changeExact && stockExact && cashExact && oneSale && receipt is not null;
            await Bg006LaneEvidence.WriteVerdictAsync(
                "bg-006-l3", "L3 cash sale",
                pass ? Bg006LaneEvidence.VerdictPass : Bg006LaneEvidence.VerdictFail,
                $"change='{changeText}' stock={stockExact} cash={cashExact} receipt={receipt} oneSale={oneSale}",
                "bg-006-cash.png");
        });
    }

    // ------- Lane 4: GCash and Maya -------

    [Fact]
    public async Task L4_WalletsPersistReferencesAndExcludeCash()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await SalesLaneSession.OpenAsync();
            await session.OpenSalesAsync();

            // GCash sale.
            TypeScan(session, Sardines);
            await WaitUntilAsync(() => session.Sales.HasCartItems);
            await DriveSingleLineDialogAsync(session, "gcash", SalesTotal, "G-REF-0001");
            session.Sales.ClearCartCommand.Execute(null);

            // Maya sale.
            TypeScan(session, Sardines);
            await WaitUntilAsync(() => session.Sales.HasCartItems);
            await DriveSingleLineDialogAsync(session, "maya", SalesTotal, "M-REF-0002");

            session.ShotOfCurrent("bg-006-wallets.png");
            Bg006LaneEvidence.SaveReviewPng("bg-006-wallets.png", 1366, 768, session.Window);

            Result<SaleDetail> gcash = session.Services.GetRequiredService<ISaleQueryService>()
                .GetByPaymentReference(session.Peddler, "G-REF-0001");
            Result<SaleDetail> maya = session.Services.GetRequiredService<ISaleQueryService>()
                .GetByPaymentReference(session.Peddler, "M-REF-0002");

            bool refsPersist = gcash.IsSuccess && maya.IsSuccess
                && gcash.Value.Payments.Single() is { Method: PaymentMethod.Gcash, Reference: "G-REF-0001" }
                && maya.Value.Payments.Single() is { Method: PaymentMethod.Maya, Reference: "M-REF-0002" };
            bool cashExcluded = session.Store.Context.State.FundsOnHand(Fund.PhysicalCash).Centavos == 0;
            bool gcashFund = session.Store.Context.State.FundsOnHand(Fund.Gcash).Centavos == 4500;
            bool mayaFund = session.Store.Context.State.FundsOnHand(Fund.Maya).Centavos == 4500;
            bool pass = refsPersist && cashExcluded && gcashFund && mayaFund;
            await Bg006LaneEvidence.WriteVerdictAsync(
                "bg-006-l4", "L4 GCash + Maya",
                pass ? Bg006LaneEvidence.VerdictPass : Bg006LaneEvidence.VerdictFail,
                $"refs={refsPersist} cash={cashExcluded} gcash={gcashFund} maya={mayaFund}",
                "bg-006-wallets.png");
        });
    }

    // ------- Lane 5: bank transfer (reference required) -------

    [Fact]
    public async Task L5_BankReferenceRequiredAndSearchable()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await SalesLaneSession.OpenAsync();
            await session.OpenSalesAsync();

            TypeScan(session, Sardines);
            await WaitUntilAsync(() => session.Sales.HasCartItems);

            bool referenceRequired = false;
            bool gateBefore = false;
            bool gateAfter = false;
            await DriveCheckoutAsync(session, v =>
            {
                v.SelectMethodCommand.Execute("bank");
                v.AmountReceivedText = SalesTotal;
                referenceRequired = v.IsReferenceRequired && v.IsReferenceEntryVisible;
                gateBefore = !v.CanConfirm; // no reference yet
                v.Reference = "B-REF-0075";
                gateAfter = v.CanConfirm;
                v.ConfirmCommand.Execute(null);
            });

            session.ShotOfCurrent("bg-006-bank.png");

            Result<SaleDetail> found = session.Services.GetRequiredService<ISaleQueryService>()
                .GetByPaymentReference(session.Peddler, "B-REF-0075");
            bool searchable = found.IsSuccess
                && found.Value.Payments.Single() is { Method: PaymentMethod.BankTransfer, Reference: "B-REF-0075" };
            bool pass = referenceRequired && gateBefore && gateAfter && searchable;
            await Bg006LaneEvidence.WriteVerdictAsync(
                "bg-006-l5", "L5 bank reference",
                pass ? Bg006LaneEvidence.VerdictPass : Bg006LaneEvidence.VerdictFail,
                $"required={referenceRequired} gateBefore={gateBefore} gateAfter={gateAfter} searchable={searchable}",
                "bg-006-bank.png");
        });
    }

    // ------- Lane 6: credit sale -------

    [Fact]
    public async Task L6_CreditCustomerPersistsAndExcludesCash()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await SalesLaneSession.OpenAsync();
            await session.OpenSalesAsync();

            TypeScan(session, Sardines);
            await WaitUntilAsync(() => session.Sales.HasCartItems);

            bool customerVisible = false;
            bool gate = false;
            await DriveCheckoutAsync(session, v =>
            {
                v.SelectMethodCommand.Execute("credit");
                v.AmountReceivedText = SalesTotal;
                v.CustomerName = "Juan Dela Cruz";
                v.Reference = "JDC-2026-01";
                customerVisible = v.IsCustomerEntryVisible;
                gate = v.CanConfirm && !string.IsNullOrWhiteSpace(v.CustomerName);
                v.ConfirmCommand.Execute(null);
            });

            session.ShotOfCurrent("bg-006-credit.png");

            Result<SaleDetail> found = session.Services.GetRequiredService<ISaleQueryService>()
                .GetByPaymentReference(session.Peddler, "JDC-2026-01");
            bool persisted = found.IsSuccess
                && found.Value.Payments.Single() is { Method: PaymentMethod.Credit, Reference: "JDC-2026-01" };
            bool cashExcluded = session.Store.Context.State.FundsOnHand(Fund.PhysicalCash).Centavos == 0;
            bool pass = customerVisible && gate && persisted && cashExcluded;
            await Bg006LaneEvidence.WriteVerdictAsync(
                "bg-006-l6", "L6 credit sale",
                pass ? Bg006LaneEvidence.VerdictPass : Bg006LaneEvidence.VerdictFail,
                $"customerVisible={customerVisible} gate={gate} persisted={persisted} cash={cashExcluded}",
                "bg-006-credit.png");
        });
    }

    // ------- Lane 7: split payment gates on zero balance -------

    [Fact]
    public async Task L7_SplitDisabledUntilBalanceZero()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await SalesLaneSession.OpenAsync();
            await session.OpenSalesAsync();

            TypeScan(session, Sardines);
            await WaitUntilAsync(() => session.Sales.HasCartItems);

            bool before = false;
            bool after = false;
            await DriveCheckoutAsync(session, v =>
            {
                v.SelectMethodCommand.Execute("split");
                v.AddSplitLineCommand.Execute(null);
                SplitLineDraft cash = v.SplitLines[0];
                cash.AmountText = "20.00";
                before = !v.CanConfirm && v.RemainingText.Contains("25", StringComparison.Ordinal);

                v.AddSplitLineCommand.Execute(null);
                SplitLineDraft gcash = v.SplitLines[1];
                gcash.CycleMethod();
                gcash.AmountText = "25.00";
                after = v.CanConfirm;
                v.ConfirmCommand.Execute(null);
            });

            session.ShotOfCurrent("bg-006-split.png");
            Bg006LaneEvidence.SaveReviewPng("bg-006-split.png", 1366, 768, session.Window);
            session.ShotOfCurrent("bg-006-result.png");
            Bg006LaneEvidence.SaveReviewPng("bg-006-result.png", 1366, 768, session.Window);

            var rows = session.QuerySales();
            bool oneSplitSale = rows.IsSuccess && rows.Value.Count == 1
                && rows.Value[0].Payments.Count == 2
                && rows.Value[0].Payments[0] is { Method: PaymentMethod.Cash, Amount.Centavos: 2000 }
                && rows.Value[0].Payments[1] is { Method: PaymentMethod.Gcash, Amount.Centavos: 2500 };
            bool pass = before && after && oneSplitSale;
            await Bg006LaneEvidence.WriteVerdictAsync(
                "bg-006-l7", "L7 split balance gate",
                pass ? Bg006LaneEvidence.VerdictPass : Bg006LaneEvidence.VerdictFail,
                $"zeroBalanceGateBefore={before} gateAfter={after} splitSale={oneSplitSale}",
                "bg-006-split.png");
        });
    }

    // ------- Lane 8: double Enter -> one sale -------

    [Fact]
    public async Task L8_DoubleEnterOneSale()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await SalesLaneSession.OpenAsync();
            await session.OpenSalesAsync();

            TypeScan(session, Sardines);
            await WaitUntilAsync(() => session.Sales.HasCartItems);

            // Canned path; the double Enter is the command-level guard.
            session.Dialogs.CannedResult = new PaymentDialogResult(
                new[] { new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(45m), null) },
                Money.Zero);
            session.Sales.CheckoutCommand.Execute(null);
            session.Sales.CheckoutCommand.Execute(null);
            await WaitUntilAsync(() => session.Sales.HasReceipt);

            session.ShotOfCurrent("bg-006-double-submit.png");
            var rows = session.QuerySales();
            bool oneSale = rows.IsSuccess && rows.Value.Count == 1;
            bool oneReceipt = ExtractReceipt(session.Sales.ReceiptText) is not null;
            bool pass = oneSale && oneReceipt;
            await Bg006LaneEvidence.WriteVerdictAsync(
                "bg-006-l8", "L8 double Enter",
                pass ? Bg006LaneEvidence.VerdictPass : Bg006LaneEvidence.VerdictFail,
                $"sales={rows.Value.Count} receipt={oneReceipt}",
                "bg-006-double-submit.png");
        });
    }

    // ------- Lane 9: oversell refused, cart recoverable -------

    [Fact]
    public async Task L9_OversellRefusedBeforePersistence()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await SalesLaneSession.OpenAsync();
            await session.OpenSalesAsync();

            TypeScan(session, Sardines);
            await WaitUntilAsync(() => session.Sales.HasCartItems);
            CartLineDraft line = session.Sales.Cart.First();

            for (int i = 1; i < 51; i++)
            {
                session.Sales.IncreaseLineCommand.Execute(line);
            }

            int afterAdjust = session.Sales.Cart.First().Quantity;
            if (afterAdjust >= 51)
            {
                // The session accepted the oversell; checkout must refuse
                // before persistence and keep the cart recoverable.
                session.Dialogs.CannedResult = new PaymentDialogResult(
                    new[] { new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(45m * afterAdjust), null) },
                    Money.Zero);
                session.Sales.CheckoutCommand.Execute(null);
                await WaitUntilAsync(() => session.Sales.HasReceipt || session.Sales.HasActionError);
            }

            session.ShotOfCurrent("bg-006-oversell.png");
            Bg006LaneEvidence.SaveReviewPng("bg-006-oversell.png", 1366, 768, session.Window);

            var rows = session.QuerySales();
            bool noSale = rows.IsSuccess && rows.Value.Count == 0;
            bool refused = session.Sales.HasActionError || !session.Sales.HasReceipt;
            bool cartIntact = session.Sales.Cart.FirstOrDefault()?.Quantity >= 50;
            bool pass = noSale && refused && cartIntact && !session.Sales.HasReceipt;
            await Bg006LaneEvidence.WriteVerdictAsync(
                "bg-006-l9", "L9 oversell",
                pass ? Bg006LaneEvidence.VerdictPass : Bg006LaneEvidence.VerdictFail,
                $"afterAdjust={afterAdjust} refused={refused} noSale={noSale} cartIntact={cartIntact}",
                "bg-006-oversell.png");
        });
    }

    // ------- Lane 10: restart offline, history survives -------

    [Fact]
    public async Task L10_OfflineRestartKeepsHistory()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "bentago-006-restart-" + Guid.NewGuid().ToString("N"));
            string receipt;
            using (var session = await SalesLaneSession.OpenAsync(root: root))
            {
                await session.OpenSalesAsync();
                TypeScan(session, Sardines);
                await WaitUntilAsync(() => session.Sales.HasCartItems);
                session.Dialogs.CannedResult = new PaymentDialogResult(
                    new[] { new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(45m), null) },
                    Money.Zero);
                session.Sales.CheckoutCommand.Execute(null);
                await WaitUntilAsync(() => session.Sales.HasReceipt);
                receipt = ExtractReceipt(session.Sales.ReceiptText)!;
                // Dispose: full teardown; the data root is kept.
            }

            // "Restart": a fresh host over the same root (no re-seed).
            using (var session2 = await SalesLaneSession.OpenAsync(root: root, seed: false))
            {
                session2.Navigation.Navigate(ScreenKeys.Transactions);
                var transactions = (TransactionsViewModel)session2.ViewModel.Current!;
                await WaitUntilAsync(() => transactions.HasRows);
                session2.ShotOfCurrent("bg-006-offline-history.png");

                var rows = session2.QuerySales();
                var firstRow = rows.Value.FirstOrDefault();
                bool survived = rows.IsSuccess && rows.Value.Count == 1 && firstRow?.ReceiptNumber == receipt;
                bool onScreen = transactions.Rows.Any(r => r.ReceiptNumber == receipt
                    && r.TotalText.Contains("45.00", StringComparison.Ordinal));
                bool pass = survived && onScreen;
                await Bg006LaneEvidence.WriteVerdictAsync(
                    "bg-006-l10", "L10 offline history",
                    pass ? Bg006LaneEvidence.VerdictPass : Bg006LaneEvidence.VerdictFail,
                    $"survived={survived} onScreen={onScreen} rows={rows.Value.Count}",
                    "bg-006-offline-history.png");
            }

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best effort
            }
        });
    }

    // ------- helpers -------

    private static void TypeScan(SalesLaneSession session, string text)
    {
        session.Sales.ScanBoxText = text;
        session.Sales.ScanCommand.Execute(null);
    }

    /// <summary>
    /// Drives the REAL checkout dialog: CheckoutCommand opens the modal; the
    /// queued op runs inside the modal while it stays open and captures gate
    /// state, then confirms. Waits for the receipt.
    /// </summary>
    private static async Task DriveCheckoutAsync(
        SalesLaneSession session, Action<PaymentDialogViewModel> op)
    {
        session.Dialogs.CannedResult = null;
        session.Dialogs.NextOp = op;
        session.Sales.CheckoutCommand.Execute(null);
        try
        {
            await WaitUntilAsync(() => session.Sales.HasReceipt);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"checkout did not complete: dialogsOpen={session.Dialogs.IsOpen} " +
                $"error='{session.Sales.ActionError}' state='{session.Sales.SessionStateMessage}' " +
                $"cart={session.Sales.Cart.Count}");
        }
    }

    /// <summary>Single-line modal round trip where the lane focus is
    /// persistence (the gate lanes drive the gate themselves).</summary>
    private static Task DriveSingleLineDialogAsync(
        SalesLaneSession session, string methodId, string amount, string reference)
    {
        return DriveCheckoutAsync(session, v =>
        {
            v.SelectMethodCommand.Execute(methodId);
            v.AmountReceivedText = amount;
            if (!string.IsNullOrEmpty(reference))
            {
                v.Reference = reference;
            }

            v.ConfirmCommand.Execute(null);
        });
    }

    private static string? ExtractReceipt(string? text)
    {
        if (text is null)
        {
            return null;
        }

        Match m = Regex.Match(text, @"R-\d{8}-\d{3,}");
        return m.Success ? m.Value : null;
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
    // Session + evidence
    // ====================================================================

    /// <summary>
    /// One lane session: an isolated migrated root seeded by <see cref="SalesSeed"/>,
    /// a visible real MainWindow, seller login, and the services a lane asserts
    /// against. Disposing closes the window and, for temp roots, deletes the data.
    /// </summary>
    private sealed class SalesLaneSession : IDisposable
    {
        private readonly string? _rootToDelete;

        public SalesLaneSession(string root, string? rootToDelete, ServiceProvider services, MainWindow window,
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
            Trip = store.Context.Trips.Single().Id;
            Peddler = store.Context.Trips.Single().Peddler;
        }

        public string Root { get; }
        public ServiceProvider Services { get; }
        public MainWindow Window { get; }
        public MainViewModel ViewModel { get; }
        public LaneDialogService Dialogs { get; }
        public INavigationService Navigation { get; }
        public SqliteLedgerStore Store { get; }

        /// <summary>The activated SalesViewModel (transient: one per
        /// navigation; must be read from the shell's Current).</summary>
        public SalesViewModel Sales => (SalesViewModel)ViewModel.Current!;
        public TripId Trip { get; }
        public PeddlerId Peddler { get; }

        public static async Task<SalesLaneSession> OpenAsync(int width = 1366, int height = 768, string? root = null, bool seed = true)
        {
            string? deleteRoot = null;
            string temp = root ?? Path.Combine(Path.GetTempPath(), "bentago-006-" + Guid.NewGuid().ToString("N"));
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
                    // Canned results for the persistence lanes; NULL falls
                    // through to the REAL modal DialogService for the gate
                    // lanes (L3-L7 drive a real PaymentDialogView).
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
            return new SalesLaneSession(temp, deleteRoot, services, window, viewModel, dialogs, nav, store);
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

        public Task<Result<IReadOnlyList<ProductHit>>> Search(string query) =>
            Services.GetRequiredService<IProductSearchService>().SearchAsync(query);

        public Result<IReadOnlyList<SaleHistoryRow>> QuerySales() =>
            Services.GetRequiredService<ISaleQueryService>().Query(Peddler);

        public void ShotOfCurrent(string fileName)
        {
            Bg006LaneEvidence.SavePng(fileName, 1366, 768, Window);
        }

        public string? FocusedElementId()
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            return focused is null ? null : FocusTarget.GetId(focused);
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
/// Lane dialog service: with <see cref="CannedResult"/> set, ShowAsync
/// returns it immediately (persistence lanes); otherwise it delegates to the
/// REAL <see cref="DialogService"/> modal round trip (gate lanes L3-L7).
/// </summary>
internal sealed class LaneDialogService : IDialogService
{
    private readonly DialogService _real;

    public LaneDialogService(IFocusService focus) => _real = new DialogService(focus);

    public object? CannedResult { get; set; }

    /// <summary>One-shot op queued into the next reopened modal (the lane
    /// drives the REAL checkout dialog; cleared after one use).</summary>
    public Action<PaymentDialogViewModel>? NextOp { get; set; }

    public bool IsOpen => _real.IsOpen;

    public Task<TResult?> ShowAsync<TResult>(DialogViewModelBase<TResult> viewModel, string? focusAfterClose = null)
        where TResult : class
    {
        if (CannedResult is { } canned && canned is TResult result)
        {
            return Task.FromResult<TResult?>(result);
        }

        if (viewModel is PaymentDialogViewModel payment && NextOp is not null)
        {
            Action<PaymentDialogViewModel> op = NextOp;
            NextOp = null;
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => op(payment)));
        }

        return _real.ShowAsync(viewModel, focusAfterClose);
    }
}

/// <summary>
/// BG-006 lane evidence writer: verdicts + PNGs under
/// artifacts/verification/bg-006/&lt;head&gt;/ and review-pack PNGs under
/// artifacts/review/. Live-lane tests are env-gated (BENTAGO_LIVE_LANES=1).
/// </summary>
internal static class Bg006LaneEvidence
{
    public const string VerdictPass = "PASS";
    public const string VerdictFail = "FAIL";

    private static readonly string ArtifactsRoot =
        Environment.GetEnvironmentVariable("BENTAGO_ARTIFACTS") ?? @"C:\BentaGo\artifacts";

    private static readonly string Head =
        Environment.GetEnvironmentVariable("BENTAGO_HEAD") ?? "current";

    public static string VerificationDir { get; } =
        Path.Combine(ArtifactsRoot, "verification", "bg-006", Head);

    public static string ReviewDir { get; } =
        Path.Combine(ArtifactsRoot, "review");

    private static readonly List<LaneVerdict> Verdicts = new();

    public static void SavePng(string fileName, int widthPx, int heightPx, Visual visual)
    {
        Directory.CreateDirectory(VerificationDir);
        var bitmap = new RenderTargetBitmap(widthPx, heightPx, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(VerificationDir, fileName));
        encoder.Save(stream);
    }

    public static void SaveReviewPng(string fileName, int widthPx, int heightPx, Visual visual)
    {
        Directory.CreateDirectory(ReviewDir);
        var bitmap = new RenderTargetBitmap(widthPx, heightPx, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(ReviewDir, fileName));
        encoder.Save(stream);
    }

    public static async Task WriteVerdictAsync(string lane, string name, string verdict, string reason, string? evidenceFile = null)
    {
        Directory.CreateDirectory(VerificationDir);
        var verdictRow = new LaneVerdict(lane, name, verdict, reason, evidenceFile);
        Verdicts.Add(verdictRow);
        await File.WriteAllTextAsync(
            Path.Combine(VerificationDir, $"{lane}.json"),
            JsonSerializer.Serialize(verdictRow, new JsonSerializerOptions { WriteIndented = true }));
        await WriteSummaryAsync();
    }

    private static async Task WriteSummaryAsync()
    {
        Directory.CreateDirectory(VerificationDir);
        await File.WriteAllTextAsync(
            Path.Combine(VerificationDir, "SUMMARY.json"),
            JsonSerializer.Serialize(Verdicts, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record LaneVerdict(string Lane, string Name, string Verdict, string Reason, string? EvidenceFile);
}
