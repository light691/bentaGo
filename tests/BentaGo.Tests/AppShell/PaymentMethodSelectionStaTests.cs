using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BentaGo.App;
using BentaGo.App.Dialogs.Payment;
using BentaGo.App.Mvvm;
using BentaGo.Core.Domain;
using BentaGo.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

[Collection("AppShellSta")]
public sealed class PaymentMethodSelectionStaTests
{
    private readonly AppShellStaFixture _sta;

    public PaymentMethodSelectionStaTests(AppShellStaFixture sta) => _sta = sta;

    [Fact]
    public void SelectedMethodButtonRendersGreenFill()
    {
        _sta.Run(() =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "bentago-paymethod-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var services = BentaGoHost.Build(
                    development: true,
                    dataRootOverride: temp,
                    extra: ObservableObjectFactory.RegisterAppServices);
                var payment = services.GetRequiredService<PaymentDialogViewModel>();
                payment.Load(Money.FromPesos(185m));

                Assert.True(payment.MethodOptions[0].IsSelected, "Cash should be selected after Load");

                var view = new PaymentDialogView { DataContext = payment };
                using var probe = new ProbeHost(view, 900, 660);
                _sta.PumpIdle();

                var cashButton = FindMethodButton(view);
                Assert.NotNull(cashButton);
                var chrome = (Border)cashButton.Template.FindName("Chrome", cashButton);
                var accent = (SolidColorBrush)System.Windows.Application.Current.FindResource(DesignTokens.AccentBrush);
                Assert.Equal(accent.Color, ((SolidColorBrush)chrome.Background).Color);
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

    private static Button? FindMethodButton(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button && button.CommandParameter?.ToString() == "cash")
            {
                return button;
            }

            Button? nested = FindMethodButton(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}