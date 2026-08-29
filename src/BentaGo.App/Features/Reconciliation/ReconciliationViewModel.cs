using System.Globalization;
using System.Windows.Input;
using BentaGo.App.Data;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using BentaGo.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Features.Reconciliation;

/// <summary>
/// End trip / reconciliation (bg-004-synthesis §3, screen 6, docs/03 page 9):
/// per-method summary, expected cash, actual cash remitted, variance banner,
/// Save Draft / Submit Close / Print Summary, visible locked state after
/// close. Gates are state flags only; BG-005 owns the close use case.
/// </summary>
public sealed class ReconciliationViewModel : ValidatableViewModelBase, IRetainableScreen
{
    public const string CountedErrorCopy = "Enter a number, or leave blank if not counted.";

    private readonly IFocusService _focus;
    private readonly ReconciliationSummary _summary;
    private string _actualCashText = "";
    private bool _isClosed;
    private bool _draftSaved;

    public ReconciliationViewModel(IServiceProvider services)
    {
        _focus = services.GetRequiredService<IFocusService>();
        AppSnapshot data = services.GetRequiredService<IAppDataSource>().Current;
        _summary = data.Summary;
        Session = data.Session;
        CountRows = data.Stock.Select(row => new CountRow(row.Sku, row.Product, row.OnHand)).ToArray();

        SaveDraftCommand = new RelayCommand(_ => SaveDraft(), _ => !IsClosed && !DraftSaved);
        SubmitCloseCommand = new RelayCommand(_ => SubmitClose(), _ => CanSubmitClose);
        CancelEditCommand = new RelayCommand(_ => CancelEdit());
        PrintSummaryCommand = new RelayCommand(_ => { }, _ => false); // BG-005 (Ctrl+P parked)
        FocusCashCommand = new RelayCommand(_ => _focus.Request(AutomationIds.ReconciliationActualCashBox));
    }

    public SessionHeader Session { get; }

    public ReconciliationSummary Summary => _summary;

    public IReadOnlyList<CountRow> CountRows { get; }

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
                OnPropertyChanged(nameof(IsShort));
                OnPropertyChanged(nameof(IsOver));
                NotifySubmitGate();
            }
        }
    }

    /// <summary>Field error with corrective copy (docs/03 §7 wording).</summary>
    public void SetCountedError() => SetFieldError(nameof(ActualCashText), CountedErrorCopy);

    public bool IsClosed
    {
        get => _isClosed;
        private set
        {
            if (SetProperty(ref _isClosed, value))
            {
                NotifySubmitGate();
                if (SaveDraftCommand is RelayCommand save)
                {
                    save.NotifyCanExecuteChanged();
                }
            }
        }
    }

    public bool DraftSaved
    {
        get => _draftSaved;
        private set
        {
            if (SetProperty(ref _draftSaved, value))
            {
                OnPropertyChanged(nameof(DraftStatusText));
                if (SaveDraftCommand is RelayCommand save)
                {
                    save.NotifyCanExecuteChanged();
                }
            }
        }
    }

    public string DraftStatusText => DraftSaved ? "Draft saved (simulated)" : "Draft not saved";

    public ICommand SaveDraftCommand { get; }

    public ICommand SubmitCloseCommand { get; }

    public ICommand CancelEditCommand { get; }

    public ICommand PrintSummaryCommand { get; }

    public ICommand FocusCashCommand { get; }

    /// <summary>Gate: a parseable counted cash and not already closed.</summary>
    public bool CanSubmitClose => !IsClosed && TryParseAmount(ActualCashText, out _);

    /// <summary>Balanced | Short | Over  - text + glyph + color (never color-only).</summary>
    public string BannerKind
    {
        get
        {
            Money variance = Variance;
            return variance.Centavos == 0 ? "Balanced" : variance.Centavos < 0 ? "Short" : "Over";
        }
    }

    public string BannerText
    {
        get
        {
            Money variance = Variance;
            return variance.Centavos switch
            {
                0 => "BALANCED",
                < 0 => $"SHORT {MoneyDisplay.Peso(variance.Negate())}",
                _ => $"OVER {MoneyDisplay.Peso(variance)}",
            };
        }
    }

    // Non-color banner channels for the view (each renders text + glyph + tint).
    public bool IsBalanced => BannerKind == "Balanced";
    public bool IsShort => BannerKind == "Short";
    public bool IsOver => BannerKind == "Over";

    private Money Variance
    {
        get
        {
            if (!TryParseAmount(ActualCashText, out Money counted))
            {
                return Money.Zero;
            }

            return counted.Minus(_summary.ExpectedCash);
        }
    }

    private void SaveDraft() => DraftSaved = true;

    private void SubmitClose()
    {
        if (!CanSubmitClose)
        {
            SetCountedError();
            return;
        }

        // BG-004 placeholder: BG-005 writes the DailyClose document. The
        // locked display state is UI-only over simulated data.
        IsClosed = true;
    }

    private void CancelEdit() => SetFieldError(nameof(ActualCashText), null);

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
        if (SubmitCloseCommand is RelayCommand command)
        {
            command.NotifyCanExecuteChanged();
        }

        OnPropertyChanged(nameof(CanSubmitClose));
    }
}

/// <summary>
/// One row of the optional inventory count table (docs/03 page 9): expected
/// value from the scenario; counted/variance/reason are blank placeholders
/// (BG-005 wires the count entry use case).
/// </summary>
public sealed record CountRow(string Sku, string Product, string Expected, string Counted = "", string Variance = "", string Reason = "");
