using System.IO;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// View-model disposal tests (bg-004-synthesis §10, docs/05 "view-model
/// disposal" + the 500-cycle perf proxy): retainable screens are never
/// disposed on navigate-away; a non-retainable IDisposable VM is disposed
/// once on evict and a fresh instance is created on return; NavigationService
/// shutdown dispose runs once, idempotently; the 500-cycle proxy stays under
/// the 20 MB managed-memory budget (docs/05 perf rule).
/// </summary>
public sealed class DisposalTests : IDisposable
{
    private readonly AppShellTestHost _host;

    public DisposalTests()
    {
        _host = new AppShellTestHost(extra: services =>
        {
            services.AddTransient<TestToolViewModel>();
            services.AddTransient<TestRetainableToolViewModel>();
        });
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public void NonRetainableDisposableViewModelIsDisposedOnceOnEvict()
    {
        var nav = CreateNavigationService(toolKey: "tool");
        nav.Unlock();

        Captured<TestToolViewModel> first = Capture<TestToolViewModel>("tool", nav);
        nav.Navigate("tool");
        TestToolViewModel firstVm = first.Value!;
        Assert.False(firstVm.IsDisposed);

        nav.Navigate(ScreenKeys.Home);
        Assert.True(firstVm.IsDisposed, "eviction must dispose the non-retainable VM once");

        Captured<TestToolViewModel> second = Capture<TestToolViewModel>("tool", nav);
        nav.Navigate("tool");
        Assert.NotSame(firstVm, second.Value);
        Assert.False(second.Value!.IsDisposed);

        nav.Navigate(ScreenKeys.Home);
        Assert.True(second.Value.IsDisposed);
    }

    [Fact]
    public void RetainableDisposableViewModelSurvivesEvictionAndDiesAtShutdown()
    {
        var nav = CreateNavigationService(retainableKey: "rtool");
        nav.Unlock();

        Captured<TestRetainableToolViewModel> retained = Capture<TestRetainableToolViewModel>("rtool", nav);
        nav.Navigate("rtool");
        TestRetainableToolViewModel original = retained.Value!;
        Assert.False(retained.Value!.IsDisposed);

        nav.Navigate(ScreenKeys.Home);
        Assert.False(retained.Value.IsDisposed, "retainable screens must not be disposed on navigate-away");

        nav.Navigate("rtool");
        Assert.Same(original, retained.Value);

        nav.Dispose();
        Assert.True(retained.Value.IsDisposed, "shutdown dispose must dispose every instance once");
    }

    [Fact]
    public void ServiceDisposeIsIdempotentAndDoubleDisposeIsSafe()
    {
        var nav = CreateNavigationService(toolKey: "tool", retainableKey: "rtool");
        nav.Unlock();
        nav.Navigate("tool");
        nav.Navigate("home");
        nav.Navigate("rtool");

        nav.Dispose();
        nav.Dispose(); // must not throw
    }

    [Fact]
    public void FiveHundredCycleMemoryProxyStaysUnderBudget()
    {
        var nav = CreateNavigationService(toolKey: "tool");
        nav.Unlock();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalMemory(forceFullCollection: true);

        for (int i = 0; i < 500; i++)
        {
            nav.Navigate("tool");
            nav.Navigate(ScreenKeys.Home);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long after = GC.GetTotalMemory(forceFullCollection: true);
        long growthBytes = after - before;

        // docs/05 perf rule: managed memory may not grow more than 20 MB
        // across 500 navigation changes.
        Assert.True(growthBytes < 20 * 1024 * 1024, $"managed growth {growthBytes / 1024.0 / 1024.0:0.0} MB >= 20 MB");
    }

    private NavigationService CreateNavigationService(string? toolKey = null, string? retainableKey = null)
    {
        var registrations = ScreenRegistry.All.ToList();
        if (toolKey is not null)
        {
            registrations.Add(new ScreenRegistration(toolKey, typeof(TestToolViewModel), "Tool.Field"));
        }

        if (retainableKey is not null)
        {
            registrations.Add(new ScreenRegistration(retainableKey, typeof(TestRetainableToolViewModel), "Tool.Field"));
        }

        return new NavigationService(_host.Services, new ScreenRegistry(registrations));
    }

    private sealed class Captured<T>
        where T : ViewModelBase
    {
        public T? Value;
    }

    private static Captured<T> Capture<T>(string key, NavigationService navigation)
        where T : ViewModelBase
    {
        var captured = new Captured<T>();
        navigation.Navigated += (_, e) =>
        {
            if (e.ToKey == key)
            {
                captured.Value = (T)e.ViewModel;
            }
        };
        return captured;
    }
}

internal sealed class TestToolViewModel : ViewModelBase, IDisposable
{
    public bool IsDisposed { get; private set; }

    public void Dispose() => IsDisposed = true;
}

internal sealed class TestRetainableToolViewModel : ViewModelBase, IRetainableScreen, IDisposable
{
    public bool IsDisposed { get; private set; }

    public void Dispose() => IsDisposed = true;
}
