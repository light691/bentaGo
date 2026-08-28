using System.Reflection;
using System.Windows.Input;
using BentaGo.App.Data;
using BentaGo.App.Features.Help;
using BentaGo.App.Features.Login;
using BentaGo.App.Features.Reconciliation;
using BentaGo.App.Features.Sales;
using BentaGo.App.Features.Stock;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App;

/// <summary>
/// Top-level shell state (bg-004-synthesis §5.2): the active screen (drives
/// the ContentControl), the rail, the F1 help overlay, the status line, and
/// the window-level gesture surface generated from ShortcutCatalog. No
/// business logic; navigation is shell policy, everything else forwards to
/// the current screen view model.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly IFocusService _focus;
    private readonly IReadOnlyDictionary<string, (ICommand Command, object? Parameter)> _gestureMap;
    private ViewModelBase? _current;
    private string? _currentKey;
    private bool _isAuthenticated;
    private bool _isHelpOpen;
    private string? _lastFocusTarget;
    private string? _helpRestoreTarget;
    private string _statusText = "BentaGo";

    public MainViewModel(IServiceProvider services)
    {
        _navigation = services.GetRequiredService<INavigationService>();
        _focus = services.GetRequiredService<IFocusService>();
        Login = services.GetRequiredService<LoginViewModel>();
        HelpOverlay = services.GetRequiredService<HelpOverlayViewModel>();

        VersionText = "v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0");
        SimulatedLabel = "Simulated data - BG-002 scenario";

        Login.Authenticated += (_, _) => OnAuthenticated();
        _navigation.Navigated += OnNavigated;
        _focus.Requested += (_, e) => _lastFocusTarget = e.ElementName;

        NavigateCommand = new RelayCommand(
            parameter => _navigation.Navigate((string)parameter!),
            parameter => parameter is string key && _navigation.CanNavigate(key));
        HelpCommand = new RelayCommand(_ => ToggleHelp());
        EscapeCommand = new RelayCommand(_ => OnEscape());
        CheckoutCommand = new RelayCommand(_ => ForwardTo<SalesViewModel>(vm => vm.CheckoutCommand.Execute(null)));
        FocusScanCommand = new RelayCommand(_ => ForwardTo<SalesViewModel>(vm => vm.FocusScanCommand.Execute(null)));
        ProductSearchCommand = new RelayCommand(_ => ForwardTo<SalesViewModel>(vm => vm.ProductSearchCommand.Execute(null)));
        DeleteLineCommand = new RelayCommand(_ => ForwardTo<SalesViewModel>(vm => vm.RemoveLineCommand.Execute(vm.SelectedLine)));
        IncreaseQuantityCommand = new RelayCommand(_ => ForwardTo<SalesViewModel>(vm => vm.IncreaseQuantityCommand.Execute(null)));
        DecreaseQuantityCommand = new RelayCommand(_ => ForwardTo<SalesViewModel>(vm => vm.DecreaseQuantityCommand.Execute(null)));
        SaveDraftCommand = new RelayCommand(_ => ForwardTo<ReconciliationViewModel>(vm => vm.SaveDraftCommand.Execute(null)));
        FocusSearchCommand = new RelayCommand(_ =>
        {
            if (Current is SalesViewModel sales)
            {
                sales.FocusSearchCommand.Execute(null);
            }
            else if (Current is StockViewModel stock)
            {
                stock.FocusSearchCommand.Execute(null);
            }
        });

        _gestureMap = BuildGestureMap();

        BuildRail();
    }

    /// <summary>
    /// Starts the shell at the login screen. Called by the host AFTER the
    /// main window is shown so the FocusCoordinator can defer the initial
    /// focus request onto the realized LoginView (boot order, §5.2).
    /// </summary>
    public void Boot()
    {
        _navigation.Navigate(ScreenKeys.Login);
    }

    public LoginViewModel Login { get; }

    public HelpOverlayViewModel HelpOverlay { get; }

    public string VersionText { get; }

    public string SimulatedLabel { get; }

    public ViewModelBase? Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    public string? CurrentKey
    {
        get => _currentKey;
        private set => SetProperty(ref _currentKey, value);
    }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set => SetProperty(ref _isAuthenticated, value);
    }

    public bool IsHelpOpen
    {
        get => _isHelpOpen;
        private set => SetProperty(ref _isHelpOpen, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public ICommand NavigateCommand { get; }

    public ICommand HelpCommand { get; }

    public ICommand EscapeCommand { get; }

    public ICommand CheckoutCommand { get; }

    public ICommand FocusScanCommand { get; }

    public ICommand ProductSearchCommand { get; }

    public ICommand DeleteLineCommand { get; }

    public ICommand IncreaseQuantityCommand { get; }

    public ICommand DecreaseQuantityCommand { get; }

    public ICommand SaveDraftCommand { get; }

    public ICommand FocusSearchCommand { get; }

    public IReadOnlyList<RailItemViewModel> RailItems { get; private set; } = Array.Empty<RailItemViewModel>();

    /// <summary>
    /// The window-level gesture surface: catalog id -&gt; (command, parameter).
    /// MainWindow generates its InputBindings from this map against
    /// ShortcutCatalog.WindowBindings (binding/catalog parity is asserted by
    /// the shortcut conflict tests).
    /// </summary>
    public (ICommand Command, object? Parameter) CommandMapping(string catalogId) =>
        _gestureMap.TryGetValue(catalogId, out var mapping)
            ? mapping
            : throw new KeyNotFoundException($"No window command mapped for catalog id '{catalogId}'.");

    private IReadOnlyDictionary<string, (ICommand Command, object? Parameter)> BuildGestureMap()
    {
        var map = new Dictionary<string, (ICommand Command, object? Parameter)>(StringComparer.Ordinal)
        {
            ["help"] = (HelpCommand, null),
            ["focus-scan"] = (FocusScanCommand, null),
            ["product-search"] = (ProductSearchCommand, null),
            ["end-trip"] = (NavigateCommand, ScreenKeys.Reconciliation),
            ["checkout"] = (CheckoutCommand, null),
            ["cancel"] = (EscapeCommand, null),
            ["remove-line"] = (DeleteLineCommand, null),
            ["qty-up"] = (IncreaseQuantityCommand, null),
            ["qty-down"] = (DecreaseQuantityCommand, null),
            ["save-draft"] = (SaveDraftCommand, null),
            ["focus-search"] = (FocusSearchCommand, null),
            ["nav-home"] = (NavigateCommand, ScreenKeys.Home),
            ["nav-sale"] = (NavigateCommand, ScreenKeys.Sales),
            ["nav-stock"] = (NavigateCommand, ScreenKeys.Stock),
            ["nav-reconcile"] = (NavigateCommand, ScreenKeys.Reconciliation),
        };
        return map;
    }

    private void OnAuthenticated()
    {
        IsAuthenticated = true;
        StatusText = "Ready";
        _navigation.Unlock();
        RefreshRailEnablement();
        _navigation.Navigate(ScreenKeys.Home);
    }

    private void OnNavigated(object? sender, NavigationEventArgs e)
    {
        Current = e.ViewModel;
        CurrentKey = e.ToKey;
        foreach (RailItemViewModel item in RailItems)
        {
            item.IsSelected = item.Key == e.ToKey;
        }
    }

    private void BuildRail()
    {
        RailItems =
        [
            new RailItemViewModel(ScreenKeys.Home, "Home", "Alt+1", "Rail.Home"),
            new RailItemViewModel(ScreenKeys.Sales, "New Sale", "Alt+2", "Rail.Sales"),
            new RailItemViewModel(ScreenKeys.Stock, "Stock", "Alt+3", "Rail.Stock"),
            new RailItemViewModel(ScreenKeys.Transactions, "Transactions", "Alt+4", "Rail.Transactions"),
            new RailItemViewModel(ScreenKeys.Reconciliation, "Close / Reconcile", "Alt+5", "Rail.Reconciliation"),
        ];
        RefreshRailEnablement();
    }

    private void RefreshRailEnablement()
    {
        foreach (RailItemViewModel item in RailItems)
        {
            bool reserved = item.Key == ScreenKeys.Transactions;
            item.SetEnabled(reserved ? false : _navigation.CanNavigate(item.Key));
        }
    }

    private void ToggleHelp()
    {
        if (IsHelpOpen)
        {
            CloseHelp();
            return;
        }

        _helpRestoreTarget = _lastFocusTarget;
        IsHelpOpen = true;
        _focus.Request(AutomationIds.HelpOverlayRoot);
    }

    private void CloseHelp()
    {
        IsHelpOpen = false;
        string? restore = _helpRestoreTarget;
        _helpRestoreTarget = null;
        if (restore is not null)
        {
            _focus.Request(restore);
        }
    }

    private void OnEscape()
    {
        if (IsHelpOpen)
        {
            CloseHelp();
            return;
        }

        if (Current is SalesViewModel sales)
        {
            sales.ClearSearchCommand.Execute(null);
        }
        else if (Current is ReconciliationViewModel reconciliation)
        {
            reconciliation.CancelEditCommand.Execute(null);
        }
    }

    private void ForwardTo<T>(Action<T> action)
        where T : class
    {
        if (Current is T viewModel)
        {
            action(viewModel);
        }
    }
}

/// <summary>One left-rail item (bg-004-synthesis §5.2): label, keycap hint, selection.</summary>
public sealed class RailItemViewModel : ViewModelBase
{
    private bool _isSelected;
    private bool _isEnabled;

    public RailItemViewModel(string key, string label, string keyCap, string focusTargetId)
    {
        Key = key;
        Label = label;
        KeyCap = keyCap;
        FocusTargetId = focusTargetId;
    }

    public string Key { get; }

    public string Label { get; }

    public string KeyCap { get; }

    public string FocusTargetId { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        private set => SetProperty(ref _isEnabled, value);
    }

    internal void SetEnabled(bool enabled) => IsEnabled = enabled;
}