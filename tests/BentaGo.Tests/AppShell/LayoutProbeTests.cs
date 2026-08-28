using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BentaGo.App;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Layout probes (bg-004-synthesis 4.6): every screen view must fit the
/// 1092x614 DIP work area (1366x768 at 125%) and interactive targets must
/// stay >= 44 DIP. The p1 slice probes the design-system controls; screen
/// views are added as they land in p3.
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
}