using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using BentaGo.App.Data;
using BentaGo.App.Features.Sales;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Catalog;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Display;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Dialogs.Corrections;

/// <summary>What a committed correction reports back to the opening screen.</summary>
public sealed record CorrectionsDialogResult(string Kind, string SummaryText);

/// <summary>
/// BG-007 corrections panel (F8, docs/05 BG-007 lanes 6-10): void a sale from
/// today's trading day, return a line (store stock or another location),
/// record damage/loss, or adjust stock up/down. Every write goes through the
/// 007b use-case services (<see cref="IVoidService"/>, <see cref="IReturnService"/>,
/// <see cref="IAdjustmentService"/>) — the builders own every rule, including
/// the approval gate (VOID_APPROVAL_REQUIRED / ADJUSTMENT_APPROVAL_REQUIRED),
/// the closed-day gate (DAY_CLOSED_CORRECTION), and stock sufficiency.
///
/// Approval policy (the app half of BG-007): a Supervisor/Admin actor
/// self-approves (ApprovedBy = own id); other actors MUST choose an approver
/// from the active Admin/Supervisor accounts. ApprovedBy is therefore always
/// non-null on the commands this panel builds. A rejected builder result is
/// surfaced as corrective copy through the FriendlyError helper pattern and
/// leaves the ledger untouched (the use case commits nothing on failure).
/// </summary>
public sealed class CorrectionsDialogViewModel : DialogViewModelBase<CorrectionsDialogResult>
{
    public const string ModeVoid = "VOID";
    public const string ModeReturn = "RETURN";
    public const string ModeDamageLoss = "DAMAGE/LOSS";
    public const string ModeAdjustment = "ADJUSTMENT";

    /// <summary>The DialogHost caption for this panel.</summary>
    public override string Title => "Corrections";

    private readonly ILedgerStore _store;
    private readonly IActorContext _actors;
    private readonly IUserAccountStore _users;
    private readonly AdminReadService _reads;
    private readonly ISaleQueryService _saleQueries;
    private readonly IProductStore _catalog;
    private readonly IVoidService _voids;
    private readonly IReturnService _returns;
    private readonly IAdjustmentService _adjustments;
    private readonly IFocusService _focus;
    private readonly Func<DateTimeOffset> _clock;

    private string? _fatalMessage;
    private string _selectedModeId = ModeVoid;
    private IReadOnlyList<SaleOption> _sales = Array.Empty<SaleOption>();
    private SaleOption? _selectedSale;
    private IReadOnlyList<ReturnLineOption> _saleLines = Array.Empty<ReturnLineOption>();
    private ReturnLineOption? _selectedLine;
    private IReadOnlyList<AdjustmentProductOption> _products = Array.Empty<AdjustmentProductOption>();
    private AdjustmentProductOption? _selectedProduct;
    private IReadOnlyList<ApproverOption> _approvers = Array.Empty<ApproverOption>();
    private ApproverOption? _selectedApprover;
    private IReadOnlyList<DestinationOption> _destinations = Array.Empty<DestinationOption>();
    private DestinationOption? _selectedDestination;
    private IReadOnlyList<KindOption> _kinds = Array.Empty<KindOption>();
    private KindOption? _selectedKind;
    private string _quantityText = "1";
    private string _reason = "";
    private string? _errorBanner;
    private string? _refundText;

    private UserId _actorId;
    private DayKey _day;

