using System.Collections.ObjectModel;
using System.Windows.Input;
using BentaGo.App.Data;
using BentaGo.App.Dialogs;
using BentaGo.App.Dialogs.Confirm;
using BentaGo.App.Dialogs.Corrections;
using BentaGo.App.Dialogs.Payment;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Display;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Receipts;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Features.Sales;

/// <summary>
/// The BG-006b sales screen: real session-backed flow over
/// <see cref="ICheckoutSession"/> (the UI never builds a NewSaleCommand and
/// never sees the store). One session per sale; the cart is a MIRROR of
/// session.Lines synchronized from the session's Changed event. Exact-once is
/// the session's typed invariant — the UI just presents SavedSaleState. The
/// keyboard loop: scan field Enter, F2 scan focus, Ctrl+F search focus, F7
/// discount cycle, F12 checkout, Delete remove, +/- quantity, Esc clear.
/// </summary>
public sealed class SalesViewModel : ValidatableViewModelBase, IRetainableScreen
{
    /// <summary>Verbatim corrective copy from docs/03 §7.</summary>
    public const string ScanNotFoundMessage = "Barcode not found. Search by product name or ask admin to import it.";

    private readonly IServiceProvider _services;
    private readonly IDialogService _dialogs;
    private readonly IFocusService _focus;
    private readonly ICheckoutSessionFactory _sessionFactory;
    private readonly IProductSearchService _search;
    private readonly IPrintService _print;
    private readonly IPrintJobStore _printJobs;
    private readonly ILedgerStore _store;
    private readonly IScannerDiagnostics _scanner;
    private EventHandler _sessionChanged = (_, _) => { };
    private ICheckoutSession? _session;
    private string _scanBoxText = "";
    private string _searchQuery = "";
    private CartLineDraft? _selectedLine;
    private string? _receiptText;
    private bool _hasReceipt;
    private bool _isSessionReady;
    private string? _sessionStateMessage;
    private string? _actionError;
    private string? _printStatusText;
    private SaleId? _lastSaleId;
    private string? _lastReceiptNumber;
    private bool _isPrinting;

    public SalesViewModel(IServiceProvider services)
    {
        _services = services;
        _dialogs = services.GetRequiredService<IDialogService>();
        _focus = services.GetRequiredService<IFocusService>();
        _sessionFactory = services.GetRequiredService<ICheckoutSessionFactory>();
        _search = services.GetRequiredService<IProductSearchService>();
        _print = services.GetRequiredService<IPrintService>();
        _printJobs = services.GetRequiredService<IPrintJobStore>();
        _store = services.GetRequiredService<ILedgerStore>();
        _scanner = services.GetRequiredService<IScannerDiagnostics>();

        ScanCommand = new RelayCommand(async _ => await ScanAsync(ScanBoxText));
        ProductSearchCommand = new RelayCommand(async _ => await RunSearchAsync());
        SelectResultCommand = new RelayCommand(parameter => AddResultAndClear((SearchResultItem)parameter!));
        CheckoutCommand = new RelayCommand(async _ => await CheckoutAsync(), _ => Cart.Count > 0 && !HasReceipt && IsSessionReady);
        ClearCartCommand = new RelayCommand(_ => ClearCart());
        RemoveLineCommand = new RelayCommand(async parameter => await RemoveLineAsync((CartLineDraft)parameter!));
        IncreaseQuantityCommand = new RelayCommand(_ => AdjustQuantity(SelectedLine, +1), _ => SelectedLine is not null);
        DecreaseQuantityCommand = new RelayCommand(_ => AdjustQuantity(SelectedLine, -1), _ => SelectedLine is not null);
        IncreaseLineCommand = new RelayCommand(parameter => AdjustQuantity((CartLineDraft)parameter!, +1));
        DecreaseLineCommand = new RelayCommand(parameter => AdjustQuantity((CartLineDraft)parameter!, -1));
        ApplyDiscountCommand = new RelayCommand(_ => CycleDiscount(SelectedLine), _ => SelectedLine is not null && IsSessionReady);
        ClearSearchCommand = new RelayCommand(_ => ClearScanAndSearch());
        FocusScanCommand = new RelayCommand(_ => _focus.Request(AutomationIds.SalesScanBox));
        FocusSearchCommand = new RelayCommand(_ => _focus.Request(AutomationIds.SalesSearchBox));
        PrintCommand = new RelayCommand(async _ => await PrintLastAsync(), _ => !IsPrinting);
        ReprintCommand = new RelayCommand(async _ => await ReprintLastAsync(), _ => !IsPrinting);
        // F8 (BG-007c2): opens the corrections panel (void/return/damage-loss/
        // adjustment). The panel resolves the actor, open trip, and approver
        // policy itself; failures are surfaced inside the panel.
        OpenCorrectionsCommand = new RelayCommand(async _ => await OpenCorrectionsAsync());
    }

