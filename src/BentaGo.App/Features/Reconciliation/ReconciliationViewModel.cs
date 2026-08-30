using System.Collections.Generic;
using System.Globalization;
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
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Display;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Reconciliation;
using BentaGo.Core.Domain.Trips;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Features.Reconciliation;

/// <summary>
/// End trip / reconciliation (BG-008 Slice B, docs/05 449-482): the REAL
/// reconciliation screen over the merged use cases — expected cash from the
/// trip's physical-cash fold, per-product expected stock from the movement
/// fold, count entry through <see cref="ICountService"/> (Save Draft: the
/// submitted counts become facts, NO trip lock and NO final audit event),
/// Submit Close through <see cref="ICloseService"/> (DailyClose + TripClose
/// movement + close lock + the close receipt on screen), Reopen through
/// <see cref="IReopenService"/> with the corrections-panel approval policy
/// (a peddler actor picks an active Admin/Supervisor; a Supervisor/Admin
/// self-approves), and the report view rendering
/// <see cref="IReconciliationReportService"/>'s totals + variances + notes.
///
/// Every rejected use case surfaces the shared FriendlyError copy and leaves
/// the ledger untouched (the service commits nothing on failure). The trip is
/// the session actor's open trip, or their most recent one so a CLOSED trip
/// still renders its lock, close receipt, and report.
/// </summary>
public sealed class ReconciliationViewModel : ValidatableViewModelBase, IRetainableScreen
{
    public const string CountedErrorCopy = "Enter a number, or leave blank if not counted.";
    public const string DraftEmptyCopy = "Enter at least one counted quantity to save a draft.";
    public const string DraftSavedCopy = "Draft saved (count recorded)";
    public const string NoteRequiredShortCopy = "A note is required to explain a cash SHORT. Record where the difference went.";
    public const string NoteRequiredOverCopy = "A note is required to explain a cash OVER. Record where the difference came from.";
    public const string ReopenReasonRequiredCopy = "A reopen reason is required.";
    public const string ReopenApproverRequiredCopy = "A reopen needs an admin approval. Choose an admin or supervisor above.";
    public const string ClosedTripCorrectionCopy = "This trip is closed - corrections need an authorized reopen.";

    private readonly ILedgerStore _store;
    private readonly IActorContext _actors;
    private readonly IUserAccountStore _users;
    private readonly ICountService _counts;
    private readonly ICloseService _closes;
    private readonly IReopenService _reopens;
    private readonly IReconciliationReportService _reports;
    private readonly AdminReadService _reads;
    private readonly IFocusService _focus;
    private readonly Func<DateTimeOffset> _clock;

    private string _actualCashText = "";
    private string _closeNotesText = "";
    private string _reopenReasonText = "";
    private bool _isClosed;
    private bool _hasTrip;
    private string? _fatalMessage;
    private string? _errorBanner;
    private string _tripNumberText = "-";
    private string _tripStatusText = "-";
    private IReadOnlyList<MethodSummary> _methodRows = Array.Empty<MethodSummary>();
    private string _grossSalesText = MoneyDisplay.Peso(Money.Zero);
    private string _discountsText = MoneyDisplay.Peso(Money.Zero);
    private string _netSalesText = MoneyDisplay.Peso(Money.Zero);
    private string _expectedCashText = MoneyDisplay.Peso(Money.Zero);
    private IReadOnlyList<ReconciliationCountRow> _countRows = Array.Empty<ReconciliationCountRow>();
    private IReadOnlyList<ApproverOption> _approvers = Array.Empty<ApproverOption>();
    private ApproverOption? _selectedApprover;
    private bool _isSelfApproval;
    private CloseReceiptView? _closeReceipt;
    private string? _reopenSummaryText;
    private ReconciliationReportView? _report;

    private User _actor = null!;
    private PeddlerId _peddler;
    private Trip _trip = null!;
    private IReadOnlyList<User> _accounts = Array.Empty<User>();