    public CorrectionsDialogViewModel(IServiceProvider services)
    {
        _store = services.GetRequiredService<ILedgerStore>();
        _actors = services.GetRequiredService<IActorContext>();
        _users = services.GetRequiredService<IUserAccountStore>();
        _reads = services.GetRequiredService<AdminReadService>();
        _saleQueries = services.GetRequiredService<ISaleQueryService>();
        _catalog = services.GetRequiredService<IProductStore>();
        _voids = services.GetRequiredService<IVoidService>();
        _returns = services.GetRequiredService<IReturnService>();
        _adjustments = services.GetRequiredService<IAdjustmentService>();
        _focus = services.GetRequiredService<IFocusService>();
        _clock = services.GetRequiredService<Func<DateTimeOffset>>();

        ModeOptions =
        [
            new ModeOption(ModeVoid, "VOID SALE"),
            new ModeOption(ModeReturn, "RETURN LINE"),
            new ModeOption(ModeDamageLoss, "DAMAGE / LOSS"),
            new ModeOption(ModeAdjustment, "ADJUSTMENT"),
        ];

        SubmitCommand = new RelayCommand(async _ => await SubmitAsync(), _ => !IsBusy);
        SelectModeCommand = new RelayCommand(parameter => SelectMode(((ModeOption)parameter!).Id));
        SelectKindCommand = new RelayCommand(parameter => SelectKind(((KindOption)parameter!).Kind));
        FocusReasonCommand = new RelayCommand(_ => _focus.Request(AutomationIds.CorrectionsReasonBox));
    }

    /// <summary>Resolves the session actor, today's trading day, the day's
    /// sales, the approver list, and the destination candidates. A failure
    /// leaves <see cref="FatalMessage"/> set and the panel offers no actions.</summary>
    public async Task LoadAsync()
    {
        FatalMessage = null;
        ErrorBanner = null;

        Result<User> actor = await ActorGuard.ResolveAsync(_actors, _users);
        if (actor.IsFailure)
        {
            FatalMessage = SalesViewModel.FriendlyError(actor.Errors[0].Code);
            return;
        }

        User user = actor.Value;
        _actorId = user.Id;
        IsSelfApproval = user.Role is Role.Supervisor or Role.Admin;
        SelfApprovalText = IsSelfApproval ? $"Self-approved ({user.Username})" : null;
        OnPropertyChanged(nameof(IsSelfApproval));
        OnPropertyChanged(nameof(SelfApprovalText));
        OnPropertyChanged(nameof(NeedsApproverPicker));

        _day = DayKey.FromUtc(_clock());
        _store.Bind();

        // Approver candidates: active Admin/Supervisor accounts (the
        // UserManagementViewModel read path — the account store exposes no
        // enumeration seam, so the admin read glue lists them).
        IReadOnlyList<User> accounts = await _reads.ListUsersAsync();
        _approvers = accounts
            .Where(u => u.DeactivatedUtc is null && u.Role is Role.Admin or Role.Supervisor)
            .Select(u => new ApproverOption(u.Id, $"{u.Username} ({u.Role})"))
            .ToArray();

        Result<IReadOnlyList<SaleHistoryRow>> rows = _saleQueries.Query(
            new SaleHistoryFilter(null, _day, Method: null, Status: null, Receipt: null, Reference: null));
        if (rows.IsFailure)
        {
            FatalMessage = SalesViewModel.FriendlyError(rows.Errors[0].Code);
            return;
        }

        _sales = rows.Value
            .Where(r => r.Status != SaleStatus.Voided)
            .Select(r => new SaleOption(r.Id, r.ReceiptNumber, r.Total,
                $"{r.ReceiptNumber}  ·  {MoneyDisplay.Peso(r.Total)}"))
            .ToArray();

        _destinations = BuildDestinations().ToArray();
        _products = await BuildProductOptionsAsync();
        SelectMode(SelectedModeId);
    }

    public IReadOnlyList<ModeOption> ModeOptions { get; }

    public ICommand SubmitCommand { get; }

    public ICommand SelectModeCommand { get; }

    public ICommand SelectKindCommand { get; }

    public ICommand FocusReasonCommand { get; }

    public IReadOnlyList<SaleOption> Sales => _sales;

    public IReadOnlyList<ReturnLineOption> SaleLines => _saleLines;

    public IReadOnlyList<AdjustmentProductOption> Products => _products;

    public IReadOnlyList<ApproverOption> Approvers => _approvers;

    public IReadOnlyList<DestinationOption> Destinations => _destinations;

