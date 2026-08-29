using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BentaGo.App;
using BentaGo.App.Dialogs.Payment;
using BentaGo.App.Features.PeddlerHome;
using BentaGo.App.Features.Reconciliation;
using BentaGo.App.Features.Sales;
using BentaGo.App.Features.Stock;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using BentaGo.Core.Domain;
using BentaGo.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Round-8 image-aware UI pass regressions (bg004-p5-fix8): each test pins a
/// geometry defect that was visible in the 1366x768 review captures and is
/// fixed view-side only - a collapsed left-anchored field, a centred-off
/// search box, a short companion button, a duplicated/staggered header or
/// summary row, and a detached field label.
/// </summary>
[Collection("AppShellSta")]
public sealed class UiPassRound8StaTests
{
    private const double ProbeWidth = 1092;
    private const double ProbeHeight = 614;

    private readonly AppShellStaFixture _sta;

    public UiPassRound8StaTests(AppShellStaFixture sta) => _sta = sta;

    [Fact]
    public void Stock_SearchFieldAnchoredLeftAtTokenWidth()
    {
        _sta.Run(() =>
        {
            using var scope = NewScope();
            var view = new StockView { DataContext = scope.Services.GetRequiredService<StockViewModel>() };
            using var probe = new ProbeHost(view, ProbeWidth, ProbeHeight);
            _sta.PumpIdle();

            var box = FocusCoordinator.FindByFocusId(view, AutomationIds.StockSearchBox);
            Assert.NotNull(box);
            double searchWidth = (double)System.Windows.Application.Current.Resources[DesignTokens.FieldWidthSearch];
            Assert.Equal(HorizontalAlignment.Left, box.HorizontalAlignment);
            Assert.Equal(searchWidth, box.ActualWidth, 0.5);
            // Content origin: 24 screen padding + 12 field-row margin.
            Assert.Equal(36, box.TransformToAncestor(view).Transform(new Point(0, 0)).X, 0.5);
        });
    }

    [Fact]
    public void Reconciliation_CashBoxUsesTokenWidthInsteadOfCollapsing()
    {
        _sta.Run(() =>
        {
            using var scope = NewScope();
            var view = new ReconciliationView { DataContext = scope.Services.GetRequiredService<ReconciliationViewModel>() };
            using var probe = new ProbeHost(view, ProbeWidth, ProbeHeight);
            _sta.PumpIdle();

            var box = FocusCoordinator.FindByFocusId(view, AutomationIds.ReconciliationActualCashBox);
            Assert.NotNull(box);
            double cashWidth = (double)System.Windows.Application.Current.Resources[DesignTokens.FieldWidthCash];
            // Regression: MaxWidth+Left let the empty box collapse to its caret.
            Assert.Equal(cashWidth, box.ActualWidth, 0.5);
        });
    }

    [Fact]
    public void Sales_SearchButtonMatchesItsTallField()
    {
        _sta.Run(() =>
        {
            using var scope = NewScope();
            var view = new SalesView { DataContext = scope.Services.GetRequiredService<SalesViewModel>() };
            using var probe = new ProbeHost(view, ProbeWidth, ProbeHeight);
            _sta.PumpIdle();

            var scanBox = FocusCoordinator.FindByFocusId(view, AutomationIds.SalesScanBox);
            Assert.NotNull(scanBox);
            var searchButton = FindContent<Button>(view, "Search");
            Assert.NotNull(searchButton);
            Assert.Equal(scanBox.ActualHeight, searchButton.ActualHeight, 0.5);
        });
    }

    [Fact]
    public void Home_HeaderRendersGreetingOnceWithStatusOnTheSameLine()
    {
        _sta.Run(() =>
        {
            using var scope = NewScope();
            var viewModel = scope.Services.GetRequiredService<PeddlerHomeViewModel>();
            var view = new PeddlerHomeView { DataContext = viewModel };
            using var probe = new ProbeHost(view, ProbeWidth, ProbeHeight);
            _sta.PumpIdle();

            var textBlocks = Descendants<TextBlock>(view).ToList();
            // The session greeting already contains the peddler name; the old
            // view appended Session.PeddlerName a second time.
            Assert.Single(textBlocks, t => t.Text.StartsWith("Hello", StringComparison.Ordinal));
            Assert.DoesNotContain(textBlocks, t => t.Text == viewModel.Session.PeddlerName);

            var greeting = textBlocks.First(t => t.Text.StartsWith("Hello", StringComparison.Ordinal));
            var online = textBlocks.First(t => t.Text == "Online");
            double greetingCenter = Center(greeting, view).Y;
            double onlineCenter = Center(online, view).Y;
            Assert.True(Math.Abs(greetingCenter - onlineCenter) <= 3.0,
                $"status line must share the greeting's visual line: greeting={greetingCenter}, online={onlineCenter}");
        });
    }

