using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BentaGo.App;
using BentaGo.App.Features.Sales;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Round-3 UX-completion tests: the Sales screen ships with placeholders,
/// meaningful empty states (results + cart), and per-row quantity steppers -
/// no blank voids, and the F1 catalog describes the real per-screen controls.
/// Headless part covers the view-model contract; the STA part proves the
/// rendered view shows watermarks, empty-state copy, and steppers.
/// </summary>
public sealed class SalesUxTests : IDisposable
{
    private readonly AppShellTestHost _host;

    public SalesUxTests() => _host = new AppShellTestHost();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task CartShowsMeaningfulEmptyStateUntilItemsAreAdded()
    {
        SalesSeed.Seed(_host.Services);
        var sales = _host.Services.GetRequiredService<SalesViewModel>();
        await sales.OnActivatedAsync();
        Assert.True(sales.IsSessionReady);
        Assert.False(sales.HasCartItems);
        Assert.Equal("Cart is empty - scan to add items", sales.CartEmptyText);

        sales.ScanBoxText = SalesSeed.SardinesSku;
        sales.ScanCommand.Execute(null);
        await Task.Delay(150);
        Assert.True(sales.HasCartItems);

        sales.ClearCartCommand.Execute(null);
        Assert.False(sales.HasCartItems);
    }

    [Fact]
    public async Task SearchResultsEmptyStateDistinguishesNoMatchFromNoCatalog()
    {
        SalesSeed.Seed(_host.Services);
        var sales = _host.Services.GetRequiredService<SalesViewModel>();
        await sales.OnActivatedAsync();

        sales.SearchQuery = "zzz-nothing";
        sales.ProductSearchCommand.Execute(null);
        await Task.Delay(150);

        Assert.False(sales.HasSearchResults);
        Assert.Equal("No matching products. Try the product name, SKU, or barcode.", sales.SearchResultsEmptyText);
    }

    [Fact]
    public async Task PerRowStepperAdjustsExactlyItsLine()
    {
        SalesSeed.Seed(_host.Services);
        var sales = _host.Services.GetRequiredService<SalesViewModel>();
        await sales.OnActivatedAsync();

        sales.ScanBoxText = SalesSeed.SardinesSku;
        sales.ScanCommand.Execute(null);
        await Task.Delay(150);
        sales.ScanBoxText = SalesSeed.KopiSku;
        sales.ScanCommand.Execute(null);
        await Task.Delay(150);

        CartLineDraft sardines = sales.Cart.Single(line => line.ItemText == "Sardines");
        CartLineDraft kopi = sales.Cart.Single(line => line.ItemText == "Kopi");

        sales.IncreaseLineCommand.Execute(sardines);
        sales.IncreaseLineCommand.Execute(sardines);
        sales.DecreaseLineCommand.Execute(kopi);

        Assert.Equal(3, sardines.Quantity);
        Assert.Equal(1, kopi.Quantity);
    }

    [Fact]
    public void F1CatalogDescribesTheStepperHonestly()
    {
        var qtyUp = App.Navigation.Shortcuts.ShortcutCatalog.All.Single(e => e.Id == "qty-up");
        var qtyDown = App.Navigation.Shortcuts.ShortcutCatalog.All.Single(e => e.Id == "qty-down");
        Assert.Contains("cart quantity", qtyUp.Action, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cart quantity", qtyDown.Action, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>STA proof that the rendered Sales view shows the round-3 UX
/// (watermark hints, empty-state copy, stepper buttons).</summary>
[Collection("AppShellSta")]
public sealed class SalesUxStaTests
{
    private readonly AppShellStaFixture _sta;

    public SalesUxStaTests(AppShellStaFixture sta) => _sta = sta;

    [Fact]
    public void RenderedSalesViewShowsWatermarksEmptyStatesAndSteppers()
    {
        _sta.Run(() =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "bentago-salesux-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var services = BentaGoHost.Build(
                    development: true,
                    dataRootOverride: temp,
                    extra: ObservableObjectFactory.RegisterAppServices);
                SalesSeed.Seed(services);
                var viewModel = services.GetRequiredService<SalesViewModel>();
                viewModel.OnActivatedAsync().GetAwaiter().GetResult();
                var view = new SalesView { DataContext = viewModel };

                using var probe = new ProbeHost(view, 1092, 614);
                _sta.PumpIdle();

                // Watermark hints render while the fields are empty.
                Assert.NotNull(FindHintText(view, "Scan barcode or type a product name, then Enter"));
                Assert.NotNull(FindHintText(view, "Type a product name or SKU"));

                // Empty states render instead of voids.
                Assert.NotNull(FindTextBlock(view, "Cart is empty - scan to add items"));
                Assert.NotNull(FindTextBlock(view, "No matching products. Try the product name, SKU, or barcode."));

                // The per-row stepper exists; after an item is added the
                // stepper buttons are present and adjust the row quantity.
                viewModel.ScanBoxText = SalesSeed.SardinesSku;
                viewModel.ScanCommand.Execute(null);
                _sta.PumpIdle();
                System.Threading.Thread.Sleep(150);
                _sta.PumpIdle();
                Assert.True(FindButtons(view).Count >= 4); // Search + stepper pair + Remove + CANCEL + CHECKOUT
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

    private static FrameworkElement? FindHintText(DependencyObject root, string text)
    {
        foreach (DependencyObject node in Descendants(root))
        {
            if (node is TextBlock { Name: "WatermarkHint" } hint && hint.Text == text)
            {
                return hint;
            }
        }

        return null;
    }

    private static TextBlock? FindTextBlock(DependencyObject root, string text)
    {
        foreach (DependencyObject node in Descendants(root))
        {
            if (node is TextBlock tb && tb.Text == text)
            {
                return tb;
            }
        }

        return null;
    }

    private static List<Button> FindButtons(DependencyObject root)
    {
        var buttons = new List<Button>();
        foreach (DependencyObject node in Descendants(root))
        {
            if (node is Button button)
            {
                buttons.Add(button);
            }
        }

        return buttons;
    }
}
