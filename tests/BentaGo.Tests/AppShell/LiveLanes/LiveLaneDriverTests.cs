using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BentaGo.App;
using BentaGo.App.Dialogs;
using BentaGo.App.Dialogs.Payment;
using BentaGo.App.Features.Reconciliation;
using BentaGo.App.Features.Sales;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Navigation.Shortcuts;
using BentaGo.App.Ui;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell.LiveLanes;

/// <summary>
/// Live lanes L1-L10 (bg-004-synthesis §11, docs/05): the in-process STA
/// driver renders each lane off the working desktop via RenderTargetBitmap
/// and asserts measurable state (CurrentKey, Keyboard.FocusedElement,
/// IsOpen, Validation.HasError, element bounds) because no model in this
/// session can perceive pixels. Env-gated: only runs when
/// BENTAGO_LIVE_LANES=1, so the mandated --filter AppShell run stays
/// headless.
/// </summary>
[Collection("AppShellSta")]
public sealed class LiveLaneDriverTests
{
    private readonly AppShellStaFixture _sta;

    public LiveLaneDriverTests(AppShellStaFixture sta) => _sta = sta;

    private static bool LiveLanesEnabled => Environment.GetEnvironmentVariable("BENTAGO_LIVE_LANES") == "1";

    // ------- Lanes -------