    public ReconciliationViewModel(IServiceProvider services)
    {
        _store = services.GetRequiredService<ILedgerStore>();
        _actors = services.GetRequiredService<IActorContext>();
        _users = services.GetRequiredService<IUserAccountStore>();
        _counts = services.GetRequiredService<ICountService>();
        _closes = services.GetRequiredService<ICloseService>();
        _reopens = services.GetRequiredService<IReopenService>();
        _reports = services.GetRequiredService<IReconciliationReportService>();
        _reads = services.GetRequiredService<AdminReadService>();
        _focus = services.GetRequiredService<IFocusService>();
        _clock = services.GetRequiredService<Func<DateTimeOffset>>();

        SaveDraftCommand = new RelayCommand(_ => SaveDraft(), _ => CanSaveDraft);
        SubmitCloseCommand = new RelayCommand(_ => SubmitClose(), _ => CanSubmitClose);
        ReopenCommand = new RelayCommand(_ => Reopen(), _ => CanReopen);
        CancelEditCommand = new RelayCommand(_ => CancelEdit());
        PrintSummaryCommand = new RelayCommand(_ => { }, _ => false); // BG-008 Slice C (Ctrl+P parked)
        FocusCashCommand = new RelayCommand(_ => _focus.Request(AutomationIds.ReconciliationActualCashBox));
    }

    /// <summary>MainViewModel calls this when the screen is shown: the real
    /// read models (fold, counts, report, approvers) rehydrate on every
    /// activation — the screen's refresh is the navigation hook (the shell's
    /// F5 catalog entry is Future, so no key binding is added).</summary>
    public async Task OnActivatedAsync()
    {
        FatalMessage = null;
        ErrorBanner = null;

        Result<User> actor = await ActorGuard.ResolveAsync(_actors, _users);
        if (actor.IsFailure)
        {
            HasTrip = false;
            FatalMessage = SalesViewModel.FriendlyError(actor.Errors[0].Code);
            return;
        }

        _actor = actor.Value;
        if (_actor.PeddlerLink is not { } peddler)
        {
            HasTrip = false;
            FatalMessage = SalesViewModel.FriendlyError("NO_PEDDLER_LINK");
            return;
        }

        _peddler = peddler;
        _store.Bind();

        // Trip context (the corrections-panel rule): the open trip when one
        // exists, otherwise the peddler's most recent one — a CLOSED trip must
        // still render its lock, close receipt, and report.
        Trip? trip = null;
        foreach (Trip candidate in _store.Context.Trips)
        {
            if (candidate.Peddler != _peddler)
            {
                continue;
            }

            if (_store.State.Trip(candidate.Id).Status == TripStatus.Open)
            {
                trip = candidate;
                break;
            }

            if (trip is null || candidate.CreatedUtc > trip.CreatedUtc)
            {
                trip = candidate;
            }
        }

        if (trip is null)
        {
            HasTrip = false;
            FatalMessage = SalesViewModel.FriendlyError("NO_OPEN_TRIP");
            return;
        }

        _trip = trip;
        HasTrip = true;
        IsClosed = _store.State.Trip(_trip.Id).Status == TripStatus.Closed;

        IReadOnlyList<User> accounts = await _reads.ListUsersAsync();
        _accounts = accounts;
        _approvers = accounts
            .Where(u => u.DeactivatedUtc is null && u.Role is Role.Admin or Role.Supervisor)
            .Select(u => new ApproverOption(u.Id, u.Username, $"{u.Role}"))
            .ToArray();
        IsSelfApproval = _actor.Role is Role.Admin or Role.Supervisor;

        RebuildTripDisplay();
        RebuildCountRows();
        RefreshCloseReceipt();
        RefreshReport();
    }

    public ICommand SaveDraftCommand { get; }

    public ICommand SubmitCloseCommand { get; }

    public ICommand ReopenCommand { get; }

    public ICommand CancelEditCommand { get; }

    public ICommand PrintSummaryCommand { get; }

    public ICommand FocusCashCommand { get; }

    public string TripNumberText => _tripNumberText;

    public string TripStatusText => _tripStatusText;

    public IReadOnlyList<MethodSummary> MethodRows => _methodRows;

    public string GrossSalesText => _grossSalesText;

    public string DiscountsText => _discountsText;

    public string NetSalesText => _netSalesText;

    public string ExpectedCashText => _expectedCashText;

    public IReadOnlyList<ReconciliationCountRow> CountRows => _countRows;

    public IReadOnlyList<ApproverOption> Approvers => _approvers;

    public ApproverOption? SelectedApprover
    {
        get => _selectedApprover;
        set => SetProperty(ref _selectedApprover, value);
    }

