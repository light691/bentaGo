using BentaGo.App;
using BentaGo.App.Features.Login;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Focus-restoration request tests (bg-004-synthesis §10, docs/05 "focus
/// restoration requests"): navigation raises the registry's initial target,
/// the F1 overlay close re-requests the invoking target, and IFocusService
/// payloads flow through a recording fake. All headless.
/// </summary>
public sealed class FocusRestorationTests : IDisposable
{
    private readonly AppShellTestHost _host;
    private readonly RecordingFocusService _focus;

    public FocusRestorationTests()
    {
        _host = new AppShellTestHost(
            seedAdmin: true,
            extra: services => services.AddSingleton<IFocusService, RecordingFocusService>());
        _focus = (RecordingFocusService)_host.Services.GetRequiredService<IFocusService>();
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public void NavigationRaisesTheRegistriesInitialFocusTarget()
    {
        var navigation = _host.Services.GetRequiredService<INavigationService>();
        navigation.Unlock();
        var requests = new List<string>();
        navigation.FocusRequested += (_, e) => requests.Add(e.ElementName);

        navigation.Navigate(ScreenKeys.Home);
        navigation.Navigate(ScreenKeys.Sales);
        navigation.Navigate(ScreenKeys.Stock);
        navigation.Navigate(ScreenKeys.Reconciliation);

        Assert.Contains(AutomationIds.HomeNewSaleButton, requests);
        Assert.Contains(AutomationIds.SalesScanBox, requests);
        Assert.Contains(AutomationIds.StockSearchBox, requests);
        Assert.Contains(AutomationIds.ReconciliationActualCashBox, requests);
    }

    [Fact]
    public void F1OverlayCloseReRequestsTheInvokingTarget()
    {
        var main = _host.Services.GetRequiredService<MainViewModel>();
        main.Boot();
        _focus.Request(AutomationIds.HomeNewSaleButton);

        main.HelpCommand.Execute(null);
        Assert.True(main.IsHelpOpen);

        main.HelpCommand.Execute(null);
        Assert.False(main.IsHelpOpen);
        Assert.EndsWith(AutomationIds.HomeNewSaleButton, _focus.Requests[^1]);
    }

    [Fact]
    public void EscapeClosesTheOverlayInsteadOfForwarding()
    {
        var main = _host.Services.GetRequiredService<MainViewModel>();
        main.Boot();

        main.HelpCommand.Execute(null);
        Assert.True(main.IsHelpOpen);

        main.EscapeCommand.Execute(null);
        Assert.False(main.IsHelpOpen);
    }

    [Fact]
    public void RecordingFocusServiceCapturesEveryRequestPayload()
    {
        _focus.Request("Sales.ScanBox");
        _focus.Request("Stock.StockSearchBox");

        Assert.Equal(new[] { "Sales.ScanBox", "Stock.StockSearchBox" }, _focus.Requests);
    }
}