using System.Diagnostics;
using BentaGo.App.Mvvm;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Navigation;

/// <summary>
/// The one navigation choke point (bg-004-synthesis §5.1/§5.3):
/// key resolution, idempotency, retention/eviction, focus-request emission,
/// and the perf probe all live here. No WPF types — plain xunit-testable.
/// View models are resolved from DI the first time a key is navigated to and
/// retained for the session (all BG-004 screens are <see cref="IRetainableScreen"/>);
/// a non-retainable IDisposable VM is disposed once on evict (BG-005 admin).
/// </summary>
public sealed class NavigationService : INavigationService, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IReadOnlyDictionary<string, ScreenRegistration> _registry;
    private readonly Dictionary<string, ViewModelBase> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<ViewModelBase, bool> _disposed = new(ReferenceEqualityComparer.Instance);
    private string? _currentKey;
    private bool _unlocked;
    private bool _disposedService;

    public NavigationService(IServiceProvider services, ScreenRegistry registry)
    {
        _services = services;
        _registry = registry.Registrations.ToDictionary(r => r.Key, StringComparer.Ordinal);
    }

    public event EventHandler<NavigationEventArgs>? Navigated;

    public event EventHandler<FocusRequest>? FocusRequested;

    public string? CurrentKey => _currentKey;

    public double LastNavigationMilliseconds { get; private set; }

    public bool CanNavigate(string key) =>
        _registry.ContainsKey(key) && (key == ScreenKeys.Login || _unlocked);

    public void Unlock() => _unlocked = true;

    public void Navigate(string key)
    {
        if (_disposedService)
        {
            return;
        }

        if (!CanNavigate(key) || !_registry.TryGetValue(key, out ScreenRegistration? registration))
        {
            return;
        }

        if (key == _currentKey)
        {
            return; // idempotent: re-navigation to the current screen is a no-op
        }

        var watch = Stopwatch.StartNew();

        // Evict the previous screen unless it is retained (B-graft policy).
        EvictIfNotRetained(_currentKey);

        ViewModelBase viewModel = GetOrCreate(registration.ViewModelType, key);
        string? from = _currentKey;
        _currentKey = key;

        Navigated?.Invoke(this, new NavigationEventArgs(from, key, registration.InitialFocusTarget, viewModel));
        FocusRequested?.Invoke(this, new FocusRequest(registration.InitialFocusTarget));

        watch.Stop();
        LastNavigationMilliseconds = watch.Elapsed.TotalMilliseconds;
    }

    public void RequestFocus(string elementName) =>
        FocusRequested?.Invoke(this, new FocusRequest(elementName));

    private ViewModelBase GetOrCreate(Type viewModelType, string key)
    {
        if (_instances.TryGetValue(key, out ViewModelBase? existing))
        {
            return existing;
        }

        var created = (ViewModelBase)_services.GetRequiredService(viewModelType);
        _instances[key] = created;
        return created;
    }

    private void EvictIfNotRetained(string? key)
    {
        if (key is null || !_instances.TryGetValue(key, out ViewModelBase? viewModel))
        {
            return;
        }

        if (viewModel is IRetainableScreen)
        {
            return;
        }

        DisposeOnce(viewModel);
        _instances.Remove(key);
    }

    /// <summary>App shutdown: disposes every retained instance once, idempotently.</summary>
    public void Dispose()
    {
        if (_disposedService)
        {
            return;
        }

        _disposedService = true;
        foreach (ViewModelBase viewModel in _instances.Values.ToArray())
        {
            DisposeOnce(viewModel);
        }

        _instances.Clear();
    }

    private void DisposeOnce(ViewModelBase viewModel)
    {
        if (viewModel is not IDisposable disposable || _disposed.ContainsKey(viewModel))
        {
            return;
        }

        _disposed[viewModel] = true;
        disposable.Dispose();
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<ViewModelBase>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public bool Equals(ViewModelBase? x, ViewModelBase? y) => ReferenceEquals(x, y);

        public int GetHashCode(ViewModelBase obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}