    public IReadOnlyList<KindOption> Kinds => _kinds;

    /// <summary>True when the panel cannot offer corrections at all (signed
    /// out, no open trading day); the copy is in FatalMessage.</summary>
    public bool HasFatalState => !string.IsNullOrEmpty(FatalMessage);

    public string? FatalMessage
    {
        get => _fatalMessage;
        private set
        {
            if (SetProperty(ref _fatalMessage, value))
            {
                OnPropertyChanged(nameof(HasFatalState));
            }
        }
    }

    public string SelectedModeId
    {
        get => _selectedModeId;
        private set => SetProperty(ref _selectedModeId, value);
    }

    public bool IsVoidMode => SelectedModeId == ModeVoid;

    public bool IsReturnMode => SelectedModeId == ModeReturn;

    public bool IsStockMode => SelectedModeId is ModeDamageLoss or ModeAdjustment;

    /// <summary>The one shared SALE picker serves both the void and return
    /// forms (a single FocusTarget, never a duplicate in the tree).</summary>
    public bool IsSalePickerVisible => IsVoidMode || IsReturnMode;

    public SaleOption? SelectedSale
    {
        get => _selectedSale;
        set
        {
            if (SetProperty(ref _selectedSale, value))
            {
                ReloadSaleLines();
            }
        }
    }

    public ReturnLineOption? SelectedLine
    {
        get => _selectedLine;
        set
        {
            if (SetProperty(ref _selectedLine, value))
            {
                UpdateRefundPreview();
                NotifyGate();
            }
        }
    }

    public AdjustmentProductOption? SelectedProduct
    {
        get => _selectedProduct;
        set => SetProperty(ref _selectedProduct, value);
    }

    public ApproverOption? SelectedApprover
    {
        get => _selectedApprover;
        set => SetProperty(ref _selectedApprover, value);
    }

    public DestinationOption? SelectedDestination
    {
        get => _selectedDestination;
        set => SetProperty(ref _selectedDestination, value);
    }

    public KindOption? SelectedKind
    {
        get => _selectedKind;
        private set => SetProperty(ref _selectedKind, value);
    }

    public string QuantityText
    {
        get => _quantityText;
        set
        {
            if (SetProperty(ref _quantityText, value))
            {
                UpdateRefundPreview();
                NotifyGate();
            }
        }
    }

    public string Reason
    {
        get => _reason;
        set
        {
            if (SetProperty(ref _reason, value))
            {
                NotifyGate();
            }
        }
    }

    /// <summary>Supervisor/Admin actors self-approve (ApprovedBy = own id);
    /// a cashier actor must choose from the approver list.</summary>
    public bool IsSelfApproval { get; private set; }

    public string? SelfApprovalText { get; private set; }

    /// <summary>The approver picker is offered to cashier actors on the
    /// approval-carrying forms (void, damage/loss, adjustment) only.</summary>
    public bool NeedsApproverPicker => !IsSelfApproval && !IsReturnMode;

    public string? ErrorBanner
    {
        get => _errorBanner;
        private set
        {
            if (SetProperty(ref _errorBanner, value))
            {
                OnPropertyChanged(nameof(HasErrorBanner));
            }
        }
    }

    public bool HasErrorBanner => !string.IsNullOrEmpty(ErrorBanner);

    /// <summary>Pro-rated refund preview for the selected return line.</summary>
    public string? RefundText
    {
        get => _refundText;
        private set => SetProperty(ref _refundText, value);
    }

    private bool IsBusy { get; set; }

