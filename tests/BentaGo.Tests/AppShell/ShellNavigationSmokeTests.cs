using System.IO;
using BentaGo.App;
using BentaGo.App.Dialogs.Confirm;
using BentaGo.App.Dialogs.Payment;
using BentaGo.App.Features.Help;
using BentaGo.App.Features.Home;
using BentaGo.App.Features.Login;
using BentaGo.App.Features.Reconciliation;
using BentaGo.App.Features.Sales;
using BentaGo.App.Features.Stock;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// p2 gate: every BG-004 view model must construct from the app host, and
/// the real shell must navigate all screens under the STA host with focus
/// requests and VM retention (bg-004-synthesis §10 "navigation").
/// </summary>
public sealed class ViewModelConstructionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "bentago-construct-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AllScreenViewModelsConstructFromHost()
    {
        using var host = BentaGoHost.Build(development: true, dataRootOverride: _tempRoot, extra: ObservableObjectFactory.RegisterAppServices);
        Migrate(host);

        Type[] screenTypes =
        [
            typeof(LoginViewModel),
            typeof(HomeViewModel),
            typeof(SalesViewModel),
            typeof(StockViewModel),
            typeof(ReconciliationViewModel),
            typeof(PaymentDialogViewModel),
            typeof(ConfirmDialogViewModel),
            typeof(HelpOverlayViewModel),
        ];

        foreach (Type type in screenTypes)
        {
            Assert.NotNull(host.GetRequiredService(type));
        }
    }

    /// <summary>
    /// The app data source reads the live SQLite fold; production migrates
    /// before the shell exists (App.OnStartup), so the test host does too.
    /// </summary>
    private static void Migrate(IServiceProvider host) =>
        host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync().GetAwaiter().GetResult();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}

/// <summary>
/// STA navigation smoke: boot -&gt; login -&gt; every screen via the real
/// NavigationService, focus requests raised, retention holds, idempotency
/// holds (bg-004-synthesis §5, §11 Layer A core).
/// </summary>
[Collection("AppShellSta")]
public sealed class ShellNavigationSmokeTests
{
    private readonly AppShellStaFixture _sta;

    public ShellNavigationSmokeTests(AppShellStaFixture sta) => _sta = sta;

    [Fact]
    public void AuthenticatedShellNavigatesAllScreens()
    {
        _sta.Run(() =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "bentago-navsmoke-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var host = BentaGoHost.Build(
                    development: true,
                    dataRootOverride: temp,
                    extra: ObservableObjectFactory.RegisterAppServices);
                host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync().GetAwaiter().GetResult();
                host.GetRequiredService<IAdminSeeder>().SeedAsync().GetAwaiter().GetResult();

                var viewModel = host.GetRequiredService<MainViewModel>();
                var navigation = host.GetRequiredService<INavigationService>();
                var focusRequests = new List<string>();
                navigation.FocusRequested += (_, e) => focusRequests.Add(e.ElementName);

                var window = new MainWindow(viewModel)
                {
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    ShowInTaskbar = false,
                };
                window.Show();
                viewModel.Boot();

                WaitFor(() => navigation.CurrentKey == ScreenKeys.Login);
                Assert.Equal(ScreenKeys.Login, navigation.CurrentKey);

                viewModel.Login.Username = "admin";
                viewModel.Login.Password = "admin";
                viewModel.Login.LoginCommand.Execute(null);
                WaitFor(() => navigation.CurrentKey == ScreenKeys.Home);
                Assert.Equal(ScreenKeys.Home, navigation.CurrentKey);

                foreach (string key in new[] { ScreenKeys.Sales, ScreenKeys.Stock, ScreenKeys.Reconciliation, ScreenKeys.Home })
                {
                    navigation.Navigate(key);
                    WaitFor(() => navigation.CurrentKey == key);
                    Assert.Equal(key, navigation.CurrentKey);
                }

                // Focus requests must carry each screen's registered initial
                // focus target.
                Assert.Contains(AutomationIds.HomeNewSaleButton, focusRequests);
                Assert.Contains(AutomationIds.SalesScanBox, focusRequests);
                Assert.Contains(AutomationIds.StockSearchBox, focusRequests);
                Assert.Contains(AutomationIds.ReconciliationActualCashBox, focusRequests);

                // Re-navigating to the current screen is a no-op (idempotent).
                navigation.Navigate(ScreenKeys.Home);
                Assert.Equal(ScreenKeys.Home, navigation.CurrentKey);

                // Retention: the home VM survives an away-and-back
                // detour (reference-equal, not recreated).
                var homeInstance = viewModel.Current;
                navigation.Navigate(ScreenKeys.Sales);
                navigation.Navigate(ScreenKeys.Home);
                WaitFor(() => navigation.CurrentKey == ScreenKeys.Home);
                Assert.True(ReferenceEquals(homeInstance, viewModel.Current), "retained VM must not be recreated");

                window.Close();
            }
            finally
            {
                try
                {
                    Directory.Delete(temp, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        });
    }

    private void WaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("condition not met within 5 s on the STA host");
            }

            _sta.PumpIdle();
            Thread.Sleep(10);
        }
    }
}