    /// <summary>MainViewModel calls this when the Sales screen is navigated to;
    /// the session is created lazily once (retained across screen switches).</summary>
    public async Task OnActivatedAsync()
    {
        await EnsureSessionAsync();
    }

    private async Task EnsureSessionAsync()
    {
        if (_session is not null || IsSessionReady)
        {
            return;
        }

        Result<ICheckoutSession> created = await _sessionFactory.CreateSessionAsync();
        if (created.IsFailure)
        {
            IsSessionReady = false;
            SessionStateMessage = FriendlyError(created.Errors.FirstOrDefault()?.Code ?? "SESSION_UNAVAILABLE");
            return;
        }

        _session = created.Value;
        _sessionChanged = (_, _) => SyncCart();
        _session.Changed += _sessionChanged;
        IsSessionReady = true;
        SessionStateMessage = null;
        ActionError = null;
        SyncCart();
    }

    public ObservableCollection<CartLineDraft> Cart { get; } = new();

    public IReadOnlyList<SearchResultItem> SearchResults { get; private set; } = Array.Empty<SearchResultItem>();

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

    /// <summary>F7: cycle None → Pwd → Senior → None on the selected line.</summary>
    public ICommand ApplyDiscountCommand { get; }

    /// <summary>F11: prints/retries the last receipt (reprints when already printed).</summary>
    public ICommand PrintCommand { get; }

    /// <summary>Prints a labelled REPRINT copy of the last receipt.</summary>
    public ICommand ReprintCommand { get; }

    /// <summary>F8: opens the corrections panel (BG-007c2).</summary>
    public ICommand OpenCorrectionsCommand { get; }

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

    public bool HasCartItems => Cart.Count > 0;

    public string CartEmptyText => "Cart is empty - scan to add items";

    public bool HasSearchResults => SearchResults.Count > 0;

    public string SearchResultsEmptyText =>
        IsSessionReady
            ? "No matching products. Try the product name, SKU, or barcode."
            : "Product search is unavailable until a session is ready.";

    public string SearchResultCountText =>
        SearchResults.Count == 0 ? "No results" : $"{SearchResults.Count} result(s)";

    public string CartSummaryText =>
        Cart.Count == 0 ? "No items" : $"{Cart.Count} item(s)";

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

    public Money DiscountTotal => _session?.DiscountTotal ?? Money.Zero;

    public string DiscountText => DiscountTotal.Centavos <= 0
        ? "-₱0.00"
        : $"-{MoneyDisplay.Peso(DiscountTotal)}";

    /// <summary>False until a session is open (AUTH_ACTOR_*/NO_PEDDLER_LINK/NO_OPEN_TRIP).</summary>
    public bool IsSessionReady
    {
        get => _isSessionReady;
        private set => SetProperty(ref _isSessionReady, value);
    }

