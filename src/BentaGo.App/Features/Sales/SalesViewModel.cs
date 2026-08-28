using System.Collections.ObjectModel;
using System.Windows.Input;
using BentaGo.App.Data;
using BentaGo.App.Dialogs;
using BentaGo.App.Dialogs.Confirm;
using BentaGo.App.Dialogs.Payment;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using BentaGo.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Features.Sales;

/// <summary>
/// Sales / POS state (bg-004-synthesis §3, screen 3): scan/search field,
/// results, cart lines, subtotal, CHECKOUT + CANCEL. Scanner Enter keeps
/// focus in the scan field; F12 opens the payment DIALOG (never a page).
/// The cart is session-local placeholder state (BG-005 persists on checkout)
/// and survives screen switches because this view model is retained.
/// </summary>
public sealed class SalesViewModel : ValidatableViewModelBase, IRetainableScreen
{
    /// <summary>Verbatim corrective copy from docs/03 §7.</summary>
    public const string ScanNotFoundMessage = "Barcode not found. Search by product name or ask admin to import it.";

    private readonly IServiceProvider _services;
    private readonly IDialogService _dialogs;
    private readonly IFocusService _focus;
    private readonly IReadOnlyList<ProductOffer> _catalog;
    private string _scanBoxText = "";
    private string _searchQuery = "";
    private CartLineDraft? _selectedLine;
    private string? _receiptText;
    private bool _hasReceipt;

    public SalesViewModel(IServiceProvider services)
    {
        _services = services;
        _dialogs = services.GetRequiredService<IDialogService>();
        _focus = services.GetRequiredService<IFocusService>();
        _catalog = services.GetRequiredService<IAppDataSource>().Current.Catalog;

        ScanCommand = new RelayCommand(_ => Scan(ScanBoxText));
        ProductSearchCommand = new RelayCommand(_ => RunSearch());
        SelectResultCommand = new RelayCommand(parameter => AddOfferAndClear((ProductOffer)parameter!));
        CheckoutCommand = new RelayCommand(async _ => await CheckoutAsync(), _ => Cart.Count > 0 && !HasReceipt);
        ClearCartCommand = new RelayCommand(_ => ClearCart());
        RemoveLineCommand = new RelayCommand(async parameter => await RemoveLineAsync((CartLineDraft)parameter!));
        IncreaseQuantityCommand = new RelayCommand(_ => AdjustQuantity(SelectedLine, +1), _ => SelectedLine is not null);
        DecreaseQuantityCommand = new RelayCommand(_ => AdjustQuantity(SelectedLine, -1), _ => SelectedLine is not null);
        IncreaseLineCommand = new RelayCommand(parameter => AdjustQuantity((CartLineDraft)parameter!, +1));
        DecreaseLineCommand = new RelayCommand(parameter => AdjustQuantity((CartLineDraft)parameter!, -1));
        ClearSearchCommand = new RelayCommand(_ => ClearScanAndSearch());
        FocusScanCommand = new RelayCommand(_ => _focus.Request(AutomationIds.SalesScanBox));
        FocusSearchCommand = new RelayCommand(_ => _focus.Request(AutomationIds.SalesSearchBox));
    }

    public ObservableCollection<CartLineDraft> Cart { get; } = new();

    public IReadOnlyList<ProductOffer> SearchResults { get; private set; } = Array.Empty<ProductOffer>();

    /// <summary>Per-row cart quantity steppers (round-3 UX completion).</summary>
    public ICommand IncreaseLineCommand { get; }

    public ICommand DecreaseLineCommand { get; }

    public ICommand ScanCommand { get; }

    public ICommand ProductSearchCommand { get; }

    public ICommand SelectResultCommand { get; }

    public ICommand CheckoutCommand { get; }

    public ICommand ClearCartCommand { get; }

    public ICommand RemoveLineCommand { get; }

    public ICommand IncreaseQuantityCommand { get; }

