using System.Windows.Input;
using BentaGo.App.Data;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Features.Home;

/// <summary>
/// Home placeholder (BG-P1d): the BG-004 dashboard home was deleted with the
/// route-selling domain; this screen keeps the shell's Home route alive
/// (greeting + day status + primary actions) until P5 lands the real
/// register-first shell. Retained across navigation (the shell default).
/// </summary>
public sealed class HomeViewModel : ViewModelBase, IRetainableScreen
{
    private readonly INavigationService _navigation;

    public HomeViewModel(IServiceProvider services)
    {
        _navigation = services.GetRequiredService<INavigationService>();
        AppSnapshot data = services.GetRequiredService<IAppDataSource>().Current;
        Session = data.Session;

        NewSaleCommand = new RelayCommand(_ => _navigation.Navigate(ScreenKeys.Sales));
        StockCommand = new RelayCommand(_ => _navigation.Navigate(ScreenKeys.Stock));
        TransactionsCommand = new RelayCommand(_ => _navigation.Navigate(ScreenKeys.Transactions));
        CloseDayCommand = new RelayCommand(_ => _navigation.Navigate(ScreenKeys.Reconciliation));
    }

    public SessionHeader Session { get; }

    public ICommand NewSaleCommand { get; }

    public ICommand StockCommand { get; }

    public ICommand TransactionsCommand { get; }

    public ICommand CloseDayCommand { get; }
}