    public void SelectMode(string modeId)
    {
        // Hydrate the chip flags directly: re-selecting the current mode must
        // still render it filled (the same-value property set is a no-op).
        foreach (ModeOption option in ModeOptions)
        {
            option.IsSelected = option.Id == modeId;
        }

        SelectedModeId = modeId;
        SelectedSale = null;
        SelectedProduct = null;
        ErrorBanner = null;
        OnPropertyChanged(nameof(IsVoidMode));
        OnPropertyChanged(nameof(IsReturnMode));
        OnPropertyChanged(nameof(IsStockMode));
        OnPropertyChanged(nameof(NeedsApproverPicker));

        _kinds = modeId switch
        {
            ModeDamageLoss =>
            [
                new KindOption(AdjustmentKind.Damage, "DAMAGE"),
                new KindOption(AdjustmentKind.Loss, "LOSS"),
            ],
            ModeAdjustment =>
            [
                new KindOption(AdjustmentKind.Increase, "INCREASE"),
                new KindOption(AdjustmentKind.Decrease, "DECREASE"),
            ],
            _ => [],
        };
        if (_kinds.Count > 0)
        {
            _kinds[0].IsSelected = true;
            _selectedKind = _kinds[0];
        }
        else
        {
            _selectedKind = null;
        }

        OnPropertyChanged(nameof(Kinds));
        OnPropertyChanged(nameof(SelectedKind));
        UpdateRefundPreview();
    }

    public void SelectKind(AdjustmentKind kind)
    {
        if (_kinds.FirstOrDefault(k => k.Kind == kind) is not { } option)
        {
            return;
        }

        foreach (KindOption candidate in _kinds)
        {
            candidate.IsSelected = candidate.Kind == kind;
        }

        SelectedKind = option;
    }

    private IEnumerable<DestinationOption> BuildDestinations()
    {
        // Single-store pivot: returned units land back at the selling
        // location (the warehouse) — null means the sale's own location.
        yield return new DestinationOption(null, "Store stock");
    }

    private async Task<AdjustmentProductOption[]> BuildProductOptionsAsync()
    {
        IReadOnlyList<Product> catalog = await _catalog.GetAllAsync();
        if (catalog.Count == 0)
        {
            return [];
        }

        _store.Bind();
        return catalog
            .Select(p => new AdjustmentProductOption(
                p.Id, p.Sku, $"{p.Sku}  ·  on hand {_store.State.StockOnHand(p.Id, StoreLocations.Warehouse).Units}"))
            .ToArray();
    }

    private void ReloadSaleLines()
    {
        _saleLines = [];
        _selectedLine = null;
        OnPropertyChanged(nameof(SaleLines));
        OnPropertyChanged(nameof(SelectedLine));
        UpdateRefundPreview();

        if (_selectedSale is null)
        {
            return;
        }

        Result<SaleDetail> detail = _saleQueries.GetById(_selectedSale.Id);
        if (detail.IsFailure)
        {
            return;
        }

        var lines = new List<ReturnLineOption>(detail.Value.Lines.Count);
        foreach (SaleLineDetail line in detail.Value.Lines)
        {
            int returned = _store.State.Returned(line.Id).Units;
            int remaining = line.Quantity.Units - returned;
            lines.Add(new ReturnLineOption(
                line.Id, line.Sku, line.ProductName, line.UnitPrice, line.LineTotal,
                line.Quantity.Units, returned, remaining,
                $"{line.Sku}  ·  {line.ProductName}  ·  sold {line.Quantity.Units}, returned {returned}, {remaining} left"));
        }

        _saleLines = lines;
        OnPropertyChanged(nameof(SaleLines));
    }

    private void UpdateRefundPreview()
    {
        RefundText = null;
        if (!IsReturnMode || SelectedLine is not { } line || TryReadQuantity() is not { } qty || qty <= 0)
        {
            return;
        }

        Money refund = ProRata(line.LineTotal, line.SoldUnits, qty);
        if (refund.Centavos <= 0)
        {
            return;
        }

        RefundText = $"Refund {MoneyDisplay.Peso(refund)} by {RefundMethodOf().DisplayName()}";
    }

