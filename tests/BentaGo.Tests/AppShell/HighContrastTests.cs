using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BentaGo.App.Themes;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// High-contrast tests (bg-004-synthesis §4.4, §10): with a fake predicate,
/// the manager installs the override dictionary and brushes resolve to
/// SystemColors; removing it restores the theme palette. Live OS toggling is
/// the lane L9 operator step.
/// </summary>
[Collection("AppShellSta")]
public sealed class HighContrastTests
{
    private readonly AppShellStaFixture _sta;

    public HighContrastTests(AppShellStaFixture sta) => _sta = sta;

    [Fact]
    public void ManagerInstallsAndRemovesTheOverrideAndBrushesFollow()
    {
        _sta.Run(() =>
        {
            bool highContrast = false;
            using var manager = new HighContrastManager(_sta.Application, () => highContrast, attachSystemEvents: false);

            Assert.False(manager.IsHighContrastInstalled);
            Assert.Equal(Color.FromRgb(0x20, 0x21, 0x24), ProbeBrush("Brush.Text.Primary"));

            highContrast = true;
            manager.Apply();
            Assert.True(manager.IsHighContrastInstalled);
            Assert.Equal(SystemColors.WindowTextColor, ProbeBrush("Brush.Text.Primary"));
            Assert.Equal(SystemColors.HighlightColor, ProbeBrush("Brush.Accent"));
            Assert.Equal(SystemColors.HighlightColor, ProbeBrush("Brush.Focus"));

            highContrast = false;
            manager.Apply();
            Assert.False(manager.IsHighContrastInstalled);
            Assert.Equal(Color.FromRgb(0x20, 0x21, 0x24), ProbeBrush("Brush.Text.Primary"));
        });
    }

    private Color ProbeBrush(string key)
    {
        var block = new TextBlock();
        block.SetResourceReference(TextBlock.ForegroundProperty, key);
        using var host = new ProbeHost(new StackPanel { Children = { block } });
        _sta.PumpIdle();
        SolidColorBrush brush = Assert.IsType<SolidColorBrush>(block.Foreground);
        return brush.Color;
    }
}
