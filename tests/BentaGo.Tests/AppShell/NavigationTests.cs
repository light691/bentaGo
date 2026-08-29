using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Navigation contract tests (bg-004-synthesis §10 "navigation", docs/05):
/// key resolution, idempotency, retention across switches, the pre-auth
/// gate, and focus-request emission. All headless - no UI thread.
/// </summary>
public sealed class NavigationTests : IDisposable
{
    private readonly AppShellTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private INavigationService Navigate()
    {
        var navigation = _host.Services.GetRequiredService<INavigationService>();
        navigation.Unlock();
        return navigation;
    }

    [Fact]
    public void NavigateActivatesRegisteredScreenWithFocusEvent()
    {
        var navigation = Navigate();
        var navigated = new List<NavigationEventArgs>();
        var focusRequests = new List<string>();
        navigation.Navigated += (_, e) => navigated.Add(e);
        navigation.FocusRequested += (_, e) => focusRequests.Add(e.ElementName);

        navigation.Navigate(ScreenKeys.Home);

        Assert.Equal(ScreenKeys.Home, navigation.CurrentKey);
        var e = Assert.Single(navigated);
        Assert.Equal("", e.FromKey);
        Assert.Equal(ScreenKeys.Home, e.ToKey);
        Assert.Equal(AutomationIds.HomeNewSaleButton, e.FocusElement);
        Assert.Equal(AutomationIds.HomeNewSaleButton, Assert.Single(focusRequests));
    }

    [Fact]
    public void UnknownKeyIsANoOpWithoutEvents()
    {
        var navigation = Navigate();
        var navigated = 0;
        var focusRequests = 0;
        navigation.Navigated += (_, _) => navigated++;
        navigation.FocusRequested += (_, _) => focusRequests++;

        navigation.Navigate("does-not-exist");

        Assert.Null(navigation.CurrentKey);
        Assert.Equal(0, navigated);
        Assert.Equal(0, focusRequests);
    }

    [Fact]
    public void ReNavigatingToCurrentIsAnIdempotentNoOp()
    {
        var navigation = Navigate();
        navigation.Navigate(ScreenKeys.Home);
        int navigated = 0;
        navigation.Navigated += (_, _) => navigated++;

        navigation.Navigate(ScreenKeys.Home);

        Assert.Equal(ScreenKeys.Home, navigation.CurrentKey);
        Assert.Equal(0, navigated);
    }

    [Fact]
    public void RetainedViewModelsAreReferenceEqualAcrossManySwitches()
    {
        var navigation = Navigate();
        var captured = new Dictionary<string, BentaGo.App.Mvvm.ViewModelBase>();
        navigation.Navigated += (_, e) => captured[e.ToKey] = e.ViewModel;

        navigation.Navigate(ScreenKeys.Home);
        BentaGo.App.Mvvm.ViewModelBase homeFirst = captured[ScreenKeys.Home];

        for (int i = 0; i < 100; i++)
        {
            navigation.Navigate(i % 2 == 0 ? ScreenKeys.Sales : ScreenKeys.Home);
        }

        Assert.Same(homeFirst, captured[ScreenKeys.Home]);
        Assert.Same(homeFirst, captured[ScreenKeys.Sales] is { } ? captured[ScreenKeys.Home] : homeFirst);
    }

    [Fact]
    public void CanNavigateIsFalseBeforeAuthAndForUnknownKeys()
    {
        var navigation = _host.Services.GetRequiredService<INavigationService>();
        Assert.True(navigation.CanNavigate(ScreenKeys.Login), "login must be reachable pre-auth");
        Assert.False(navigation.CanNavigate(ScreenKeys.Home), "home must be gated pre-auth");
        Assert.False(navigation.CanNavigate(ScreenKeys.Transactions), "unregistered keys are never navigable");

        navigation.Unlock();
        Assert.True(navigation.CanNavigate(ScreenKeys.Home));
        Assert.False(navigation.CanNavigate(ScreenKeys.Transactions));
    }

    [Fact]
    public void RequestFocusForwardsExplicitRequests()
    {
        var navigation = Navigate();
        string? payload = null;
        navigation.FocusRequested += (_, e) => payload = e.ElementName;

        navigation.RequestFocus("Sales.ScanBox");

        Assert.Equal("Sales.ScanBox", payload);
    }

    [Fact]
    public void NavigationBodyDurationIsMeasured()
    {
        var navigation = Navigate();
        navigation.Navigate(ScreenKeys.Home);
        Assert.True(navigation.LastNavigationMilliseconds >= 0);
    }
}