    /// <summary>Supervisor/Admin actors self-approve the reopen; a peddler
    /// actor must pick an approver (the corrections-panel policy).</summary>
    public bool IsSelfApproval
    {
        get => _isSelfApproval;
        private set
        {
            if (SetProperty(ref _isSelfApproval, value))
            {
                OnPropertyChanged(nameof(NeedsApproverPicker));
                OnPropertyChanged(nameof(SelfApprovalText));
            }
        }
    }

    public bool NeedsApproverPicker => HasTrip && !_isSelfApproval;

    public string? SelfApprovalText => _isSelfApproval
        ? $"Self-approved ({_actor.Username})"
        : null;

    public string ActualCashText
    {
        get => _actualCashText;
        set
        {
            if (SetProperty(ref _actualCashText, value))
            {
                OnPropertyChanged(nameof(BannerText));
                OnPropertyChanged(nameof(BannerKind));
                OnPropertyChanged(nameof(IsBalanced));
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(IsShort));
                OnPropertyChanged(nameof(IsOver));
                NotifySubmitGate();
            }
        }
    }

    public string CloseNotesText
    {
        get => _closeNotesText;
        set => SetProperty(ref _closeNotesText, value);
    }

    public string ReopenReasonText
    {
        get => _reopenReasonText;
        set => SetProperty(ref _reopenReasonText, value);
    }

    /// <summary>Field error with corrective copy (docs/03 §7 wording).</summary>
    public void SetCountedError() => SetFieldError(nameof(ActualCashText), CountedErrorCopy);

    /// <summary>True when the screen has a resolvable trip; the copy is in
    /// FatalMessage otherwise (signed out, no peddler link, no trip).</summary>
    public bool HasTrip
    {
        get => _hasTrip;
        private set
        {
            if (SetProperty(ref _hasTrip, value))
            {
                OnPropertyChanged(nameof(NeedsApproverPicker));
                NotifySubmitGate();
                NotifySaveGate();
                NotifyReopenGate();
            }
        }
    }

    public bool IsClosed
    {
        get => _isClosed;
        private set
        {
            if (SetProperty(ref _isClosed, value))
            {
                OnPropertyChanged(nameof(CorrectionGateText));
                OnPropertyChanged(nameof(CanSubmitClose));
                NotifySaveGate();
                NotifyReopenGate();
            }
        }
    }

    /// <summary>The shared TRIP_CLOSED_CORRECTION copy from the Sales mapper:
    /// a closed trip gates corrections until an authorized reopen.</summary>
    public string? CorrectionGateText => IsClosed ? ClosedTripCorrectionCopy : null;

    public bool DraftSaved => HasTrip && _store.Context.Counts.Any(
        c => c.Location == _trip.Location && c.WhenUtc >= _trip.CreatedUtc);

    public string DraftStatusText => DraftSaved
        ? DraftSavedCopy
        : "Draft not saved";

    /// <summary>Gate: a resolvable open trip (draft counts never lock).</summary>
    public bool CanSaveDraft => HasTrip && !IsClosed;

    /// <summary>Gate: a parseable counted cash on an open trip.</summary>
    public bool CanSubmitClose => HasTrip && !IsClosed && TryParseAmount(ActualCashText, out _);

    /// <summary>Gate: a closed trip (reopen is the authorized exit).</summary>
    public bool CanReopen => HasTrip && IsClosed;

    /// <summary>The screen-level corrective banner (submit/reopen rejections).
    /// The use case commits nothing on failure, so the banner is the only
    /// effect a rejected action has.</summary>
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

    public bool HasFatalState => !string.IsNullOrEmpty(FatalMessage);

    /// <summary>The close receipt (rendered when a close document exists;
    /// it REMAINS visible after a reopen — the prior close is a fact).</summary>
    public CloseReceiptView? CloseReceipt => _closeReceipt;

    public bool HasCloseReceipt => _closeReceipt is not null;

    public string? ReopenSummaryText
    {
        get => _reopenSummaryText;
        private set => SetProperty(ref _reopenSummaryText, value);
    }

    /// <summary>The reconciliation report view model (totals + variances +
    /// notes) built by the Slice A projection service.</summary>
    public ReconciliationReportView? Report => _report;

    public bool HasReport => _report is not null;

    /// <summary>Balanced | Short | Over  - text + glyph + color (never color-only).</summary>
    public string BannerKind
    {
        get
        {
            if (!HasTrip || !TryParseAmount(ActualCashText, out _))
            {
                return "Pending";
            }

            return CashVarianceLabel() switch
            {
                VarianceLabel.Shortage => "Short",
                VarianceLabel.Overage => "Over",
                _ => "Balanced",
            };
        }
    }

    public string BannerText
    {
        get
        {
            if (!HasTrip)
            {
                return "NO TRIP TO RECONCILE";
            }

            if (string.IsNullOrWhiteSpace(ActualCashText))
            {
                return "ENTER CASH TO RECONCILE";
            }

            if (!TryParseAmount(ActualCashText, out _))
            {
                return "CHECK CASH AMOUNT";
            }

            return CashVarianceLabel() switch
            {
                // The banner speaks the operator's side: a SHORT names the
                // missing amount (expected minus counted), an OVER the extra.
                VarianceLabel.Shortage => $"SHORT {MoneyDisplay.Peso(ExpectedCash.Minus(CountedCash()))}",
                VarianceLabel.Overage => $"OVER {MoneyDisplay.Peso(CountedCash().Minus(ExpectedCash))}",
                _ => "BALANCED",
            };
        }
    }

    // Non-color banner channels for the view (each renders text + glyph + tint).
    public bool IsPending => BannerKind == "Pending";
    public bool IsBalanced => BannerKind == "Balanced";
    public bool IsShort => BannerKind == "Short";
    public bool IsOver => BannerKind == "Over";

    /// <summary>The cash variance verdict for the entered count: counted less
    /// than expected is a SHORTAGE, more is an OVERAGE (the report's sign
    /// convention: expected minus counted, positive = short).</summary>
    internal VarianceLabel CashVarianceLabel()
    {
        Money variance = ExpectedCash.Minus(CountedCash());
        return variance.Centavos switch
        {
            > 0 => VarianceLabel.Shortage,
            < 0 => VarianceLabel.Overage,
            _ => VarianceLabel.Balanced,
        };
    }

    private Money ExpectedCash => _store.State.TripCash(_trip.Id, Fund.PhysicalCash);

    private Money CountedCash() =>
        TryParseAmount(ActualCashText, out Money counted) ? counted : Money.Zero;

    private void SaveDraft()
    {
        if (!CanSaveDraft)
        {
            ErrorBanner = ClosedTripCorrectionCopy;
            return;
        }

        ErrorBanner = null;
        List<CountLineCommand> lines = new();
        foreach (ReconciliationCountRow row in _countRows)
        {
            if (row.ParsedCounted is { } counted)
            {
                lines.Add(new CountLineCommand(
                    row.Product, Quantity.Of(counted), NullIfBlank(row.Notes)));
            }
        }

        if (lines.Count == 0)
        {
            ErrorBanner = DraftEmptyCopy;
            return;
        }

        Result<CountOutcome> outcome = _counts.Count(new CountCommand(
            new CountId(Guid.NewGuid()), _trip.Location, lines, _actor.Id, _clock()));
        if (outcome.IsFailure)
        {
            ErrorBanner = SalesViewModel.FriendlyError(outcome.Errors[0].Code);
            return;
        }

        // The use case's last Bind ran BEFORE its Append commit, so the new
        // documents are visible only after the next Bind — rebind before any
        // read model rebuild (count, receipt, report).
        _store.Bind();

        // The counts are now ledger facts (observation movements + a COUNT
        // audit); the trip stays open and no final audit event exists.
        RebuildCountRows();
        RefreshReport();
        OnPropertyChanged(nameof(DraftStatusText));
        OnPropertyChanged(nameof(DraftSaved));
    }

    private void SubmitClose()
    {
        if (!HasTrip)
        {
            ErrorBanner = FatalMessage;
            return;
        }

        if (!TryParseAmount(ActualCashText, out _))
        {
            SetCountedError();
            return;
        }

        ErrorBanner = null;

        // A cash difference must be explained (docs/05 474-475: amount, sign,
        // label, required note) — balanced closes need no note.
        string? noteError = CashVarianceLabel() switch
        {
            VarianceLabel.Shortage when string.IsNullOrWhiteSpace(CloseNotesText) => NoteRequiredShortCopy,
            VarianceLabel.Overage when string.IsNullOrWhiteSpace(CloseNotesText) => NoteRequiredOverCopy,
            _ => null,
        };
        if (noteError is not null)
        {
            ErrorBanner = noteError;
            return;
        }

        Result<CloseOutcome> outcome = _closes.Close(new CloseCommand(
            new CloseId(Guid.NewGuid()),
            _trip.Id,
            CountedCash(),
            NullIfBlank(CloseNotesText),
            _actor.Id,
            _clock()));
        if (outcome.IsFailure)
        {
            ErrorBanner = SalesViewModel.FriendlyError(outcome.Errors[0].Code);
            return;
        }

        // Rebind so the close document is visible before the read models
        // rebuild (the use case's last Bind ran before its Append commit).
        _store.Bind();

        IsClosed = true;
        RebuildTripDisplay();
        RebuildCountRows();
        RefreshCloseReceipt();
        RefreshReport();
    }

    private void Reopen()
    {
        if (!CanReopen)
        {
            return;
        }

        ErrorBanner = null;
        if (string.IsNullOrWhiteSpace(ReopenReasonText))
        {
            ErrorBanner = ReopenReasonRequiredCopy;
            return;
        }

        if (!IsSelfApproval && SelectedApprover is null)
        {
            ErrorBanner = ReopenApproverRequiredCopy;
            return;
        }

        // The approver is recorded with the reason (the reopen command has no
        // approval field): the audit then carries reason + approving actor.
        string reason = IsSelfApproval
            ? ReopenReasonText.Trim()
            : $"{ReopenReasonText.Trim()} (approved by {SelectedApprover!.Username})";

        Result<AppendReceipt> outcome = _reopens.Reopen(new ReopenCommand(
            _trip.Id, reason, _actor.Id, _clock()));
        if (outcome.IsFailure)
        {
            ErrorBanner = SalesViewModel.FriendlyError(outcome.Errors[0].Code);
            return;
        }

        // Rebind so the reopen movement is visible before the read models
        // rebuild (the use case's last Bind ran before its Append commit).
        _store.Bind();

        IsClosed = false;
        RebuildTripDisplay();
        RebuildCountRows();
        RefreshCloseReceipt();
        RefreshReport();
        ReopenSummaryText = $"Reopened by {_actor.Username} — {reason}";
    }

    private void CancelEdit()
    {
        SetFieldError(nameof(ActualCashText), null);
        ErrorBanner = null;
    }

    private void RebuildTripDisplay()
    {
        List<Trip> trips = _store.Context.Trips
            .Where(t => t.Peddler == _peddler)
            .OrderBy(t => t.CreatedUtc).ThenBy(t => t.Id.Value)
            .ToList();
        int index = trips.FindIndex(t => t.Id == _trip.Id);
        _tripNumberText = $"TRIP-{_trip.CreatedUtc:yyyyMMdd}-{(index < 0 ? 1 : index + 1):000}";
        _tripStatusText = _store.State.Trip(_trip.Id).Status == TripStatus.Closed
            ? "CLOSED"
            : "OPEN";
        OnPropertyChanged(nameof(TripNumberText));
        OnPropertyChanged(nameof(TripStatusText));

        // Per-method summary over the trip's own funds (the fold is the
        // computation source; credit produces no fund and shows as zero).
        _methodRows = new List<MethodSummary>
        {
            new("Cash", MoneyDisplay.Peso(_store.State.TripCash(_trip.Id, Fund.PhysicalCash))),
            new("GCash", MoneyDisplay.Peso(_store.State.TripCash(_trip.Id, Fund.Gcash))),
            new("Maya", MoneyDisplay.Peso(_store.State.TripCash(_trip.Id, Fund.Maya))),
            new("Bank", MoneyDisplay.Peso(_store.State.TripCash(_trip.Id, Fund.Bank))),
        };

        Money gross = Money.Zero;
        Money discounts = Money.Zero;
        foreach (Sale sale in _store.Context.Sales)
        {
            if (sale.Trip != _trip.Id || _store.State.Sale(sale.Id).Status == SaleStatus.Voided)
            {
                continue;
            }

            gross = gross.Plus(sale.Total);
            foreach (SaleLine line in sale.Lines)
            {
                discounts = discounts.Plus(line.Discount);
            }
        }

        _grossSalesText = MoneyDisplay.Peso(gross);
        _discountsText = MoneyDisplay.Peso(discounts);
        _netSalesText = MoneyDisplay.Peso(gross.Minus(discounts));
        _expectedCashText = MoneyDisplay.Peso(ExpectedCash);
        OnPropertyChanged(nameof(MethodRows));
        OnPropertyChanged(nameof(GrossSalesText));
        OnPropertyChanged(nameof(DiscountsText));
        OnPropertyChanged(nameof(NetSalesText));
        OnPropertyChanged(nameof(ExpectedCashText));
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(BannerKind));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsBalanced));
        OnPropertyChanged(nameof(IsShort));
        OnPropertyChanged(nameof(IsOver));
    }

    /// <summary>One row per product the trip touched: expected from the fold's
    /// ending units, counted from the fold's latest observation (hydrating a
    /// saved draft), signed quantity + value variance (counted minus expected,
    /// a negative is a shortage) at the fold-derived unit price.</summary>
    private void RebuildCountRows()
    {
        var products = new List<(ProductId Id, string Sku, string Name)>();
        var seen = new HashSet<ProductId>();
        foreach (StockLoad load in _store.Context.Loads)
        {
            if (load.Trip != _trip.Id)
            {
                continue;
            }

            foreach (StockLoadLine line in load.Lines)
            {
                if (seen.Add(line.ProductId))
                {
                    products.Add((line.ProductId, line.Sku, line.ProductName));
                }
            }
        }

        foreach (Sale sale in _store.Context.Sales)
        {
            if (sale.Trip != _trip.Id)
            {
                continue;
            }

            foreach (SaleLine line in sale.Lines)
            {
                if (seen.Add(line.ProductId))
                {
                    products.Add((line.ProductId, line.Sku, line.ProductName));
                }
            }
        }

        var rows = new List<ReconciliationCountRow>(products.Count);
        foreach ((ProductId id, string sku, string name) in products.OrderBy(p => p.Sku, StringComparer.Ordinal))
        {
            InventoryBreakdown breakdown = _store.State.Breakdown(id, _trip.Location);
            string? notes = LatestCountNotes(id);
            rows.Add(new ReconciliationCountRow(
                id,
                sku,
                name,
                (int)breakdown.Ending,
                breakdown.Observed?.Units,
                DeriveUnitPrice(id),
                notes));
        }

        _countRows = rows;
        OnPropertyChanged(nameof(CountRows));
    }

    private string? LatestCountNotes(ProductId product)
    {
        CountLine? match = null;
        foreach (PhysicalCount count in _store.Context.Counts
                     .Where(c => c.Location == _trip.Location)
                     .OrderBy(c => c.WhenUtc).ThenBy(c => c.Id.Value))
        {
            foreach (CountLine line in count.Lines)
            {
                if (line.ProductId == product)
                {
                    match = line;
                }
            }
        }

        return match?.Notes;
    }

    /// <summary>The fold-derived unit price (the report's derivation, display
    /// twin): committed sale value over sold quantity, else the load unit
    /// cost, else zero — so the screen value agrees with the report value.</summary>
    private Money DeriveUnitPrice(ProductId product)
    {
        long value = 0;
        int quantity = 0;
        foreach (Sale sale in _store.Context.Sales)
        {
            if (sale.Trip != _trip.Id || _store.State.Sale(sale.Id).Status == SaleStatus.Voided)
            {
                continue;
            }

            foreach (SaleLine line in sale.Lines)
            {
                if (line.ProductId == product)
                {
                    value += line.LineTotal.Centavos;
                    quantity += line.Quantity.Units;
                }
            }
        }

        if (quantity > 0)
        {
            return new Money(value / quantity);
        }

        Money? cost = null;
        foreach (StockLoad load in _store.Context.Loads)
        {
            if (load.Trip != _trip.Id)
            {
                continue;
            }

            foreach (StockLoadLine line in load.Lines)
            {
                if (line.ProductId == product)
                {
                    cost = line.UnitCost;
                }
            }
        }

        return cost ?? Money.Zero;
    }

    /// <summary>The close receipt from the latest close document — rebuilt
    /// from the ledger on every activation, so it persists across restarts
    /// and REMAINS visible after a reopen.</summary>
    private void RefreshCloseReceipt()
    {
        DailyClose? close = _store.Context.Closes
            .Where(c => c.Trip == _trip.Id)
            .OrderBy(c => c.WhenUtc).ThenBy(c => c.Id.Value)
            .LastOrDefault();
        _closeReceipt = close is null
            ? null
            : new CloseReceiptView(
                MoneyDisplay.Peso(close.ExpectedCash),
                MoneyDisplay.Peso(close.CountedCash),
                // The close document's own sign (expected minus counted, a
                // positive is a shortage) — the label carries the direction.
                MoneyDisplay.Peso(close.Variance),
                LabelOf(close.Variance),
                close.Notes,
                _accounts.FirstOrDefault(u => u.Id == close.By)?.Username ?? "user",
                ManilaDisplay.UtcAsManila(close.WhenUtc).ToString("dd MMM yyyy HH:mm"));
        OnPropertyChanged(nameof(CloseReceipt));
        OnPropertyChanged(nameof(HasCloseReceipt));
    }

    /// <summary>The report view: the Slice A projection rendered as display
    /// strings. Rebuilt after every count/close/reopen and on activation.</summary>
    private void RefreshReport()
    {
        Result<ReconciliationReport> report = _reports.Build(_peddler, _trip.Id);
        if (report.IsFailure)
        {
            _report = null;
            OnPropertyChanged(nameof(Report));
            OnPropertyChanged(nameof(HasReport));
            return;
        }

        ReconciliationReport value = report.Value;
        var stockRows = value.StockRows
            .Select(r => new ReportStockRowView(
                r.Sku,
                r.ProductName,
                r.ExpectedUnits.ToString(CultureInfo.InvariantCulture),
                r.CountedUnits?.ToString(CultureInfo.InvariantCulture) ?? "—",
                r.QuantityVariance is { } qty
                    ? (qty > 0 ? "+" : string.Empty) + qty.ToString(CultureInfo.InvariantCulture)
                    : "—",
                r.ValueVariance is { } variance ? MoneyDisplay.Peso(variance) : "—",
                r.Notes ?? "—"))
            .ToList();

        _report = new ReconciliationReportView(
            value.Totals.Sales.ToString(CultureInfo.InvariantCulture),
            MoneyDisplay.Peso(value.Totals.SalesTotal),
            value.Totals.Voided.ToString(CultureInfo.InvariantCulture),
            value.Totals.Returns.ToString(CultureInfo.InvariantCulture),
            MoneyDisplay.Peso(value.Totals.RefundTotal),
            value.Totals.Adjustments.ToString(CultureInfo.InvariantCulture),
            MoneyDisplay.Peso(value.Totals.ExpectedCash),
            MoneyDisplay.Peso(value.Totals.CountedCash),
            MoneyDisplay.Peso(value.Totals.CashVariance),
            LabelOf(value.Totals.CashLabel),
            value.CloseNotes ?? "—",
            stockRows);
        OnPropertyChanged(nameof(Report));
        OnPropertyChanged(nameof(HasReport));
    }

    /// <summary>The display label for a signed cash variance (expected minus
    /// counted): positive is a SHORTAGE, negative an OVERAGE.</summary>
    internal static string LabelOf(Money expectedMinusCounted) => expectedMinusCounted.Centavos switch
    {
        > 0 => "SHORTAGE",
        < 0 => "OVERAGE",
        _ => "BALANCED",
    };

    /// <summary>The display label for the report's enum verdict.</summary>
    internal static string LabelOf(VarianceLabel label) => label switch
    {
        VarianceLabel.Shortage => "SHORTAGE",
        VarianceLabel.Overage => "OVERAGE",
        _ => "BALANCED",
    };

    private static string? NullIfBlank(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    internal static bool TryParseAmount(string? text, out Money amount)
    {
        amount = Money.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal pesos) || pesos < 0)
        {
            return false;
        }

        try
        {
            amount = Money.FromPesos(pesos);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void NotifySubmitGate()
    {
        if (SubmitCloseCommand is RelayCommand submit)
        {
            submit.NotifyCanExecuteChanged();
        }

        OnPropertyChanged(nameof(CanSubmitClose));
    }

    private void NotifySaveGate()
    {
        if (SaveDraftCommand is RelayCommand save)
        {
            save.NotifyCanExecuteChanged();
        }

        OnPropertyChanged(nameof(CanSaveDraft));
        OnPropertyChanged(nameof(DraftSaved));
        OnPropertyChanged(nameof(DraftStatusText));
    }

    private void NotifyReopenGate()
    {
        if (ReopenCommand is RelayCommand reopen)
        {
            reopen.NotifyCanExecuteChanged();
        }

        OnPropertyChanged(nameof(CanReopen));
    }
}

