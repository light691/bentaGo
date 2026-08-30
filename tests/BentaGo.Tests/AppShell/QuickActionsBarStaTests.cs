using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BentaGo.App;
using BentaGo.App.Features.PeddlerHome;
using BentaGo.App.Features.Sales;
using BentaGo.App.Mvvm;
using BentaGo.App.Ui;
using BentaGo.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

[Collection("AppShellSta")]
public sealed class QuickActionsBarStaTests
{
    private readonly AppShellStaFixture _sta;

    public QuickActionsBarStaTests(AppShellStaFixture sta) => _sta = sta;

    /// <summary>
    /// Round-4: the Quick Actions (Keyboard First) chip bar renders on Home
    /// and Sales with live chips bound to existing commands and parked chips
    /// visible-disabled with NO command - exactly the Alt+4 pattern. BG-006b:
    /// Sales trades the parked F8 Pay chip for a live F7 Discount chip.
    /// </summary>
    [Fact]
    public void QuickActionsBarRendersLiveAndParkedChipsOnHomeAndSales()
    {
        _sta.Run(() =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "bentago-qa-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var services = BentaGoHost.Build(
                    development: true,
                    dataRootOverride: temp,
                    extra: ObservableObjectFactory.RegisterAppServices);

                VerifyScreen(services, new PeddlerHomeView(), services.GetRequiredService<PeddlerHomeViewModel>(), "Home");
                VerifyScreen(services, new SalesView(), services.GetRequiredService<SalesViewModel>(), "Sales");
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

    private void VerifyScreen(IServiceProvider services, FrameworkElement view, object viewModel, string screen)
    {
        if (screen == "Sales")
        {
            // BG-006b: the Discount chip is live but gated on a selected cart
            // row, so give the session + a row for the enablement assert.
            SalesSeed.Seed((Microsoft.Extensions.DependencyInjection.ServiceProvider)services);
            var sales = (SalesViewModel)viewModel;
            sales.OnActivatedAsync().GetAwaiter().GetResult();
            sales.ScanBoxText = SalesSeed.SardinesSku;
            sales.ScanCommand.Execute(null);
            Thread.Sleep(200);
            _sta.PumpIdle();
            sales.SelectedLine = sales.Cart.FirstOrDefault();
        }

        view.DataContext = viewModel;
        using var probe = new ProbeHost((UserControl)view, 1092, 700);
        _sta.PumpIdle();

        Button scan = FindById(view, screen + ".QuickAction.Scan")
            ?? throw new Xunit.Sdk.XunitException($"{screen} Quick Action Scan chip not found");
        Button? pay = FindById(view, screen + ".QuickAction.Pay");
        Button print = FindById(view, screen + ".QuickAction.Print")
            ?? throw new Xunit.Sdk.XunitException($"{screen} Quick Action Print chip not found");
        Button? discount = FindById(view, screen + ".QuickAction.Discount");

        Assert.True(scan.IsEnabled, $"{screen} F2 Scan chip must be live");
        Assert.NotNull(scan.Command); // bound to an existing VM command
        if (screen == "Sales")
        {
            // BG-006b: F8 Pay retired from the bar; F7 Discount is live.
            Assert.Null(pay);
            Assert.NotNull(discount);
            Assert.True(discount.IsEnabled, $"{screen} F7 Discount chip must be live");
            Assert.NotNull(discount.Command);
        }
        else
        {
            Assert.False(pay!.IsEnabled, $"{screen} F8 Pay chip must be parked (disabled)");
            Assert.Null(pay.Command); // never bound - the Alt+4 pattern
            Assert.Null(discount);    // Home has no discount action
        }
        if (screen == "Sales")
        {
            // BG-007: F11 Print chip is live (prints/reprints the last receipt).
            Assert.True(print.IsEnabled, $"{screen} Ctrl+P Print chip must be live");
            Assert.NotNull(print.Command);
        }
        else
        {
            Assert.False(print.IsEnabled, $"{screen} F11 Print chip must be parked (disabled)");
            Assert.Null(print.Command); // never bound - the Alt+4 pattern
        }

        Button tall = (screen == "Sales" ? discount : pay)!;
        // Round-5 C: one Quick Actions row must show identical geometry -
        // enabled and parked chips differ only in colour/foreground.
        Assert.True(
            Math.Abs(scan.ActualHeight - tall!.ActualHeight) <= 2,
            $"{screen} enabled chip {scan.ActualHeight:F1}px vs parked chip {tall.ActualHeight:F1}px differ > 2px");
        Assert.True(
            Math.Abs(scan.ActualHeight - print.ActualHeight) <= 2,
            $"{screen} enabled chip {scan.ActualHeight:F1}px vs parked chip {print.ActualHeight:F1}px differ > 2px");

        if (screen == "Home")
        {
            Button endTrip = FindById(view, "Home.QuickAction.EndTrip")
                ?? throw new Xunit.Sdk.XunitException("Home Quick Action End Trip chip not found");
            Assert.True(endTrip.IsEnabled, "Home F12 End Trip chip must be live");
            Assert.NotNull(endTrip.Command);
        }
    }

    private static Button? FindById(DependencyObject root, string id)
    {
        foreach (DependencyObject node in Descendants(root))
        {
            if (node is Button button && FocusTarget.GetId(button) == id)
            {
                return button;
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
}
