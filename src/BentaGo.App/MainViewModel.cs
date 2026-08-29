using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;
using BentaGo.App.Features.Help;
using BentaGo.App.Features.Login;
using BentaGo.App.Features.Reconciliation;
using BentaGo.App.Features.Sales;
using BentaGo.App.Features.Stock;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Identity;
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
    private readonly IActorContext _actorContext;
    private readonly IUserAccountStore _userStore;
    private readonly IReadOnlyDictionary<string, (ICommand Command, object? Parameter)> _gestureMap;
    private ViewModelBase? _current;
    private string? _currentKey;
    private bool _isAuthenticated;
    private bool _isHelpOpen;
    private string? _lastFocusTarget;
    private string? _helpRestoreTarget;
    private string _statusText = "BentaGo";
    private Role? _sessionRole;

    public MainViewModel(IServiceProvider services)
    {
        _navigation = services.GetRequiredService<INavigationService>();
        _focus = services.GetRequiredService<IFocusService>();
        _actorContext = services.GetRequiredService<IActorContext>();
        _userStore = services.GetRequiredService<IUserAccountStore>();
        Login = services.GetRequiredService<LoginViewModel>();
        Login.Authenticated += (_, _) => OnAuthenticated();
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
        // Admin rail navigation (BG-005d): role-gated at the command, so the
        // Alt+6/7/8 window bindings are inert for non-admin sessions even
        // though the keys are registered post-auth.
        AdminNavigateCommand = new RelayCommand(
            parameter => _navigation.Navigate((string)parameter!),
            parameter => SessionRole?.CanManageUsers() == true);
        CheckoutCommand = new RelayCommand(_ => ForwardTo<SalesViewModel>(vm => vm.CheckoutCommand.Execute(null)));
        FocusScanCommand = new RelayCommand(_ => ForwardTo<SalesViewModel>(vm => vm.FocusScanCommand.Execute(null)));
        ProductSearchCommand = new RelayCommand(_ => ForwardTo<SalesViewModel>(vm => vm.ProductSearchCommand.Execute(null)));
        // F7 (BG-006b): discount cycle on the selected cart line.
        DiscountCommand = new RelayCommand(
            _ => ForwardTo<SalesViewModel>(vm => vm.ApplyDiscountCommand.Execute(null)),
            _ => Current is SalesViewModel sales && sales.SelectedLine is not null);
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

    public LoginViewModel Login
    {
        get => _login;
        private set => SetProperty(ref _login, value);
    }

    private LoginViewModel _login = null!;

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

    /// <summary>
    /// The signed-in session role (BG-005d): learned from the actor's Users
    /// row after authentication (the shell reads the session actor's own row
    /// — no cross-account surface — and drives the admin rail entries).
    /// Role gating uses <see cref="RoleExtensions.CanManageUsers"/>.
    /// </summary>
    public Role? SessionRole
    {
        get => _sessionRole;
        private set
        {
            if (SetProperty(ref _sessionRole, value))
            {
                BuildRail();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public ICommand NavigateCommand { get; }

    public ICommand HelpCommand { get; }

    public ICommand EscapeCommand { get; }

    public ICommand AdminNavigateCommand { get; }

    public ICommand CheckoutCommand { get; }

    public ICommand FocusScanCommand { get; }

    public ICommand ProductSearchCommand { get; }

    public ICommand DiscountCommand { get; }

    public ICommand DeleteLineCommand { get; }

    public ICommand IncreaseQuantityCommand { get; }

    public ICommand DecreaseQuantityCommand { get; }

    public ICommand SaveDraftCommand { get; }

    public ICommand FocusSearchCommand { get; }

    public IReadOnlyList<RailItemViewModel> RailItems
    {
        get => _railItems;
        // SetProperty is mandatory here: BuildRail reassigns the list after
        // the session role loads (admin entries), and a plain auto-property
        // never notifies the ItemsControl - the rail would keep the startup
        // 5 items (captured by the live lane).
        private set => SetProperty(ref _railItems, value);
    }

    private IReadOnlyList<RailItemViewModel> _railItems = Array.Empty<RailItemViewModel>();

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
            ["discount"] = (DiscountCommand, null),
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
            ["nav-transactions"] = (NavigateCommand, ScreenKeys.Transactions),
            ["transactions-f9"] = (NavigateCommand, ScreenKeys.Transactions),
            ["nav-reconcile"] = (NavigateCommand, ScreenKeys.Reconciliation),
            ["nav-admin-users"] = (AdminNavigateCommand, ScreenKeys.AdminUsers),
            ["nav-admin-import"] = (AdminNavigateCommand, ScreenKeys.AdminImport),
            ["nav-admin-overview"] = (AdminNavigateCommand, ScreenKeys.AdminOverview),
        };
        return map;
    }

    private async void OnAuthenticated()
    {
        IsAuthenticated = true;
        StatusText = "Ready";
        _navigation.Unlock();
        await LoadSessionRoleAsync();
        // Rebuild the rail now that the session role is known: BuildRail
        // emits the admin entries only when SessionRole can manage users
        // (startup built the 5-item base rail while the role was null;
        // never rebuilding left the admin entries absent even though the
        // Alt+6/7/8 commands gate live and work).
        BuildRail();
        _navigation.Navigate(ScreenKeys.Home);
    }

    /// <summary>
    /// Learns the session role from the actor's own Users row (the shell
    /// needs only its own account; the admin rail entries and the Alt+6/7/8
    /// command gate on it). Fail-closed: when the read fails the role stays
    /// unknown and no admin entries appear — the authenticated shell stays
    /// usable.
    /// </summary>
    private async Task LoadSessionRoleAsync()
    {
        try
        {
            UserId? id = _actorContext.CurrentUserId;
            User? actor = id is null ? null : await _userStore.FindByIdAsync(id.Value);
            SessionRole = actor?.Role;
        }
        catch
        {
            SessionRole = null;
        }
    }

    private void OnNavigated(object? sender, NavigationEventArgs e)
    {
        Current = e.ViewModel;
        CurrentKey = e.ToKey;
        // The shell authenticates through the login VM that is actually
        // shown (the navigation-created, retained instance), not the ctor
        // probe instance.
        if (e.ViewModel is LoginViewModel login && !ReferenceEquals(login, Login))
        {
            Login = login;
            login.Authenticated += (_, _) => OnAuthenticated();
        }

        foreach (RailItemViewModel item in RailItems)
        {
            item.IsSelected = item.Key == e.ToKey;
        }

        // BG-006b: session/data VMs (re)hydrate when their screen is shown;
        // retained across navigation, activation is the refresh hook.
        if (e.ViewModel is SalesViewModel sales)
        {
            _ = sales.OnActivatedAsync();
        }
        else if (e.ViewModel is Features.Transactions.TransactionsViewModel transactions)
        {
            _ = transactions.OnActivatedAsync();
        }
    }

    private void BuildRail()
    {
        var items = new List<RailItemViewModel>
        {
            new(ScreenKeys.Home, "Home", "Alt+1", "Rail.Home"),
            new(ScreenKeys.Sales, "New Sale", "Alt+2", "Rail.Sales"),
            new(ScreenKeys.Stock, "Stock", "Alt+3", "Rail.Stock"),
            new(ScreenKeys.Transactions, "Transactions", "Alt+4", "Rail.Transactions"),
            new(ScreenKeys.Reconciliation, "Close / Reconcile", "Alt+5", "Rail.Reconciliation"),
        };

        // Admin entries (BG-005d): visible only for the Admin role (the
        // RoleExtensions capability predicate); the first entry carries the
        // group divider so the rail reads as one section.
        if (SessionRole?.CanManageUsers() == true)
        {
            items.Add(new RailItemViewModel(ScreenKeys.AdminUsers, "Users", "Alt+6", AutomationIds.RailAdminUsers, isAdminSection: true));
            items.Add(new RailItemViewModel(ScreenKeys.AdminImport, "Catalog Import", "Alt+7", AutomationIds.RailAdminImport));
            items.Add(new RailItemViewModel(ScreenKeys.AdminOverview, "Admin Overview", "Alt+8", AutomationIds.RailAdminOverview));
        }

        RailItems = items;
        RefreshRailEnablement();
    }

    private void RefreshRailEnablement()
    {
        foreach (RailItemViewModel item in RailItems)
        {
            item.SetEnabled(_navigation.CanNavigate(item.Key));
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

    public RailItemViewModel(string key, string label, string keyCap, string focusTargetId, bool isAdminSection = false)
    {
        Key = key;
        Label = label;
        KeyCap = keyCap;
        FocusTargetId = focusTargetId;
        IsAdminSection = isAdminSection;
    }

    public string Key { get; }

    public string Label { get; }

    public string KeyCap { get; }

    public string FocusTargetId { get; }

    /// <summary>True on the first admin rail entry: the shell renders a divider above it (rail grouping).</summary>
    public bool IsAdminSection { get; }

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
