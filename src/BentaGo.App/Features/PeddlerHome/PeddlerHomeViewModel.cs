using System.Windows.Input;
using BentaGo.App.Data;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Features.PeddlerHome;

/// <summary>
/// Peddler home / dashboard (bg-004-synthesis §3, screen 2): header info,
/// four info cards, primary NEW SALE, secondary actions, urgent banner.
/// Retained across navigation (no session state to lose, but retention is the
/// BG-004 default).
/// </summary>
public sealed class PeddlerHomeViewModel : ViewModelBase, IRetainableScreen
{
    private readonly INavigationService _navigation;

    public PeddlerHomeViewModel(IServiceProvider services)
    {
        _navigation = services.GetRequiredService<INavigationService>();
        AppSnapshot data = services.GetRequiredService<IAppDataSource>().Current;
        Session = data.Session;
        Dashboard = data.Dashboard;

        NewSaleCommand = new RelayCommand(_ => _navigation.Navigate(ScreenKeys.Sales));
        StockCommand = new RelayCommand(_ => _navigation.Navigate(ScreenKeys.Stock));
        EndTripCommand = new RelayCommand(_ => _navigation.Navigate(ScreenKeys.Reconciliation));
        TransactionsCommand = new RelayCommand(_ => { }, _ => false); // BG-005 (Alt+4 parked)
        ReturnsCommand = new RelayCommand(_ => { }, _ => false);      // BG-005 (F8 parked)
    }

    public SessionHeader Session { get; }

    public DashboardSummary Dashboard { get; }

    public ICommand NewSaleCommand { get; }

    public ICommand StockCommand { get; }

    public ICommand EndTripCommand { get; }

    public ICommand TransactionsCommand { get; }

    public ICommand ReturnsCommand { get; }
}