    public ICommand DecreaseQuantityCommand { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand FocusScanCommand { get; }

    public ICommand FocusSearchCommand { get; }

    public string ScanBoxText
    {
        get => _scanBoxText;
        set
        {
            if (SetProperty(ref _scanBoxText, value))
            {
                SetFieldError(nameof(ScanBoxText), null);
            }
        }
    }

    private string? _scanError;

    /// <summary>Corrective scan error, surfaced as the scan field's error.</summary>
    public string? ScanError
    {
        get => _scanError;
        set
        {
            _scanError = value;
            SetFieldError(nameof(ScanBoxText), value);
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public CartLineDraft? SelectedLine
    {
        get => _selectedLine;
        set
        {
            if (SetProperty(ref _selectedLine, value))
            {
                NotifyCartCommands();
            }
        }
    }

    public string SubtotalText => MoneyDisplay.Peso(Total);

    public string TotalText => MoneyDisplay.Peso(Total);

    /// <summary>Empty-state guidance for the results list and the cart (round-3
    /// UX completion): meaningful text instead of blank voids.</summary>
    public bool HasCartItems => Cart.Count > 0;

    public string CartEmptyText => "Cart is empty - scan to add items";

    public bool HasSearchResults => SearchResults.Count > 0;

    public string SearchResultsEmptyText =>
        _catalog.Count == 0
            ? "No products loaded - ask admin to import."
            : "No matching products. Try the product name or SKU.";

    public Money Total
    {
        get
        {
            Money total = Money.Zero;
            foreach (CartLineDraft line in Cart)
            {
                total = total.Plus(line.LineTotal);
            }

            return total;
        }
    }

    public string? ReceiptText
    {
        get => _receiptText;
        private set => SetProperty(ref _receiptText, value);
    }

    public bool HasReceipt
    {
        get => _hasReceipt;
        private set
        {
            if (SetProperty(ref _hasReceipt, value))
            {
                NotifyCartCommands();
            }
        }
    }

    /// <summary>
    /// Scan/type submit: exact SKU or case-insensitive name match adds or
    /// increments the line and keeps focus in the scan field; a miss shows
    /// corrective copy and PRESERVES the typed value (docs/03 §3).
    /// </summary>
    private void Scan(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        ProductOffer? offer = FindOffer(text.Trim());
        if (offer is null)
        {
            ScanError = ScanNotFoundMessage;
            return;
        }

        AddToCart(offer);
        ScanBoxText = "";
        _focus.Request(AutomationIds.SalesScanBox);
    }

    private void RunSearch()
    {
        string query = SearchQuery.Trim();
        if (query.Length == 0)
        {
            SearchResults = Array.Empty<ProductOffer>();
        }
        else
        {
            SearchResults = _catalog
                .Where(offer => offer.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || offer.Sku.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        OnPropertyChanged(nameof(SearchResults));
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(SearchResultsEmptyText));
    }

    private void AddOfferAndClear(ProductOffer offer)
    {
        AddToCart(offer);
        SearchQuery = "";
        SearchResults = Array.Empty<ProductOffer>();
        OnPropertyChanged(nameof(SearchResults));
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(SearchResultsEmptyText));
        _focus.Request(AutomationIds.SalesScanBox);
    }

    private ProductOffer? FindOffer(string text) =>
        _catalog.FirstOrDefault(offer =>
            string.Equals(offer.Sku, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(offer.Name, text, StringComparison.OrdinalIgnoreCase));

    private void AddToCart(ProductOffer offer)
    {
        CartLineDraft? existing = Cart.FirstOrDefault(line => line.Offer.Sku == offer.Sku);
        if (existing is not null)
        {
            existing.Quantity++;
        }
        else
        {
            Cart.Add(new CartLineDraft(offer, 1, NotifyCartChanged));
        }

        NotifyCartChanged();
    }

    private void AdjustQuantity(CartLineDraft? line, int delta)
    {
        if (line is null)
        {
            return;
        }

        int next = line.Quantity + delta;
        if (next <= 0)
        {
            return;
        }

        line.Quantity = next; // CartLineDraft raises the change callback itself
    }

    private async Task RemoveLineAsync(CartLineDraft line)
    {
        var confirm = _services.GetRequiredService<ConfirmDialogViewModel>();
        confirm.Message = $"Remove \"{line.Offer.Name}\" from the cart?";
        ConfirmDialogResult? result = await _dialogs.ShowAsync(confirm, focusAfterClose: AutomationIds.SalesScanBox);
        if (result is { Confirmed: true })
        {
            Cart.Remove(line);
            NotifyCartChanged();
        }
    }

    private async Task CheckoutAsync()
    {
        if (Cart.Count == 0)
        {
            return;
        }

        var payment = _services.GetRequiredService<PaymentDialogViewModel>();
        payment.Load(Total);
        PaymentDialogResult? result = await _dialogs.ShowAsync(payment, focusAfterClose: AutomationIds.SalesScanBox);
        if (result is null)
        {
            return; // Esc/cancel: cart untouched, scan field re-focused by the service
        }

        // BG-004 placeholder completion: BG-005 persists the sale and prints
        // the receipt. The banner is display-only over simulated data.
        ReceiptText = $"Sale complete - {result.Lines.Count} payment line(s), change {MoneyDisplay.Peso(result.Change)} (simulated data)";
        HasReceipt = true;
        Cart.Clear();
        SelectedLine = null;
        NotifyCartChanged();
    }

    private void ClearCart()
    {
        Cart.Clear();
        SelectedLine = null;
        ReceiptText = null;
        HasReceipt = false;
        NotifyCartChanged();
    }

    private void ClearScanAndSearch()
    {
        ScanBoxText = "";
        SearchQuery = "";
        SearchResults = Array.Empty<ProductOffer>();
        SetFieldError(nameof(ScanBoxText), null);
        OnPropertyChanged(nameof(SearchResults));
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(SearchResultsEmptyText));
    }

    private void NotifyCartChanged()
    {
        OnPropertyChanged(nameof(SubtotalText));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(HasCartItems));
        OnPropertyChanged(nameof(CartEmptyText));
        NotifyCartCommands();
    }

    private void NotifyCartCommands()
    {
        if (CheckoutCommand is RelayCommand checkout)
        {
            checkout.NotifyCanExecuteChanged();
        }

        if (IncreaseQuantityCommand is RelayCommand increase)
        {
            increase.NotifyCanExecuteChanged();
        }

        if (DecreaseQuantityCommand is RelayCommand decrease)
        {
            decrease.NotifyCanExecuteChanged();
        }
    }
}

/// <summary>
/// One cart line. Quantity is mutable session-local state; LineTotal is
/// display formatting of document-price data (no business math).
/// </summary>
public sealed class CartLineDraft : ViewModelBase
{
    private readonly Action _changed;
    private int _quantity;

    public CartLineDraft(ProductOffer offer, int quantity, Action changed)
    {
        Offer = offer;
        _quantity = quantity;
        _changed = changed;
    }

    public ProductOffer Offer { get; }

    public int Quantity
    {
        get => _quantity;
        set
        {
            if (SetProperty(ref _quantity, value))
            {
                _changed();
            }
        }
    }

    public string DetailText => $"{Offer.Name}  {Quantity} x {MoneyDisplay.Peso(Offer.UnitPrice)}";

    public string LineTotalText => MoneyDisplay.Peso(Offer.UnitPrice.Times(Quantity));

    public Money LineTotal => Offer.UnitPrice.Times(Quantity);
}