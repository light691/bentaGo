using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BentaGo.App;
using BentaGo.App.Dialogs.Confirm;
using BentaGo.App.Dialogs.Payment;
using BentaGo.App.Features.Login;
using BentaGo.App.Features.Home;
using BentaGo.App.Features.Reconciliation;
using BentaGo.App.Features.Sales;
using BentaGo.App.Features.Stock;
using BentaGo.App.Mvvm;
using BentaGo.Core.Domain;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Layout probes (bg-004-synthesis 4.6): every screen view must fit the
/// 1092x614 DIP work area (1366x768 at 125%) and interactive targets must
/// stay >= 44 DIP. Dialogs are probed at the DialogHost caps read from the
/// tokens (updated to 900x600 in round-5 so the payment dialog's method
/// strip + keypad + order summary fit with no scrollbar).
/// </summary>
[Collection("AppShellSta")]
public sealed class LayoutProbeTests
{
    private const double ProbeWidth = 1092;
    private const double ProbeHeight = 614;

    private readonly AppShellStaFixture _sta;

    public LayoutProbeTests(AppShellStaFixture sta) => _sta = sta;

    [Fact]
    public void ProbeControlsFitTheWorkArea()
    {
        _sta.Run(() =>
        {
            var content = new Grid();
            content.Children.Add(new TextBox { Margin = new Thickness(8) });
            content.Children.Add(new PasswordBox { Margin = new Thickness(8) });
            content.Children.Add(new Button
            {
                Style = (Style)System.Windows.Application.Current.FindResource(DesignTokens.ButtonPrimaryStyle),
                Content = "Primary",
                Margin = new Thickness(8),
            });
            content.Children.Add(new Button
            {
                Style = (Style)System.Windows.Application.Current.FindResource(DesignTokens.ButtonKeypadStyle),
                Content = "7",
                Margin = new Thickness(8),
            });
            content.Children.Add(new StatusBar { Height = 28, VerticalAlignment = VerticalAlignment.Bottom });

            using var host = new ProbeHost(content, ProbeWidth, ProbeHeight);
            _sta.PumpIdle();

            foreach (FrameworkElement element in content.Children.Cast<FrameworkElement>())
            {
                Rect bounds = element.TransformToAncestor(host.Window).TransformBounds(new Rect(element.RenderSize));
                Assert.True(
                    bounds.Left >= 0 && bounds.Top >= 0
                    && bounds.Right <= host.Window.ActualWidth + 0.5
                    && bounds.Bottom <= host.Window.ActualHeight + 0.5,
                    $"element {element.Name ?? element.GetType().Name} overflows the work area: {bounds}");
            }
        });
    }

    [Fact]
    public void InteractiveTargetsStayAtLeast44Dip()
    {
        _sta.Run(() =>
        {
            var primary = new Button
            {
                Style = (Style)System.Windows.Application.Current.FindResource(DesignTokens.ButtonPrimaryStyle),
                Content = "Primary",
            };
            var keypad = new Button
            {
                Style = (Style)System.Windows.Application.Current.FindResource(DesignTokens.ButtonKeypadStyle),
                Content = "7",
            };
            using var host = new ProbeHost(new StackPanel { Children = { primary, keypad } });
            _sta.PumpIdle();

            Assert.True(primary.ActualHeight >= 44, $"primary button too small: {primary.ActualHeight}");
            Assert.True(keypad.ActualHeight >= 44, $"keypad button too small: {keypad.ActualHeight}");
        });
    }

    /// <summary>
    /// Every screen view (and both dialog views) must fit its probe area:
    /// screens at 1092x614 DIP, dialogs at the DialogHost caps from the
    /// tokens (bg-004-synthesis 4.6, 14; caps 900x600 since round-5).
    /// </summary>
    [Fact]
    public void AllScreensAndDialogsFitTheirProbeArea()
    {
        _sta.Run(() =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "bentago-layout-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var services = BentaGoHost.Build(
                    development: true,
                    dataRootOverride: temp,
                    extra: ObservableObjectFactory.RegisterAppServices);
                // The app data source reads the live SQLite fold; production
                // migrates before the shell exists (App.OnStartup).
                services.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync().GetAwaiter().GetResult();

                double dialogWidth = (double)System.Windows.Application.Current.Resources[DesignTokens.DialogHostMaxWidth];
                double dialogHeight = (double)System.Windows.Application.Current.Resources[DesignTokens.DialogHostMaxHeight];

                var cases = new (string Name, FrameworkElement View, double Width, double Height)[]
                {
                    ("login", NewView<LoginView, LoginViewModel>(services), ProbeWidth, ProbeHeight),
                    ("home", NewView<HomeView, HomeViewModel>(services), ProbeWidth, ProbeHeight),
                    ("sales", NewView<SalesView, SalesViewModel>(services), ProbeWidth, ProbeHeight),
                    ("stock", NewView<StockView, StockViewModel>(services), ProbeWidth, ProbeHeight),
                    ("reconciliation", NewView<ReconciliationView, ReconciliationViewModel>(services), ProbeWidth, ProbeHeight),
                    ("payment-dialog", NewPaymentView(services), dialogWidth, dialogHeight),
                    ("confirm-dialog", NewView<ConfirmDialogView, ConfirmDialogViewModel>(services), dialogWidth, dialogHeight),
                };

                foreach ((string name, FrameworkElement view, double width, double height) in cases)
                {
                    using var probe = new ProbeHost(view, width, height);
                    _sta.PumpIdle();
                    Assert.True(
                        view.ActualWidth <= width + 0.5 && view.ActualHeight <= height + 0.5,
                        $"{name} overflows its probe area: {view.ActualWidth}x{view.ActualHeight} in {width}x{height}");
                }
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

    private static FrameworkElement NewView<TView, TViewModel>(IServiceProvider services)
        where TView : FrameworkElement, new()
        where TViewModel : class
    {
        var view = new TView();
        view.DataContext = services.GetRequiredService<TViewModel>();
        return view;
    }

    private static FrameworkElement NewPaymentView(IServiceProvider services)
    {
        var viewModel = services.GetRequiredService<PaymentDialogViewModel>();
        viewModel.Load(Money.FromPesos(1250m));
        viewModel.SplitLines.Add(new SplitLineDraft(() => { }));
        return new PaymentDialogView { DataContext = viewModel };
    }
}