    /// <summary>Friendly explanation when no session can be opened.</summary>
    public string? SessionStateMessage
    {
        get => _sessionStateMessage;
        private set
        {
            if (SetProperty(ref _sessionStateMessage, value))
            {
                OnPropertyChanged(nameof(HasSessionStateMessage));
            }
        }
    }

    public bool HasSessionStateMessage => !string.IsNullOrEmpty(SessionStateMessage);

    /// <summary>Last action error (search, add, payment, submit) shown in the banner.</summary>
    public string? ActionError
    {
        get => _actionError;
        private set
        {
            if (SetProperty(ref _actionError, value))
            {
                OnPropertyChanged(nameof(HasActionError));
            }
        }
    }

    public bool HasActionError => !string.IsNullOrEmpty(ActionError);

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

    /// <summary>Printed / "Printer needs attention" (docs/03 §7 copy).</summary>
    public string? PrintStatusText
    {
        get => _printStatusText;
        private set => SetProperty(ref _printStatusText, value);
    }

    /// <summary>True when the last print failed and the job can be retried.</summary>
    public bool HasPrintAttention
    {
        get => _hasPrintAttention;
        private set => SetProperty(ref _hasPrintAttention, value);
    }

    private bool _hasPrintAttention;

    public bool CanPrint => HasReceipt && !IsPrinting;

    public bool IsPrinting
    {
        get => _isPrinting;
        private set
        {
            if (SetProperty(ref _isPrinting, value))
            {
                OnPropertyChanged(nameof(CanPrint));
                NotifyCartCommands();
            }
        }
    }

