using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BentaGo.App;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Focused-bug regression (bg004-p5-fix9): the shell rail must render the
/// brand navy (#01274F, Brush.Brand.Rail) on EVERY screen in a fresh STA
/// session. A mounted high-contrast dictionary would collapse the rail to
/// the light system control colour (the "light grey rail + blue tile" look),
/// so this assert resolves the DynamicResource right after navigation.
/// </summary>
[Collection("AppShellSta")]
public sealed class ShellRailStaysBrandNavyTests
{
    private readonly AppShellStaFixture _sta;

    public ShellRailStaysBrandNavyTests(AppShellStaFixture sta) => _sta = sta;

    [Fact]
    public void RailChromeStaysBrandNavyOnEveryScreenOfAShell()
    {
        _sta.Run(() =>
        {
            using var session = ShellSession.Open(_sta);
            var rail = (Border)session.Window.FindName("RailChrome");
            Assert.NotNull(rail);

            Color expected = ((SolidColorBrush)System.Windows.Application.Current.FindResource(DesignTokens.BrandRailBrush)).Color;
            AssertRail(rail, expected, "login (before login)");

            session.ViewModel.Login.Username = "admin";
            session.ViewModel.Login.Password = "admin";
            session.ViewModel.Login.LoginCommand.Execute(null);
            _sta.PumpIdle();

            foreach ((string key, string label) in new[]
            {
                (ScreenKeys.Home, "home"),
                (ScreenKeys.Sales, "sales"),
                (ScreenKeys.Stock, "stock"),
                (ScreenKeys.Reconciliation, "reconciliation"),
            })
            {
                session.Navigation.Navigate(key);
                _sta.PumpIdle();
                AssertRail(rail, expected, label);
            }

            // The DynamicResource must also still resolve navy after a settle
            // (a late theme swap would re-render the rail light).
            _sta.PumpIdle();
            AssertRail(rail, expected, "home (resettled)");
        });
    }

    private static void AssertRail(Border rail, Color expected, string where)
    {
        var background = rail.Background as SolidColorBrush;
        Assert.True(background is not null, $"{where}: rail background is not a solid brush");
        Assert.Equal(expected, background!.Color);
    }

    /// <summary>Minimal real shell for the rail assert (no login).</summary>
    private sealed class ShellSession : IDisposable
    {
        private readonly string _tempRoot;

        private ShellSession(string tempRoot, ServiceProvider services, MainWindow window)
        {
            _tempRoot = tempRoot;
            Services = services;
            Window = window;
            Navigation = services.GetRequiredService<INavigationService>();
        }

        public ServiceProvider Services { get; }

        public MainWindow Window { get; }

        public INavigationService Navigation { get; }

        public MainViewModel ViewModel { get; private set; } = null!;

        public static ShellSession Open(AppShellStaFixture sta)
        {
            string temp = Path.Combine(Path.GetTempPath(), "bentago-rail-" + Guid.NewGuid().ToString("N"));
            var services = BentaGoHost.Build(
                development: true,
                dataRootOverride: temp,
                extra: ObservableObjectFactory.RegisterAppServices);
            var viewModel = services.GetRequiredService<MainViewModel>();
            var window = new MainWindow(viewModel)
            {
                Width = 1366,
                Height = 768,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 60,
                Top = 40,
            };
            window.Show();
            viewModel.Boot();
            sta.PumpIdle();
            var session = new ShellSession(temp, services, window) { ViewModel = viewModel };
            return session;
        }

        public void Dispose()
        {
            Window.Close();
            Services.Dispose();
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
}