    /// <summary>The refund follows the sale's first funded (non-credit)
    /// payment; a credit-only sale refunds nothing (movement-less return).</summary>
    private PaymentMethod RefundMethodOf()
    {
        if (_selectedSale is null)
        {
            return PaymentMethod.Cash;
        }

        Result<SaleDetail> detail = _saleQueries.GetById(_selectedSale.Id);
        foreach (SavedPaymentLine payment in detail.IsSuccess ? detail.Value.Payments : [])
        {
            if (payment.Method != PaymentMethod.Credit)
            {
                return payment.Method;
            }
        }

        return PaymentMethod.Cash;
    }

    private static Money ProRata(Money lineTotal, int soldUnits, int returnedUnits)
    {
        if (soldUnits <= 0 || returnedUnits <= 0)
        {
            return Money.Zero;
        }

        long perUnit = lineTotal.Centavos / soldUnits;
        return new Money(perUnit * returnedUnits);
    }

    private int? TryReadQuantity() =>
        int.TryParse(QuantityText, out int qty) && qty > 0 ? qty : null;

    /// <summary>Submit-time validation (the UserManagementViewModel pattern):
    /// corrective copy in the banner, NO command, NO movement on any failure.</summary>
    private void Validate()
    {
        string? message =
            IsVoidMode ? ValidateVoid() :
            IsReturnMode ? ValidateReturn() :
            ValidateStock();
        if (message is not null)
        {
            ErrorBanner = message;
        }
    }

    private string? ValidateVoid()
    {
        if (SelectedSale is null)
        {
            return "Select the sale to void from today's sales.";
        }

        if (!IsSelfApproval && SelectedApprover is null)
        {
            return "A void needs an approver. Choose a supervisor or admin above.";
        }

        return ReasonError();
    }

    private string? ValidateReturn()
    {
        if (SelectedSale is null)
        {
            return "Select the sale to return against from today's sales.";
        }

        if (SelectedLine is null)
        {
            return "Select the sale line to return.";
        }

        if (TryReadQuantity() is not { } qty)
        {
            return "Enter a quantity of at least 1.";
        }

        if (qty > SelectedLine.RemainingUnits)
        {
            return $"Only {SelectedLine.RemainingUnits} unit(s) of {SelectedLine.Sku} can still be returned.";
        }

        return ReasonError();
    }

    private string? ValidateStock()
    {
        if (SelectedProduct is null)
        {
            return "Select a product.";
        }

        if (TryReadQuantity() is null)
        {
            return "Enter a quantity of at least 1.";
        }

        if (!IsSelfApproval && SelectedApprover is null)
        {
            return "An adjustment needs an approver. Choose a supervisor or admin above.";
        }

        return ReasonError();
    }

    private string? ReasonError() =>
        string.IsNullOrWhiteSpace(Reason) ? "A reason is required." : null;