/// <summary>
/// One editable row of the inventory count table (docs/03 page 9): expected
/// units from the fold, the counted entry (blank = not counted), the signed
/// quantity variance (counted minus expected; a negative is a shortage), the
/// value variance at the fold-derived unit price, and the explanatory note.
/// </summary>
public sealed class ReconciliationCountRow : ViewModelBase
{
    private string _countedText;
    private string _notes;

    public ReconciliationCountRow(
        ProductId product,
        string sku,
        string productName,
        int expectedUnits,
        int? countedUnits,
        Money unitPrice,
        string? notes)
    {
        Product = product;
        Sku = sku;
        ProductName = productName;
        ExpectedUnits = expectedUnits;
        UnitPrice = unitPrice;
        _countedText = countedUnits?.ToString(CultureInfo.InvariantCulture) ?? "";
        _notes = notes ?? "";
        UpdateVariance();
    }

    public ProductId Product { get; }

    public string Sku { get; }

    public string ProductName { get; }

    public int ExpectedUnits { get; }

    public Money UnitPrice { get; }

    public string ExpectedText => ExpectedUnits.ToString(CultureInfo.InvariantCulture);

    /// <summary>The counted entry; blank (or non-numeric) means not counted.</summary>
    public string CountedText
    {
        get => _countedText;
        set
        {
            if (SetProperty(ref _countedText, value))
            {
                UpdateVariance();
            }
        }
    }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    public int? ParsedCounted { get; private set; }