    /// <summary>
    /// Scan/type submit: the search service matches exact SKU → barcode →
    /// name infix (scanner CR trimmed at the service boundary). An exact hit
    /// adds to the session; a name-infix list fills the results. A miss shows
    /// corrective copy and PRESERVES the typed value (docs/03 §3).
    /// </summary>
    private async Task ScanAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!IsSessionReady)
        {
            ScanError = "No session is open.";
            return;
        }

        ActionError = null;
        Result<IReadOnlyList<ProductHit>> found = await _search.SearchAsync(text.Trim());
        if (found.IsFailure)
        {
            ScanError = FriendlyError(found.Errors.FirstOrDefault()?.Code ?? "SEARCH_FAILED");
            return;
        }

        // BG-007 scanner diagnostics: the RAW box text (suffix included) and
        // the matched product are recorded before the trimmed service search.
        _scanner.RecordScan(text, found.Value.Count == 1 ? found.Value[0].Product.Name : null, DateTimeOffset.Now);

        if (found.Value.Count == 1)
        {
            AddToSession(found.Value[0]);
            ScanBoxText = "";
            _focus.Request(AutomationIds.SalesScanBox);
            return;
        }

        if (found.Value.Count == 0)
        {
            ScanError = ScanNotFoundMessage;
            return;
        }

        // Name infix: show the list (typed value stays put).
        SetResults(found.Value);
    }

    private async Task RunSearchAsync()
    {
        string query = SearchQuery.Trim();
        if (query.Length == 0)
        {
            SetResults(Array.Empty<ProductHit>());
            return;
        }

        Result<IReadOnlyList<ProductHit>> found = await _search.SearchAsync(query);
        if (found.IsFailure)
        {
            ActionError = FriendlyError(found.Errors.FirstOrDefault()?.Code ?? "SEARCH_FAILED");
            SetResults(Array.Empty<ProductHit>());
            return;
        }

        ActionError = null;
        SetResults(found.Value);
    }

    private void AddResultAndClear(SearchResultItem item)
    {
        AddToSession(item.Hit);
        SearchQuery = "";
        SetResults(Array.Empty<ProductHit>());
        _focus.Request(AutomationIds.SalesScanBox);
    }

    private void SetResults(IReadOnlyList<ProductHit> hits)
    {
        SearchResults = hits
            .Select(h => new SearchResultItem(h.Product.Name, h.Product.Sku,
                MoneyDisplay.Peso(h.Product.RetailPrice),
                $"on hand: {h.OnHand.Units}", h))
            .ToArray();
        OnPropertyChanged(nameof(SearchResults));
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(SearchResultsEmptyText));
        OnPropertyChanged(nameof(SearchResultCountText));
    }

    private void AddToSession(ProductHit hit)
    {
        if (_session is null)
        {
            return;
        }

        Result<bool> added = _session.AddProduct(hit.Product, Quantity.Of(1));
        if (added.IsFailure)
        {
            ScanError = FriendlyError(added.Errors.FirstOrDefault()?.Code ?? "LINE_NOT_ADDED");
            return;
        }

        ScanError = null;
        ActionError = null;
    }

    /// <summary>Mirror session.Lines into the cart; syncs in place so the
    /// DataGrid selection survives a quantity change.</summary>
    private void SyncCart()
    {
        if (_session is null)
        {
            return;
        }

        // Remove gone lines.
        for (int i = Cart.Count - 1; i >= 0; i--)
        {
            if (_session.Lines.All(line => line.LineId != Cart[i].LineId))
            {
                Cart.RemoveAt(i);
            }
        }

        // Add new lines, update existing ones.
        foreach (SessionLine line in _session.Lines)
        {
            CartLineDraft? existing = Cart.FirstOrDefault(c => c.LineId == line.LineId);
            if (existing is null)
            {
                Cart.Add(new CartLineDraft(line, NotifyCartChanged));
            }
            else
            {
                existing.UpdateFrom(line, NotifyCartChanged);
            }
        }

        if (SelectedLine is not null && Cart.All(c => c.LineId != SelectedLine.LineId))
        {
            SelectedLine = null;
        }

        NotifyCartChanged();
    }

    private void AdjustQuantity(CartLineDraft? line, int delta)
    {
        if (line is null || _session is null)
        {
            return;
        }

        Result<bool> adjusted = _session.AdjustQuantity(line.LineId, delta);
        if (adjusted.IsFailure)
        {
            ActionError = FriendlyError(adjusted.Errors.FirstOrDefault()?.Code ?? "ADJUST_FAILED");
        }
        else
        {
            ActionError = null;
        }
    }

    private void CycleDiscount(CartLineDraft? line)
    {
        if (line is null || _session is null)
        {
            return;
        }

        DiscountType next = line.DiscountType switch
        {
            DiscountType.None => DiscountType.Pwd,
            DiscountType.Pwd => DiscountType.Senior,
            _ => DiscountType.None,
        };
        Result<bool> applied = _session.ApplyDiscount(line.LineId, next);
        if (applied.IsFailure)
        {
            ActionError = FriendlyError(applied.Errors.FirstOrDefault()?.Code ?? "DISCOUNT_FAILED");
        }
        else
        {
            ActionError = null;
        }
    }

    private async Task RemoveLineAsync(CartLineDraft line)
    {
        if (_session is null)
        {
            return;
        }

        var confirm = _services.GetRequiredService<ConfirmDialogViewModel>();
        confirm.Message = $"Remove \"{line.ItemText}\" from the cart?";
        ConfirmDialogResult? result = await _dialogs.ShowAsync(confirm, focusAfterClose: AutomationIds.SalesScanBox);
        if (result is { Confirmed: true })
        {
            Result<bool> removed = _session.RemoveLine(line.LineId);
            if (removed.IsFailure)
            {
                ActionError = FriendlyError(removed.Errors.FirstOrDefault()?.Code ?? "LINE_REMOVE_FAILED");
            }
        }
    }

    private async Task CheckoutAsync()
    {
        if (_session is null || Cart.Count == 0 || HasReceipt)
        {
            return;
        }

        var payment = _services.GetRequiredService<PaymentDialogViewModel>();
        payment.Load(Total, Cart.Count);
        PaymentDialogResult? result = await _dialogs.ShowAsync(payment, focusAfterClose: AutomationIds.SalesScanBox);
        if (result is null)
        {
            return; // Esc/cancel: cart untouched, scan field re-focused by the service
        }

        Result<bool> set = _session.SetPayment(result.Lines, result.Change);
        if (set.IsFailure)
        {
            ActionError = FriendlyError(set.Errors.FirstOrDefault()?.Code ?? "PAYMENT_FAILED");
            return; // cart untouched; the user can re-open the dialog
        }

        Result<SavedSaleState> submitted = await _session.SubmitAsync();
        if (submitted.IsFailure)
        {
            ActionError = FriendlyError(submitted.Errors.FirstOrDefault()?.Code ?? "SUBMIT_FAILED");
            return;
        }

        SavedSaleState saved = submitted.Value;
        string payments = string.Join("; ",
            saved.Payments.Select(p =>
                $"{p.Method.DisplayName()} {MoneyDisplay.Peso(p.Amount)}"));
        ReceiptText = $"Sale {saved.ReceiptNumber} complete - total {MoneyDisplay.Peso(saved.Total)} " +
                      $"({payments})";
        HasReceipt = true;
        ActionError = null;
        _lastSaleId = saved.SaleId;
        _lastReceiptNumber = saved.ReceiptNumber;

        // BG-007: the print happens AFTER the commit (docs/03 §7) — the sale
        // is saved either way; a failed print shows "Printer needs attention"
        // and leaves one recoverable job for retry/reprint.
        await PrintAfterCommitAsync(saved.SaleId);

        // The sale is saved; the next scan starts a NEW sale on the same trip.
        _session.Changed -= _sessionChanged;
        _session = null;
        IsSessionReady = false;
        await EnsureSessionAsync();
    }

    private async Task PrintAfterCommitAsync(SaleId saleId)
    {
        IsPrinting = true;
        try
        {
            _store.Bind();
            Sale? committed = _store.Context.FindSale(saleId);
            if (committed is null)
            {
                PrintStatusText = "Sale saved. Printer needs attention.";
                HasPrintAttention = true;
                return;
            }

            PrintOutcome outcome = await _print.PrintAfterCommitAsync(committed);
            PrintStatusText = outcome.UserMessage;
            HasPrintAttention = outcome.Status != PrintJobStatus.Printed;
        }
        finally
        {
            IsPrinting = false;
        }
    }

    /// <summary>F11: reprints the last receipt (or retries a failed job).</summary>
    private async Task PrintLastAsync()
    {
        if (_lastSaleId is null)
        {
            return;
        }

        IsPrinting = true;
        try
        {
            PrintJob? existing = await _printJobs.FindBySaleAsync(_lastSaleId.Value, reprintInclusive: false);
            PrintOutcome outcome = existing is { Status: PrintJobStatus.Failed }
                ? await _print.RetryAsync(_lastSaleId.Value)
                : await ReprintAsync();

            PrintStatusText = outcome.UserMessage;
            HasPrintAttention = outcome.Status != PrintJobStatus.Printed;
        }
        finally
        {
            IsPrinting = false;
        }
    }

    private async Task<PrintOutcome> ReprintAsync()
    {
        if (_lastSaleId is null)
        {
            return new PrintOutcome("", PrintJobStatus.Failed, "NO_SALE", IsReprint: true, 0);
        }

        _store.Bind();
        Sale? committed = _store.Context.FindSale(_lastSaleId.Value);
        return committed is null
            ? new PrintOutcome(_lastReceiptNumber ?? "", PrintJobStatus.Failed, "NO_SALE", IsReprint: true, 0)
            : await _print.ReprintAsync(committed);
    }

    private async Task ReprintLastAsync()
    {
        if (_lastSaleId is null)
        {
            return;
        }

        IsPrinting = true;
        try
        {
            PrintOutcome outcome = await ReprintAsync();
            PrintStatusText = outcome.UserMessage;
            HasPrintAttention = outcome.Status != PrintJobStatus.Printed;
        }
        finally
        {
            IsPrinting = false;
        }
    }

    /// <summary>F8 (BG-007c2): opens the corrections panel over this screen.
    /// A committed correction re-focuses the scan box; the panel's own banner
    /// carries every rejection copy.</summary>
    private async Task OpenCorrectionsAsync()
    {
        var corrections = _services.GetRequiredService<CorrectionsDialogViewModel>();
        await corrections.LoadAsync();
        CorrectionsDialogResult? result = await _dialogs.ShowAsync(
            corrections, focusAfterClose: AutomationIds.SalesScanBox);
        if (result is not null)
        {
            ActionError = null;
        }
    }

    private void ClearCart()
    {
        if (_session is null)
        {
            return;
        }

        foreach (CartLineDraft line in Cart.ToArray())
        {
            _session.RemoveLine(line.LineId);
        }

        SelectedLine = null;
        ReceiptText = null;
        HasReceipt = false;
        ActionError = null;
        PrintStatusText = null;
        HasPrintAttention = false;
        _lastSaleId = null;
        _lastReceiptNumber = null;
        NotifyCartChanged();
    }

    private void ClearScanAndSearch()
    {
        ScanBoxText = "";
        SearchQuery = "";
        SetResults(Array.Empty<ProductHit>());
        SetFieldError(nameof(ScanBoxText), null);
    }

    private void NotifyCartChanged()
    {
        OnPropertyChanged(nameof(SubtotalText));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(DiscountTotal));
        OnPropertyChanged(nameof(DiscountText));
        OnPropertyChanged(nameof(HasCartItems));
        OnPropertyChanged(nameof(CartEmptyText));
        OnPropertyChanged(nameof(CartSummaryText));
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

        if (ApplyDiscountCommand is RelayCommand discount)
        {
            discount.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// The shared FriendlyError mapper (error-copy conventions): the Sales
    /// screen and the BG-007 corrections panel render the same corrective
    /// copy for the same domain code (internal: the corrections dialog
    /// reuses this helper instead of carrying a divergent copy).
    /// </summary>
    internal static string FriendlyError(string code) =>
        code switch
        {
            "AUTH_ACTOR_NOT_SIGNED_IN" => "You are not signed in.",
            "AUTH_ACTOR_DEACTIVATED" => "This account is deactivated. Ask an admin.",
            "AUTH_ACTOR_UNKNOWN" => "Sign in again and retry.",
            "NO_PEDDLER_LINK" => "This account is not linked to a peddler. Ask an admin.",
            "NO_OPEN_TRIP" => "There is no open trip - open one from Stock.",
            "TRIP_PEDDLER_MISMATCH" => "That record belongs to another peddler.",
            "STOCK_INSUFFICIENT" => "Not enough stock on hand for that quantity.",
            "BANK_REFERENCE_REQUIRED" => "A bank transfer needs a reference number.",
            "PAYMENT_TOTAL" => "Payments do not equal the sale total.",
            "CHANGE_MISMATCH" => "Change applies to one cash line only - check the tender.",
            "DISCOUNT_NOT_ALLOWED" => "This product is not eligible for that discount.",
            "DISCOUNT_TYPE_MISMATCH" => "Discount and product disagree - re-check.",
            "SALE_EMPTY" => "Add at least one item before checkout.",
            "SUBMIT_IN_PROGRESS" => "The sale is being saved - try again in a moment.",
            "LINE_UNKNOWN_PRODUCT" => "A product is no longer in the catalog. Ask an admin to re-import it.",
            "SESSION_CLOSED" => "This sale is already finished - scan to start a new one.",
            // BG-007 corrections (the 007b builders' rejection codes).
            "VOID_APPROVAL_REQUIRED" => "A void needs an approver. Choose a supervisor or admin.",
            "ADJUSTMENT_APPROVAL_REQUIRED" => "An adjustment needs an approver. Choose a supervisor or admin.",
            "TRIP_CLOSED_CORRECTION" => "This trip is closed - corrections need an authorized reopen.",
            "TRIP_CANCELLED" => "This trip is cancelled.",
            "TRIP_UNKNOWN" => "This trip no longer exists.",
            "SALE_ALREADY_VOIDED" => "That sale is already voided.",
            "SALE_VOIDED" => "That sale is voided.",
            "VOID_RETURNED" => "A sale with returned items cannot be voided.",
            "SALE_NOT_COMMITTED" => "That sale is not committed. Refresh and retry.",
            "UNKNOWN_SALE" => "That sale no longer exists. Refresh and retry.",
            "RETURN_LIMIT" => "The return exceeds the sold quantity.",
            "RETURN_EMPTY" => "Add at least one return line.",
            "LINE_UNKNOWN" => "That line does not belong to the sale.",
            "LINE_ZERO" => "Enter a quantity of at least 1.",
            "REASON_REQUIRED" => "A reason is required.",
            "REFUND_METHOD" => "A refund cannot post to Credit.",
            "REFUND_LIMIT" => "The refund exceeds the original payment.",
            "REFUND_RECEIPT_MISSING" => "No payment is available to refund against.",
            "REFUND_NEGATIVE" => "The refund amount cannot be negative.",
            _ => "Action could not be completed. Please try again.",
        };
}