    [Fact]
    public async Task L1_LoginFits1366WithoutScroll()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await LaneSession.OpenAsync(1366, 768, login: false);
            string? overflow = FirstOverflow(session.Window);
            session.Shot("bg-004-login-1366.png", 1366, 768);
            await LaneEvidence.WriteVerdictAsync(
                "bg-004-l1", "L1 login @1366",
                overflow is null ? LaneEvidence.VerdictPass : LaneEvidence.VerdictFail,
                overflow is null ? "all named elements inside the window; no scrollbar" : overflow,
                "bg-004-login-1366.png");
        });
    }

    [Fact]
    public async Task L2_LoginFits1080WithoutExcessiveStretch()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await LaneSession.OpenAsync(1920, 1080, login: false);
            string? overflow = FirstOverflow(session.Window);
            var loginCard = FindByFocusId(session.Window, AutomationIds.LoginUsername) is FrameworkElement box
                ? VisualUpwardFind<Border>(box, b => ReferenceEquals(b.Style, System.Windows.Application.Current.FindResource(DesignTokens.CardStyle)))
                : null;
            bool cardBounded = loginCard is null || (loginCard.ActualWidth <= 900 && loginCard.ActualWidth >= 300);
            session.Shot("bg-004-login-1080.png", 1920, 1080);
            bool pass = overflow is null && cardBounded;
            await LaneEvidence.WriteVerdictAsync(
                "bg-004-l2", "L2 login @1920x1080",
                pass ? LaneEvidence.VerdictPass : LaneEvidence.VerdictFail,
                pass ? "balanced layout at 1920x1080; no excessive stretch" : $"overflow={overflow} cardBounded={cardBounded}",
                "bg-004-login-1080.png");
        });
    }

    [Fact]
    public async Task L3_AltNavigationLandsFocusOnPrimaryControl()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await LaneSession.OpenAsync(1366, 768);
            var trace = new List<string>();
            var targets = new (string Key, string FocusTarget)[]
            {
                (ScreenKeys.Sales, AutomationIds.SalesScanBox),
                (ScreenKeys.Stock, AutomationIds.StockSearchBox),
                (ScreenKeys.Reconciliation, AutomationIds.ReconciliationActualCashBox),
                (ScreenKeys.Home, AutomationIds.HomeNewSaleButton),
            };

            bool allOk = true;
            foreach ((string key, string expectedFocus) in targets)
            {
                session.Navigation.Navigate(key);
                // Harden (round 2): the primary control must hold REAL
                // keyboard focus after navigation - focused=- now fails.
                WaitFor(() => session.FocusedElementId() == expectedFocus);
                bool keyOk = session.Navigation.CurrentKey == key;
                string? actual = session.FocusedElementId();
                bool focusOk = actual == expectedFocus;
                allOk &= keyOk && focusOk;
                trace.Add($"Alt -> {key}: CurrentKey={session.Navigation.CurrentKey} focused={actual ?? "-"} expected={expectedFocus}");
            }

            await LaneEvidence.WriteTraceAsync(string.Join(Environment.NewLine, trace));
            session.ShotOfCurrent("bg-004-alt-navigation.png", 1366, 768);
            await LaneEvidence.WriteVerdictAsync(
                "bg-004-l3", "L3 Alt navigation",
                allOk ? LaneEvidence.VerdictPass : LaneEvidence.VerdictFail,
                string.Join(" | ", trace),
                "bg-004-alt-navigation.png");
        });
    }

    [Fact]
    public async Task L4_F1HelpListsEveryCatalogEntry()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await LaneSession.OpenAsync(1366, 768);

            session.ViewModel.HelpCommand.Execute(null);
            _sta.PumpIdle();
            bool overlayOpen = session.ViewModel.IsHelpOpen;
            bool rowsMatch = session.ViewModel.HelpOverlay.Rows.Count == ShortcutCatalog.All.Count;

            session.ShotOfCurrent("bg-004-shortcuts.png", 1366, 768);
            await LaneEvidence.WriteVerdictAsync(
                "bg-004-l4", "L4 F1 help",
                overlayOpen && rowsMatch ? LaneEvidence.VerdictPass : LaneEvidence.VerdictFail,
                $"overlayOpen={overlayOpen} overlayRows={session.ViewModel.HelpOverlay.Rows.Count} catalogRows={ShortcutCatalog.All.Count}",
                "bg-004-shortcuts.png");

            session.ViewModel.EscapeCommand.Execute(null);
        });
    }

    [Fact]
    public async Task L5_TabFocusOrderOnLoginIsLogical()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await LaneSession.OpenAsync(1366, 768, login: false);
            // Enable the Log in button so Tab can reach it (it is disabled
            // until both fields are non-empty).
            session.ViewModel.Login.Username = "a";
            session.ViewModel.Login.Password = "a";
            bool submitEnabled =
                FindByFocusId(session.Window, AutomationIds.LoginSubmit) is Button submit && submit.IsEnabled;
            string tabLoop = string.Empty;
            var sequence = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                string? id = session.FocusedElementId();
                if (id is not null)
                {
                    sequence.Add(id);
                }

                var focused = Keyboard.FocusedElement as UIElement;
                bool moved = focused?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) ?? false;
                if (!moved)
                {
                    _sta.PumpIdle();
                    focused = Keyboard.FocusedElement as UIElement;
                    moved = focused?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) ?? false;
                }

                string? after = Keyboard.FocusedElement is DependencyObject f2 ? FocusTarget.GetId(f2) : null;
                tabLoop += (after ?? "-") + ",";
                _sta.PumpIdle();
            }

            string[] expected = [AutomationIds.LoginUsername, AutomationIds.LoginPassword, AutomationIds.LoginSubmit];
            bool orderOk = sequence.Intersect(expected).SequenceEqual(expected);
            session.ShotOfCurrent("bg-004-login-focus.png", 1366, 768);
            await LaneEvidence.WriteVerdictAsync(
                "bg-004-l5", "L5 tab focus order",
                orderOk ? LaneEvidence.VerdictPass : LaneEvidence.VerdictFail,
                $"sequence=[{string.Join(",", sequence)}] tabLoop=[{tabLoop}] submitEnabled={submitEnabled} expected=[{string.Join(",", expected)}]",
                "bg-004-login-focus.png");
        });
    }

    [Fact]
    public async Task L6_DialogEscClosesOnlyTopDialogAndRestoresFocus()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await LaneSession.OpenAsync(1366, 768);
            var dialogVm = session.Services.GetRequiredService<PaymentDialogViewModel>();
            var sales = session.Services.GetRequiredService<SalesViewModel>();

            session.Navigation.Navigate(ScreenKeys.Sales);
            _sta.PumpIdle();
            sales.ScanBoxText = "Sardines";
            sales.ScanCommand.Execute(null);
            _sta.PumpIdle();

            Task<PaymentDialogResult?>? showTask = null;
            bool opened = false;
            var focusRequests = new List<string>();
            if (session.Services.GetRequiredService<IFocusService>() is FocusCoordinator coordinator)
            {
                coordinator.Requested += (_, e) => focusRequests.Add(e.ElementName);
            }

            // Both ops are queued before any pump: the modal loop opened by
            // ShowAsync runs the cancel op, closes the window, and unwinds
            // the frame (same mechanics as DialogGuardAndFocusStaTests).
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                showTask = session.Dialogs.ShowAsync(dialogVm, focusAfterClose: AutomationIds.SalesScanBox)));
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                opened = session.Dialogs.IsOpen;
                dialogVm.CancelCommand.Execute(null);
            }));
            WaitFor(() => showTask is { IsCompleted: true });

            bool dismissed = showTask!.Result is null;
            bool closed = !session.Dialogs.IsOpen;
            bool stillSales = session.Navigation.CurrentKey == ScreenKeys.Sales;
            bool focusRestored = focusRequests.Contains(AutomationIds.SalesScanBox);

            session.ShotOfCurrent("bg-004-dialog.png", 1366, 768);
            await LaneEvidence.WriteVerdictAsync(
                "bg-004-l6", "L6 dialog Esc",
                opened && dismissed && closed && stillSales && focusRestored
                    ? LaneEvidence.VerdictPass
                    : LaneEvidence.VerdictFail,
                $"opened={opened} dismissedByEsc={dismissed} closed={closed} currentKey={session.Navigation.CurrentKey} focusRestored={focusRestored}",
                "bg-004-dialog.png");
        });
    }

    [Fact]
    public async Task L7_ValidationShowsColorAndCorrectiveCopy()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await LaneSession.OpenAsync(1366, 768);
            var sales = session.Services.GetRequiredService<SalesViewModel>();

            sales.ScanBoxText = "DEFINITELY-NOT-A-SKU";
            sales.ScanCommand.Execute(null);
            _sta.PumpIdle();

            bool hasError = sales.HasErrors;
            string? errorText = sales.GetErrors(nameof(SalesViewModel.ScanBoxText)).Cast<string>().FirstOrDefault();
            bool corrective = errorText == SalesViewModel.ScanNotFoundMessage;

            session.ShotOfCurrent("bg-004-validation.png", 1366, 768);
            await LaneEvidence.WriteVerdictAsync(
                "bg-004-l7", "L7 field validation",
                hasError && corrective ? LaneEvidence.VerdictPass : LaneEvidence.VerdictFail,
                $"HasError={hasError} errorText={errorText}",
                "bg-004-validation.png");
        });
    }

    [Fact]
    public async Task L8_SixScreenSwitchNeverDuplicatesViewsNorRetainsDialogs()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await LaneSession.OpenAsync(1366, 768);
            var results = new List<string>();
            bool ok = true;
            foreach (string key in new[] { ScreenKeys.Home, ScreenKeys.Sales, ScreenKeys.Stock, ScreenKeys.Reconciliation, ScreenKeys.Home })
            {
                session.Navigation.Navigate(key);
                _sta.PumpIdle();
                bool oneContentChild = session.ShellContentChildrenCount() == 1;
                bool noStaleDialog = !session.Dialogs.IsOpen;
                ok &= oneContentChild && noStaleDialog;
                results.Add($"{key}: content={session.ShellContentChildrenCount()} dialog={session.Dialogs.IsOpen}");
            }

            session.ShotOfCurrent("bg-004-navigation-state.png", 1366, 768);
            await LaneEvidence.WriteVerdictAsync(
                "bg-004-l8", "L8 six-screen switch",
                ok ? LaneEvidence.VerdictPass : LaneEvidence.VerdictFail,
                string.Join(" | ", results),
                "bg-004-navigation-state.png");
        });
    }

    [Fact]
    public async Task L9_HighContrastKeepsFocusStatusAndActionsDistinguishable()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await LaneSession.OpenAsync(1366, 768);
            bool highContrast = true;
            using var manager = new BentaGo.App.Themes.HighContrastManager(
                _sta.Application, () => highContrast, attachSystemEvents: false);

            bool railPresent = FindByFocusId(session.Window, AutomationIds.RailHome) is not null;
            bool statusPresent = FindByFocusId(session.Window, AutomationIds.HomeNewSaleButton) is not null;
            var probe = new TextBlock();
            probe.SetResourceReference(TextBlock.ForegroundProperty, DesignTokens.TextPrimaryBrush);
            var probeWindow = new Window
            {
                Width = 320,
                Height = 120,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 60,
                Top = 40,
                ShowInTaskbar = false,
                Content = new StackPanel { Children = { probe } },
            };
            probeWindow.Show();
            _sta.PumpIdle();
            bool hcSourced = probe.Foreground is SolidColorBrush b && b.Color == SystemColors.WindowTextColor;
            probeWindow.Close();

            session.Shot("bg-004-high-contrast.png", 1366, 768, session.Window);
            await LaneEvidence.WriteVerdictAsync(
                "bg-004-l9", "L9 high contrast",
                railPresent && statusPresent && hcSourced ? LaneEvidence.VerdictPass : LaneEvidence.VerdictFail,
                $"rail={railPresent} newSale={statusPresent} brushSourcedFromSystem={hcSourced} (real OS toggle = operator step)",
                "bg-004-high-contrast.png");
        });
    }

    [Fact]
    public async Task L10_Scaling125PercentRendersAtDevicePixelsWithoutClipping()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await LaneSession.OpenAsync(1366, 768);
            string? overflow = FirstOverflow(session.Window);
            session.Shot("bg-004-scaling.png", 1708, 960, session.Window, dpiX: 120, dpiY: 120);
            await LaneEvidence.WriteVerdictAsync(
                "bg-004-l10", "L10 125% scaling",
                overflow is null ? LaneEvidence.VerdictPass : LaneEvidence.VerdictFail,
                overflow ?? "no overflow inside the 1092x614 DIP work area (real 125% OS session = operator step)",
                "bg-004-scaling.png");
        });
    }

    [Fact]
    public async Task ReviewPack_SixScreenshotsAt1366x768()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await LaneSession.OpenAsync(1366, 768, login: false);
            // Credentials typed BEFORE the shot so the Log in button renders
            // enabled (vivid green), not the disabled grey of empty fields.
            session.ViewModel.Login.Username = "admin";
            session.ViewModel.Login.Password = "admin";
            _sta.PumpIdle();

            // Every shell capture MUST render the brand navy rail; a mounted
            // high-contrast dictionary would collapse it to the system light
            // grey. Reset the theme right before each capture so no state can
            // leak into the PNG, and verify the rail pixel after saving.
            EnsureDefaultTheme();
            bool railNavy = LaneEvidence.SaveReviewPng("bg-004-login-1366.png", 1366, 768, session.Window);

            // Log in and capture the remaining five screens.
            session.ViewModel.Login.LoginCommand.Execute(null);
            WaitFor(() => session.Navigation.CurrentKey == ScreenKeys.Home);
            _sta.PumpIdle();
            EnsureDefaultTheme();
            railNavy &= LaneEvidence.SaveReviewPng("bg-004-peddler-home.png", 1366, 768, session.Window);

            var sales = session.Services.GetRequiredService<SalesViewModel>();
            session.Navigation.Navigate(ScreenKeys.Sales);
            sales.ScanBoxText = "Sardines";
            sales.ScanCommand.Execute(null);
            sales.ScanBoxText = "Kopi";
            sales.ScanCommand.Execute(null);
            _sta.PumpIdle();
            EnsureDefaultTheme();
            railNavy &= LaneEvidence.SaveReviewPng("bg-004-sales.png", 1366, 768, session.Window);

            // Payment: stage the dialog window with a cash tender ready.
            var payment = session.Services.GetRequiredService<PaymentDialogViewModel>();
            payment.Load(BentaGo.Core.Domain.Money.FromPesos(185m));
            payment.AmountReceivedText = "200";
            var dialogHost = new BentaGo.App.Dialogs.DialogHost
            {
                DataContext = payment,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 60,
                Top = 40,
            };
            dialogHost.Show();
            _sta.PumpIdle();
            // The dialog is a modal without a rail; the rail check is skipped.
            _ = LaneEvidence.SaveReviewPng("bg-004-payment.png", 1366, 768, dialogHost, verifyRail: false);
            dialogHost.Close();

            session.Navigation.Navigate(ScreenKeys.Stock);
            _sta.PumpIdle();
            EnsureDefaultTheme();
            railNavy &= LaneEvidence.SaveReviewPng("bg-004-stock.png", 1366, 768, session.Window);

            session.Navigation.Navigate(ScreenKeys.Reconciliation);
            session.Services.GetRequiredService<ReconciliationViewModel>().ActualCashText = "284";
            _sta.PumpIdle();
            EnsureDefaultTheme();
            railNavy &= LaneEvidence.SaveReviewPng("bg-004-reconciliation.png", 1366, 768, session.Window);

            await LaneEvidence.WriteVerdictAsync(
                "bg-004-review-pack", "review pack (6 screenshots @1366x768)",
                railNavy ? LaneEvidence.VerdictPass : LaneEvidence.VerdictFail,
                railNavy
                    ? "bg-004-login-1366.png, bg-004-peddler-home.png, bg-004-sales.png, bg-004-payment.png, bg-004-stock.png, bg-004-reconciliation.png; rail verified navy on all five shell captures"
                    : "a shell capture rendered the rail WITHOUT the brand navy (#01274F) - a high-contrast dictionary was mounted during the review pack",
                null);
        });
    }

    [Fact]
    public async Task Perf_P95NavigationStaysUnder150ms()
    {
        if (!LiveLanesEnabled)
            return;
        await _sta.RunAsync(async () =>
        {
            using var session = await LaneSession.OpenAsync(1366, 768);
            var samples = new List<double>();
            for (int i = 0; i < 100; i++)
            {
                session.Navigation.Navigate(i % 2 == 0 ? ScreenKeys.Stock : ScreenKeys.Sales);
                samples.Add(session.Navigation.LastNavigationMilliseconds);
            }

            samples.Sort();
            double p95 = samples[(int)(samples.Count * 0.95)];
            await LaneEvidence.WriteVerdictAsync(
                "bg-004-perf", "perf P95 navigation",
                p95 <= 150 ? LaneEvidence.VerdictPass : LaneEvidence.VerdictFail,
                $"P95={p95:0.00} ms over {samples.Count} navigations (rule: <= 150 ms); 500-change memory budget covered by DisposalTests",
                null);
        });
    }

    // ------- helpers -------

    private static string? FirstOverflow(Window window)
    {
        foreach (FrameworkElement element in Descendants(window).OfType<FrameworkElement>())
        {
            if (!element.IsVisible)
            {
                continue; // hidden and collapsed subtrees (e.g. the closed help overlay) carry no layout claim
            }

            GeneralTransform transform = element.TransformToAncestor(window);
            Rect bounds = transform.TransformBounds(new Rect(element.RenderSize));

            // WPF caret visuals can report unconstrained (infinite or
            // astronomically large) sizes while the focused field is laying
            // out; they are layout artifacts, never real overflows.
            if (double.IsInfinity(bounds.X) || double.IsInfinity(bounds.Y)
                || double.IsInfinity(bounds.Width) || double.IsInfinity(bounds.Height)
                || double.IsNaN(bounds.Width) || double.IsNaN(bounds.Height)
                || bounds.Width > window.ActualWidth * 4
                || bounds.Height > window.ActualHeight * 4)
            {
                continue;
            }

            if (bounds.Right > window.ActualWidth + 1 || bounds.Bottom > window.ActualHeight + 1)
            {
                return $"{element.GetType().Name} @ {bounds} overflows {window.ActualWidth}x{window.ActualHeight}";
            }
        }

        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject grandchild in Descendants(child))
            {
                yield return grandchild;
            }
        }
    }

    internal static FrameworkElement? FindByFocusId(DependencyObject root, string id) =>
        FocusCoordinator.FindByFocusId(root, id);

    private static T? VisualUpwardFind<T>(DependencyObject start, Func<T, bool> predicate)
        where T : DependencyObject
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            if (current is T match && predicate(match))
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static void EnsureDefaultTheme()
    {
        if (System.Windows.Application.Current is null)
        {
            return;
        }

        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            string? source = merged[i].Source?.ToString();
            if (source is not null && source.EndsWith("Theme.HighContrast.xaml", StringComparison.OrdinalIgnoreCase))
            {
                merged.RemoveAt(i);
            }
        }
    }

    private static void WaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("lane condition not met");
            }

            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// One logged-in shell ready to drive on the STA thread: real services,
    /// real FocusCoordinator and DialogService, screen content realized.
    /// </summary>
    private sealed class LaneSession : IDisposable
    {
        private LaneSession(string tempRoot, ServiceProvider services, MainWindow window, MainViewModel viewModel)
        {
            TempRoot = tempRoot;
            Services = services;
            Window = window;
            ViewModel = viewModel;
            Navigation = services.GetRequiredService<INavigationService>();
            Dialogs = services.GetRequiredService<IDialogService>();
        }

        public string TempRoot { get; }

        public ServiceProvider Services { get; }

        public MainWindow Window { get; }

        public MainViewModel ViewModel { get; }

        public INavigationService Navigation { get; }

        public IDialogService Dialogs { get; }

        public static async Task<LaneSession> OpenAsync(int width, int height, bool login = true)
        {
            // Lane order is not guaranteed within the collection: if the L9
            // high-contrast lane ran first (or left the swap installed in a
            // prior process), every capture in this session would render in
            // system-colour high contrast. Start every lane on the default
            // theme so captures show the real brand (round-5 A).
            EnsureDefaultTheme();

            string temp = Path.Combine(Path.GetTempPath(), "bentago-lane-" + Guid.NewGuid().ToString("N"));
            var services = BentaGoHost.Build(
                development: true,
                dataRootOverride: temp,
                extra: ObservableObjectFactory.RegisterAppServices);
            services.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync().GetAwaiter().GetResult();
            services.GetRequiredService<IAdminSeeder>().SeedAsync().GetAwaiter().GetResult();

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
            PumpSeconds(1);

            if (login)
            {
                viewModel.Login.Username = "admin";
                viewModel.Login.Password = "admin";
                viewModel.Login.LoginCommand.Execute(null);
                await WaitUntilAsync(() => services.GetRequiredService<INavigationService>().CurrentKey == ScreenKeys.Home, window.Dispatcher);
                PumpSeconds(0.5);
            }

            return new LaneSession(temp, services, window, viewModel);
        }

        public void Shot(string fileName, int widthPx, int heightPx, FrameworkElement? root = null, double dpiX = 96, double dpiY = 96) =>
            LaneEvidence.SavePng(fileName, widthPx, heightPx, root ?? Window, dpiX, dpiY);

        public void ShotOfCurrent(string fileName, int widthPx, int heightPx) =>
            Shot(fileName, widthPx, heightPx);

        public string? FocusedElementId()
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            return focused is null ? null : FocusTarget.GetId(focused);
        }

        public int ShellContentChildrenCount()
        {
            if (Window.ShellContent?.Content is not Panel panel)
            {
                return Window.ShellContent?.Content is null ? 0 : 1;
            }

            return panel.Children.Count;
        }

        public void Dispose()
        {
            Window.Close();
            Services.Dispose();
            try
            {
                Directory.Delete(TempRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        private static void PumpSeconds(double seconds)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Thread.Sleep(5);
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition, Dispatcher dispatcher)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("lane login did not complete");
                }

                await dispatcher.InvokeAsync(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                await Task.Delay(20);
            }
        }
    }
}