    public string QuantityVarianceText { get; private set; } = "—";

    public string ValueVarianceText { get; private set; } = "—";

    /// <summary>SHORTAGE (fewer counted) | OVERAGE (more counted) | BALANCED
    /// | "" when not counted — never color-only.</summary>
    public string VarianceLabel { get; private set; } = "";

    private void UpdateVariance()
    {
        ParsedCounted = int.TryParse(CountedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int counted)
            && counted >= 0
            ? counted
            : null;

        if (ParsedCounted is not { } countedUnits)
        {
            QuantityVarianceText = "—";
            ValueVarianceText = "—";
            VarianceLabel = "";
        }
        else
        {
            int variance = countedUnits - ExpectedUnits;
            QuantityVarianceText = (variance > 0 ? "+" : string.Empty) + variance.ToString(CultureInfo.InvariantCulture);
            ValueVarianceText = MoneyDisplay.Peso(UnitPrice.Times(variance));
            VarianceLabel = variance switch
            {
                < 0 => "SHORTAGE",
                > 0 => "OVERAGE",
                _ => "BALANCED",
            };
        }

        OnPropertyChanged(nameof(ParsedCounted));
        OnPropertyChanged(nameof(QuantityVarianceText));
        OnPropertyChanged(nameof(ValueVarianceText));
        OnPropertyChanged(nameof(VarianceLabel));
    }
}