/// <summary>
/// One cart line mirroring a <see cref="SessionLine"/>: quantity, discount,
/// and totals are display-only projections (the session owns the rules).
/// </summary>
public sealed class CartLineDraft : ViewModelBase
{
    private readonly System.Action _changed;
    private int _quantity;
    private Money _unitPrice;
    private Money _discount;
    private DiscountType _discountType;
    private string _itemText = "";

    public CartLineDraft(SessionLine line, System.Action changed)
    {
        LineId = line.LineId;
        _changed = changed;
        _quantity = line.Quantity.Units;
        _unitPrice = line.UnitPrice;
        _discount = line.Discount;
        _discountType = line.DiscountType;
        _itemText = line.ProductName;
    }

    public SaleLineId LineId { get; }

    public string ItemText => _itemText;

    public int Quantity => _quantity;

    public DiscountType DiscountType => _discountType;

    public Money LineTotal => _unitPrice.Times(_quantity).Minus(_discount);

    public string DetailText => $"{ItemText}  {Quantity} x {MoneyDisplay.Peso(_unitPrice)}";

    public string LineTotalText => MoneyDisplay.Peso(LineTotal);

    public bool HasDiscount => DiscountType != DiscountType.None;

    public string DiscountLabel => DiscountType == DiscountType.Pwd ? "PWD" : "SENIOR";

    public string DiscountText => $"{DiscountLabel} -{MoneyDisplay.Peso(_discount)}";

    /// <summary>In-place refresh when the session line changes (sync keeps the
    /// DataGrid selection alive).</summary>
    internal void UpdateFrom(SessionLine line, System.Action changed)
    {
        _itemText = line.ProductName;
        _quantity = line.Quantity.Units;
        _unitPrice = line.UnitPrice;
        _discount = line.Discount;
        _discountType = line.DiscountType;
        OnPropertyChanged(nameof(ItemText));
        OnPropertyChanged(nameof(Quantity));
        OnPropertyChanged(nameof(DiscountType));
        OnPropertyChanged(nameof(LineTotal));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(LineTotalText));
        OnPropertyChanged(nameof(HasDiscount));
        OnPropertyChanged(nameof(DiscountLabel));
        OnPropertyChanged(nameof(DiscountText));
        _changed();
    }
}

/// <summary>One search-result row (display projection over a ProductHit).</summary>
public sealed record SearchResultItem(string Name, string Sku, string PriceText, string OnHandText, ProductHit Hit);
