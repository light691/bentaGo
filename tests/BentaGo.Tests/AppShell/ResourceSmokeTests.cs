using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using BentaGo.App;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Internal probe host for the STA tier: a real (off-screen) Window so
/// DynamicResource values, focus, and the adorner layer all behave like a
/// running app (bg-004-synthesis 4.6 smoke probe). Rendered off the desktop
/// at -32000,-32000 so no pixels ever appear on the operator's screen.
/// </summary>
internal sealed class ProbeHost : IDisposable
{
    public ProbeHost(UIElement content, double width = 1092, double height = 614)
    {
        Window = new Window
        {
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            Content = content,
        };
        Window.Show();
    }

    public Window Window { get; }

    public void Dispose() => Window.Close();
}

/// <summary>
/// Original source for functional validation-template probing: an
/// INotifyDataErrorInfo object with one scripted error.
/// </summary>
internal sealed class InvalidSource : System.ComponentModel.INotifyDataErrorInfo
{
    private string _value = "";

    public string Value
    {
        get => _value;
        set
        {
            _value = value;
            ErrorsChanged?.Invoke(this, new System.ComponentModel.DataErrorsChangedEventArgs(nameof(Value)));
        }
    }

    public bool HasErrors => true;

    public event EventHandler<System.ComponentModel.DataErrorsChangedEventArgs>? ErrorsChanged;

    public System.Collections.IEnumerable GetErrors(string? propertyName) =>
        propertyName == nameof(Value)
            ? new[] { "Corrective message from the source." }
            : Array.Empty<string>();
}

/// <summary>
/// Design-system smoke tests (bg-004-synthesis 4.6): every DesignTokens.All
/// key resolves in the merged dictionaries, high-contrast brush parity holds,
/// control styles apply without DynamicResource-expression leaks, and the
/// validation templates render corrective copy.
/// </summary>
[Collection("AppShellSta")]
public sealed class ResourceSmokeTests
{
    private readonly AppShellStaFixture _sta;

    public ResourceSmokeTests(AppShellStaFixture sta) => _sta = sta;

    [Fact]
    public void EveryDesignTokenKeyResolves()
    {
        _sta.Run(() =>
        {
            foreach (string key in DesignTokens.All)
            {
                Assert.True(System.Windows.Application.Current.TryFindResource(key) is not null, $"missing resource key: {key}");
            }
        });
    }