    private Task SubmitAsync()
    {
        if (IsBusy || HasFatalState)
        {
            return Task.CompletedTask;
        }

        ErrorBanner = null;
        Validate();
        if (HasErrorBanner)
        {
            return Task.CompletedTask; // message shown, nothing moved
        }

        IsBusy = true;
        try
        {
            switch (SelectedModeId)
            {
                case ModeVoid:
                    SubmitVoid();
                    break;
                case ModeReturn:
                    SubmitReturn();
                    break;
                default:
                    SubmitStockAdjustment();
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }

        return Task.CompletedTask;
    }

    private void SubmitVoid()
    {
        Result<VoidOutcome> outcome = _voids.Void(new VoidCommand(
            new VoidId(Guid.NewGuid()),
            SelectedSale!.Id,
            Reason.Trim(),
            _actorId,
            _clock(),
            ResolveApprover()));
        if (outcome.IsFailure)
        {
            ErrorBanner = SalesViewModel.FriendlyError(outcome.Errors[0].Code);
            return;
        }

        Close(new CorrectionsDialogResult(
            ModeVoid,
            $"Sale {SelectedSale.ReceiptNumber} voided. Reason: {Reason.Trim()}."));
    }

    private void SubmitReturn()
    {
        ReturnLineOption line = SelectedLine!;
        int qty = TryReadQuantity()!.Value;
        LocationId? destination = SelectedDestination?.Location;
        Money refund = ProRata(line.LineTotal, line.SoldUnits, qty);

        var command = new ReturnLineCommand(
            line.Id, Quantity.Of(qty), Reason.Trim(), RefundMethodOf(), refund, destination);
        Result<ReturnOutcome> outcome = _returns.Return(new ReturnCommand(
            new ReturnId(Guid.NewGuid()),
            SelectedSale!.Id,
            new[] { command },
            _actorId,
            _clock()));
        if (outcome.IsFailure)
        {
            ErrorBanner = SalesViewModel.FriendlyError(outcome.Errors[0].Code);
            return;
        }

        string where = destination is { } dest ? $"store {ShortLocation(dest)}" : "store stock";
        Close(new CorrectionsDialogResult(
            ModeReturn,
            $"Returned {qty} × {line.Sku} to {where}. Reason: {Reason.Trim()}."));
    }

    private void SubmitStockAdjustment()
    {
        Result<AdjustmentOutcome> outcome = _adjustments.Adjust(new AdjustmentCommand(
            new AdjustmentId(Guid.NewGuid()),
            _day,
            SelectedProduct!.Product,
            StoreLocations.Warehouse,
            SelectedKind!.Kind,
            Quantity.Of(TryReadQuantity()!.Value),
            Reason.Trim(),
            _actorId,
            _clock(),
            ResolveApprover()));
        if (outcome.IsFailure)
        {
            ErrorBanner = SalesViewModel.FriendlyError(outcome.Errors[0].Code);
            return;
        }

        Close(new CorrectionsDialogResult(
            SelectedModeId,
            $"{SelectedKind.Kind} {TryReadQuantity()!.Value} × {SelectedProduct.Sku} recorded. Reason: {Reason.Trim()}."));
    }

    /// <summary>The approval resolution the builders demand: a self-approving
    /// Supervisor/Admin fills its own id; a cashier's choice is validated at
    /// submit time, so this never returns null past Validate().</summary>
    private UserId ResolveApprover() =>
        IsSelfApproval ? _actorId : SelectedApprover!.Id;

    private static string ShortLocation(LocationId location) =>
        "LOC-" + location.Value.ToString("N")[..8].ToUpperInvariant();


    private void NotifyGate()
    {
        if (SubmitCommand is RelayCommand submit)
        {
            submit.NotifyCanExecuteChanged();
        }
    }
}

/// <summary>One action chip (VOID SALE / RETURN LINE / DAMAGE-LOSS / ADJUSTMENT).</summary>
public sealed class ModeOption : ViewModelBase
{
    private bool _isSelected;

    public ModeOption(string id, string label) => (Id, Label) = (id, label);

    public string Id { get; }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>One kind chip (Damage/Loss or Increase/Decrease).</summary>
public sealed class KindOption : ViewModelBase
{
    private bool _isSelected;

    public KindOption(AdjustmentKind kind, string label)
    {
        Kind = kind;
        Label = label;
    }

    public AdjustmentKind Kind { get; }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>One pickable sale from today's trip (non-voided).</summary>
public sealed record SaleOption(SaleId Id, string ReceiptNumber, Money Total, string Label);

/// <summary>One returnable sale line with its remaining quantity.</summary>
public sealed record ReturnLineOption(
    SaleLineId Id, string Sku, string ProductName, Money UnitPrice, Money LineTotal,
    int SoldUnits, int ReturnedUnits, int RemainingUnits, string Label);

/// <summary>One pickable product for stock adjustments (on-hand shown).</summary>
public sealed record AdjustmentProductOption(ProductId Product, string Sku, string Label);

/// <summary>One active Admin/Supervisor account able to approve.</summary>
public sealed record ApproverOption(UserId Id, string Label);

/// <summary>Where returned units land: null = the sale's location.</summary>
public sealed record DestinationOption(LocationId? Location, string Label);
