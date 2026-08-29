using System.Windows.Input;
using BentaGo.App.Data;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Features.Stock;

/// <summary>
/// My Stock (bg-004-synthesis §3, screen 5): searchable table with SKU,
/// loaded, sold, damaged/lost, returned, on hand, last movement + a compact
/// low-stock filter. Read-only scenario data; retained across navigation.
/// </summary>
public sealed class StockViewModel : ViewModelBase, IRetainableScreen
{
    private readonly IFocusService _focus;
    private readonly IReadOnlyList<StockRow> _rows;
    private string _searchQuery = "";
    private bool _lowOnly;

    public StockViewModel(IServiceProvider services)
    {
        _focus = services.GetRequiredService<IFocusService>();
        AppSnapshot data = services.GetRequiredService<IAppDataSource>().Current;
        _rows = data.Stock;
        Session = data.Session;
        ToggleLowOnlyCommand = new RelayCommand(_ => LowOnly = !LowOnly);
        FocusSearchCommand = new RelayCommand(_ => _focus.Request(AutomationIds.StockSearchBox));
    }

    public SessionHeader Session { get; }

    public ICommand ToggleLowOnlyCommand { get; }

    public ICommand FocusSearchCommand { get; }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                OnPropertyChanged(nameof(FilteredRows));
                OnPropertyChanged(nameof(FilteredCountText));
                OnPropertyChanged(nameof(HasFilteredRows));
            }
        }
    }

    public bool LowOnly
    {
        get => _lowOnly;
        set
        {
            if (SetProperty(ref _lowOnly, value))
            {
                OnPropertyChanged(nameof(FilteredRows));
                OnPropertyChanged(nameof(LowOnlyLabel));
                OnPropertyChanged(nameof(FilteredCountText));
                OnPropertyChanged(nameof(HasFilteredRows));
            }
        }
    }

    public string LowOnlyLabel => LowOnly ? "Showing low stock only" : "Show low stock only";

    public IReadOnlyList<StockRow> FilteredRows
    {
        get
        {
            string query = SearchQuery.Trim();
            IEnumerable<StockRow> rows = _rows;
            if (query.Length > 0)
            {
                rows = rows.Where(row =>
                    row.Sku.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || row.Product.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            if (LowOnly)
            {
                rows = rows.Where(row => row.IsLow);
            }

            return rows.ToArray();
        }
    }

    public bool HasFilteredRows => FilteredRows.Count > 0;

    public string FilteredCountText =>
        FilteredRows.Count == 1 ? "1 item shown" : $"{FilteredRows.Count} items shown";
}