/// <summary>The close receipt the screen renders (display strings only).</summary>
public sealed record CloseReceiptView(
    string ExpectedCashText,
    string CountedCashText,
    string VarianceText,
    string VarianceLabelText,
    string? Notes,
    string ByText,
    string WhenText);

/// <summary>One approver candidate (active Admin/Supervisor account).</summary>
public sealed record ApproverOption(UserId Id, string Username, string RoleLabel)
{
    public string Label => $"{Username} ({RoleLabel})";
}

/// <summary>The reconciliation report as display strings: header totals, the
/// cash verdict, and one row per product with the count's note.</summary>
public sealed record ReconciliationReportView(
    string SalesCount,
    string SalesTotalText,
    string VoidedCount,
    string ReturnsCount,
    string RefundTotalText,
    string AdjustmentsCount,
    string ExpectedCashText,
    string CountedCashText,
    string CashVarianceText,
    string CashLabelText,
    string CloseNotesText,
    IReadOnlyList<ReportStockRowView> StockRows);

/// <summary>One report stock line: expected / counted / qty variance / value.</summary>
public sealed record ReportStockRowView(
    string Sku,
    string Product,
    string ExpectedText,
    string CountedText,
    string QuantityVarianceText,
    string ValueVarianceText,
    string NotesText);