    [Fact]
    public void HighContrastDictionaryHasExactBrushParity()
    {
        _sta.Run(() =>
        {
            var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
            Assert.True(merged.Count >= 1, "Theme must be a merged dictionary");

            // Load the HC override standalone (the app inserts it at runtime
            // only when high contrast is active) and compare brush keys.
            var highContrast = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/BentaGo.App;component/Themes/Theme.HighContrast.xaml"),
            };

            IEnumerable<string> brushKeys(ResourceDictionary dict) =>
                dict.Keys.Cast<object>()
                    .Where(k => k is string s && s.StartsWith("Brush.", StringComparison.Ordinal))
                    .Cast<string>()
                    .OrderBy(k => k);

            Assert.Equal(brushKeys(merged[0]), brushKeys(highContrast));
        });
    }

    [Fact]
    public void ControlStylesResolveWithoutDynamicResourceLeaks()
    {
        _sta.Run(() =>
        {
            StackPanel panel = BuildProbePanel();
            using var host = new ProbeHost(panel);
            _sta.PumpIdle();

            var button = FindByName<Button>(host.Window, "ProbeButton");
            Assert.IsType<SolidColorBrush>(button.Background);
            Assert.Equal(38, button.MinHeight);
            Assert.NotNull(button.Template);

            var textBox = FindByName<TextBox>(host.Window, "ProbeTextBox");
            Assert.IsType<SolidColorBrush>(textBox.Background);
            Assert.IsType<ControlTemplate>(System.Windows.Controls.Validation.GetErrorTemplate(textBox));

            var passwordBox = FindByName<PasswordBox>(host.Window, "ProbePasswordBox");
            Assert.IsType<ControlTemplate>(System.Windows.Controls.Validation.GetErrorTemplate(passwordBox));

            var grid = FindByName<DataGrid>(host.Window, "ProbeDataGrid");
            Assert.Equal(40, grid.RowHeight);
            Assert.True(grid.IsReadOnly);

            var statusBar = FindByName<StatusBar>(host.Window, "ProbeStatusBar");
            Assert.IsType<SolidColorBrush>(statusBar.Background);

            var primary = FindByName<Button>(host.Window, "ProbePrimary");
            var primaryChrome = (Border)primary.Template.FindName("Chrome", primary);
            Assert.Equal(Color.FromRgb(0x0B, 0x6E, 0x4F), ((SolidColorBrush)primaryChrome.Background).Color);

            var keypad = FindByName<Button>(host.Window, "ProbeKeypad");
            Assert.Equal(72, keypad.Width);
            Assert.Equal(56, keypad.Height);

            var card = FindByName<Border>(host.Window, "ProbeCard");
            Assert.IsType<SolidColorBrush>(card.Background);
            Assert.Equal(new CornerRadius(12), card.CornerRadius);
        });
    }

    [Fact]
    public void NamedTextStylesResolve()
    {
        _sta.Run(() =>
        {
            (string Key, double FontSize)[] cases =
            [
                (DesignTokens.DisplayTextStyle, 40),
                (DesignTokens.HeaderTextStyle, 26),
                (DesignTokens.TitleTextStyle, 20),
                (DesignTokens.BodyTextStyle, 15),
                (DesignTokens.LabelTextStyle, 13),
                (DesignTokens.StatusTextStyle, 12),
                (DesignTokens.CaptionTextStyle, 11),
                (DesignTokens.MoneyTextStyle, 15),
            ];

            foreach ((string key, double fontSize) in cases)
            {
                var style = (Style)System.Windows.Application.Current.FindResource(key);
                var block = new TextBlock { Style = style };
                Assert.Equal(fontSize, block.FontSize);
                Assert.IsType<SolidColorBrush>(block.Foreground);
            }

            var focusTemplate = (ControlTemplate)System.Windows.Application.Current.FindResource(DesignTokens.FocusVisualDefault);
            Assert.NotNull(focusTemplate);
        });
    }

    [Fact]
    public void ValidationTemplatesResolveAndRenderCorrectiveCopy()
    {
        _sta.Run(() =>
        {
            var template = (ControlTemplate)System.Windows.Application.Current.FindResource(DesignTokens.ErrorTemplateTextBox);
            Assert.NotNull(template);

            var source = new InvalidSource { Value = "bad" };
            var box = new TextBox
            {
                Width = 220,
                Height = 38,
                Margin = new Thickness(12),
            };
            box.SetBinding(TextBox.TextProperty, new Binding(nameof(InvalidSource.Value))
            {
                Source = source,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });

            using var host = new ProbeHost(new StackPanel { Children = { box } });
            _sta.PumpIdle();

            Assert.True(System.Windows.Controls.Validation.GetHasError(box));
            Assert.Equal(
                "Corrective message from the source.",
                System.Windows.Controls.Validation.GetErrors(box).Cast<ValidationError>().First().ErrorContent);

            bool messageRendered = false;
            AdornerLayer? adornerLayer = AdornerLayer.GetAdornerLayer(box);
            Adorner[]? adorners = adornerLayer?.GetAdorners(box);
            if (adorners is not null)
            {
                foreach (Adorner adorner in adorners)
                {
                    if (FindTextBlock(adorner, "Corrective message from the source.") is not null)
                    {
                        messageRendered = true;
                        break;
                    }
                }
            }

            Assert.True(messageRendered, "error template did not render the corrective message text");
        });
    }

    private static StackPanel BuildProbePanel()
    {
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new Button { Name = "ProbeButton" });
        content.Children.Add(new TextBox { Name = "ProbeTextBox" });
        content.Children.Add(new PasswordBox { Name = "ProbePasswordBox" });
        content.Children.Add(new DataGrid { Name = "ProbeDataGrid" });
        var statusBar = new StatusBar { Name = "ProbeStatusBar" };
        statusBar.Items.Add(new StatusBarItem { Content = "status" });
        content.Children.Add(statusBar);

        content.Children.Add(new Button
        {
            Name = "ProbePrimary",
            Style = (Style)System.Windows.Application.Current.FindResource(DesignTokens.ButtonPrimaryStyle),
            Content = "Primary",
        });
        content.Children.Add(new Button
        {
            Name = "ProbeKeypad",
            Style = (Style)System.Windows.Application.Current.FindResource(DesignTokens.ButtonKeypadStyle),
            Content = "7",
        });
        content.Children.Add(new Border
        {
            Name = "ProbeCard",
            Style = (Style)System.Windows.Application.Current.FindResource(DesignTokens.CardStyle),
            Child = new TextBlock { Text = "Card" },
        });
        return content;
    }

    private static T FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var found = FindVisual<T>(root, name);
        Assert.NotNull(found);
        return found!;
    }

    private static T? FindVisual<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T match && match.Name == name)
        {
            return match;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var result = FindVisual<T>(VisualTreeHelper.GetChild(root, i), name);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static TextBlock? FindTextBlock(DependencyObject root, string text)
    {
        if (root is TextBlock tb && tb.Text == text)
        {
            return tb;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var result = FindTextBlock(VisualTreeHelper.GetChild(root, i), text);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}