    [Fact]
    public void Reconciliation_SummaryRowsShareOneLeftEdge()
    {
        _sta.Run(() =>
        {
            using var scope = NewScope();
            var view = new ReconciliationView { DataContext = scope.Services.GetRequiredService<ReconciliationViewModel>() };
            using var probe = new ProbeHost(view, ProbeWidth, ProbeHeight);
            _sta.PumpIdle();

            double GrossLeft() => Left(view, "Gross Sales");
            // Regression: uniform M margins staggered each row +12px right.
            Assert.Equal(GrossLeft(), Left(view, "Discounts"), 0.5);
            Assert.Equal(GrossLeft(), Left(view, "Net Sales"), 0.5);
        });
    }

    [Fact]
    public void Payment_AmountLabelSitsInlineWithItsFieldAndChangeIsGreen()
    {
        _sta.Run(() =>
        {
            using var scope = NewScope();
            var payment = scope.Services.GetRequiredService<PaymentDialogViewModel>();
            payment.Load(Money.FromPesos(185m));
            payment.AmountReceivedText = "200";
            var view = new PaymentDialogView { DataContext = payment };
            using var probe = new ProbeHost(view, 900, 660);
            _sta.PumpIdle();

            var box = FocusCoordinator.FindByFocusId(view, "Payment.AmountReceivedBox");
            Assert.NotNull(box);
            var label = FindText(view, "Amount received");
            Assert.NotNull(label);
            // Regression: the label was right-aligned on its own line above.
            Assert.True(Math.Abs(Center(box, view).Y - Center(label, view).Y) <= 3.0,
                "the Amount received label must sit on the input's line");

            var accent = ((SolidColorBrush)System.Windows.Application.Current.FindResource(DesignTokens.AccentBrush)).Color;
            var changeValues = Descendants<TextBlock>(view).Where(t => t.Text == "\u20B115.00").ToList();
            string allTexts = string.Join("|", Descendants<TextBlock>(view).Select(t => t.Text));
            Assert.True(changeValues.Count > 0, $"no \u20B115.00 TextBlock; texts=[{allTexts}]");
            // Regression: the top Change value rendered navy while the order
            // summary rendered the same figure green.
            Assert.All(changeValues, t =>
                Assert.Equal(accent, ((SolidColorBrush)t.Foreground).Color));
        });
    }

    // ------- helpers -------

    private static TestScope NewScope()
    {
        string temp = Path.Combine(Path.GetTempPath(), "bentago-uipass8-" + Guid.NewGuid().ToString("N"));
        var services = BentaGoHost.Build(
            development: true,
            dataRootOverride: temp,
            extra: ObservableObjectFactory.RegisterAppServices);
        return new TestScope(temp, services);
    }

    private sealed class TestScope(string temp, ServiceProvider services) : IDisposable
    {
        private readonly string _temp = temp;

        public ServiceProvider Services { get; } = services;

        public void Dispose()
        {
            Services.Dispose();
            try
            {
                Directory.Delete(_temp, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static Point Center(FrameworkElement element, FrameworkElement root)
    {
        var origin = element.TransformToAncestor(root).Transform(new Point(0, 0));
        return new Point(origin.X + element.ActualWidth / 2, origin.Y + element.ActualHeight / 2);
    }

    private static double Left(FrameworkElement root, string text)
    {
        var block = FindText(root, text);
        Assert.NotNull(block);
        return block.TransformToAncestor(root).Transform(new Point(0, 0)).X;
    }

    private static TextBlock? FindText(DependencyObject root, string text)
    {
        foreach (var block in Descendants<TextBlock>(root))
        {
            if (block.Text == text)
            {
                return block;
            }
        }

        return null;
    }

    private static T? FindContent<T>(DependencyObject root, object content) where T : ContentControl
    {
        foreach (var control in Descendants<ContentControl>(root).OfType<T>())
        {
            if (Equals(control.Content, content))
            {
                return control;
            }
        }

        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
