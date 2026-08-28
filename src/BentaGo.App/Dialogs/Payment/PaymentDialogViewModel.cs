using System.Globalization;
using System.Windows.Input;
using BentaGo.App.Data;
using BentaGo.App.Mvvm;
using BentaGo.App.Ui;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Payments;

namespace BentaGo.App.Dialogs.Payment;

/// <summary>
/// Payment dialog state (bg-004-synthesis §3, screen 4): method strip
/// (Cash/GCash/Maya/Bank/Credit/Split), tender entry, split lines with
/// zero-balance enforcement, credit completeness. The completion gate
/// (CanConfirm) is state only - BG-005 owns the real tender business rules.
/// Enter confirms through the (disabled-until-gate) CONFIRM button; Esc
/// dismisses; F6 focuses the method strip.
/// </summary>
public sealed class PaymentDialogViewModel : DialogViewModelBase<PaymentDialogResult>
{
    private const string CashId = "cash";
    private const string GcashId = "gcash";
    private const string MayaId = "maya";
    private const string BankId = "bank";
    private const string CreditId = "credit";
    private const string SplitId = "split";

    private Money _total;
    private string _selectedMethodId = CashId;
    private string _amountReceivedText = "";
    private string _customerName = "";
    private string _reference = "";

    public PaymentDialogViewModel()
    {
        MethodOptions =
        [
            new MethodOption(CashId, "Cash"),
            new MethodOption(GcashId, "GCash"),
            new MethodOption(MayaId, "Maya"),
            new MethodOption(BankId, "Bank"),
            new MethodOption(CreditId, "Credit/Utang"),
            new MethodOption(SplitId, "Split"),
        ];
        SplitLines = new System.Collections.ObjectModel.ObservableCollection<SplitLineDraft>();

        ConfirmCommand = new RelayCommand(_ => Confirm(), _ => CanConfirm);
        SelectMethodCommand = new RelayCommand(parameter => SelectMethod((string)parameter!));
        AppendDigitCommand = new RelayCommand(parameter => AppendDigit((string)parameter!));
        ClearEntryCommand = new RelayCommand(_ => ClearEntry());
        AddSplitLineCommand = new RelayCommand(_ => AddSplitLine());
        RemoveSplitLineCommand = new RelayCommand(parameter => RemoveSplitLine((SplitLineDraft)parameter!));
        FocusMethodCommand = new RelayCommand(_ => { });
    }

    public Money Total
    {
        get => _total;
        private set => SetProperty(ref _total, value);
    }

    public string TotalText => MoneyDisplay.Peso(Total);

    public IReadOnlyList<MethodOption> MethodOptions { get; }

    public System.Collections.ObjectModel.ObservableCollection<SplitLineDraft> SplitLines { get; }

    public ICommand ConfirmCommand { get; }

    public ICommand SelectMethodCommand { get; }

    public ICommand AppendDigitCommand { get; }

    public ICommand ClearEntryCommand { get; }

    public ICommand AddSplitLineCommand { get; }

    public ICommand RemoveSplitLineCommand { get; }

    /// <summary>F6: focus payment method (bound inside the payment view).</summary>
    public ICommand FocusMethodCommand { get; }

    public string SelectedMethodId
    {
        get => _selectedMethodId;
        private set
        {
            if (SetProperty(ref _selectedMethodId, value))
            {
                foreach (MethodOption option in MethodOptions)
                {
                    option.IsSelected = option.Id == value;
                }

                OnPropertyChanged(nameof(IsCashSelected));
                OnPropertyChanged(nameof(IsCardSelected));
                OnPropertyChanged(nameof(IsCreditSelected));
                OnPropertyChanged(nameof(IsSplitSelected));
                NotifyGateChanged();
            }
        }
    }

    public bool IsCashSelected => SelectedMethodId == CashId;
    public bool IsCardSelected => SelectedMethodId is GcashId or MayaId or BankId;
    public bool IsCreditSelected => SelectedMethodId == CreditId;
    public bool IsSplitSelected => SelectedMethodId == SplitId;

    public string AmountReceivedText
    {
        get => _amountReceivedText;
        set
        {
            if (SetProperty(ref _amountReceivedText, value))
            {
                OnPropertyChanged(nameof(ChangeText));
                NotifyGateChanged();
            }
        }
    }

    public string ChangeText
    {
        get
        {
            if (!IsCashSelected || !TryParseAmount(AmountReceivedText, out Money received) || received.Centavos < Total.Centavos)
            {
                return MoneyDisplay.Peso(Money.Zero);
            }

            return MoneyDisplay.Peso(received.Minus(Total));
        }
    }

    public string CustomerName
    {
        get => _customerName;
        set
        {
            if (SetProperty(ref _customerName, value))
            {
                NotifyGateChanged();
            }
        }
    }

    public string Reference
    {
        get => _reference;
        set
        {
            if (SetProperty(ref _reference, value))
            {
                NotifyGateChanged();
            }
        }
    }

    public string RemainingText
    {
        get
        {
            Money remaining = Total.Minus(SplitTotal());
            return MoneyDisplay.Peso(remaining.Centavos < 0 ? Money.Zero : remaining);
        }
    }

    /// <summary>
    /// Completion gate: cash sufficient / single-method exact / credit
    /// complete / split balance zero. State flags only (bg-004-synthesis §3).
    /// </summary>
    public bool CanConfirm =>
        SelectedMethodId switch
        {
            CashId => TryParseAmount(AmountReceivedText, out Money received) && received.Centavos >= Total.Centavos,
            GcashId or MayaId or BankId => TryParseAmount(AmountReceivedText, out Money received) && received.Centavos == Total.Centavos,
            CreditId => !string.IsNullOrWhiteSpace(CustomerName) && !string.IsNullOrWhiteSpace(Reference),
            SplitId => SplitBalanceZero(),
            _ => false,
        };

    /// <summary>Loads the cart total before the dialog is shown.</summary>
    public void Load(Money total)
    {
        Total = total;
        SelectedMethodId = CashId;
        AmountReceivedText = "";
        CustomerName = "";
        Reference = "";
        SplitLines.Clear();
        NotifyGateChanged();
    }

    private void SelectMethod(string id)
    {
        if (MethodOptions.Any(option => option.Id == id))
        {
            SelectedMethodId = id;
        }
    }

    private void AppendDigit(string digit)
    {
        if (digit == "C")
        {
            ClearEntry();
            return;
        }

        if (digit == "." && AmountReceivedText.Contains('.', StringComparison.Ordinal))
        {
            return;
        }

        string next = digit == "." ? (AmountReceivedText.Length == 0 ? "0." : AmountReceivedText + ".") : AmountReceivedText + digit;
        if (next.Count(c => c == '.') == 0 && next.Length > 6)
        {
            return; // sane entry bound for the placeholder keypad
        }

        // Keep at most two decimal places.
        int dot = next.IndexOf('.');
        if (dot >= 0 && next.Length - dot - 1 > 2)
        {
            return;
        }

        AmountReceivedText = next;
    }

    private void ClearEntry() => AmountReceivedText = "";

    private void AddSplitLine()
    {
        var line = new SplitLineDraft(NotifyGateChanged);
        if (SplitLines.Any() && SplitLines[^1].AmountText.Length == 0)
        {
            return; // avoid piling up empty rows
        }

        SplitLines.Add(line);
        NotifyGateChanged();
    }

    private void RemoveSplitLine(SplitLineDraft line)
    {
        SplitLines.Remove(line);
        NotifyGateChanged();
    }

    private Money SplitTotal()
    {
        Money total = Money.Zero;
        foreach (SplitLineDraft line in SplitLines)
        {
            if (!TryParseAmount(line.AmountText, out Money amount))
            {
                return Total; // any unparseable line keeps the balance non-zero
            }

            total = total.Plus(amount);
        }

        return total;
    }

    private bool SplitBalanceZero()
    {
        if (SplitLines.Count == 0)
        {
            return false;
        }

        foreach (SplitLineDraft line in SplitLines)
        {
            if (!TryParseAmount(line.AmountText, out _))
            {
                return false;
            }
        }

        return SplitTotal().Centavos == Total.Centavos;
    }

    private void Confirm()
    {
        var lines = new List<PaymentLineDraft>();
        Money change = Money.Zero;
        if (SelectedMethodId == SplitId)
        {
            foreach (SplitLineDraft line in SplitLines)
            {
                if (TryParseAmount(line.AmountText, out Money amount))
                {
                    lines.Add(new PaymentLineDraft(line.Method, amount, ""));
                }
            }
        }
        else if (TryParseAmount(AmountReceivedText, out Money tender))
        {
            PaymentMethod method = MethodFor(SelectedMethodId);
            Money amount = method == PaymentMethod.Cash ? Total : tender;
            lines.Add(new PaymentLineDraft(method, amount, SelectedMethodId == CreditId ? Reference : ""));
            if (method == PaymentMethod.Cash && tender.Centavos > Total.Centavos)
            {
                change = tender.Minus(Total);
            }
        }

        Close(new PaymentDialogResult(lines, change));
    }

    private static PaymentMethod MethodFor(string id) =>
        id switch
        {
            GcashId => PaymentMethod.Gcash,
            MayaId => PaymentMethod.Maya,
            BankId => PaymentMethod.BankTransfer,
            CreditId => PaymentMethod.Credit,
            _ => PaymentMethod.Cash,
        };

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

    private void NotifyGateChanged()
    {
        if (ConfirmCommand is RelayCommand command)
        {
            command.NotifyCanExecuteChanged();
        }

        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(RemainingText));
    }
}

/// <summary>One method-strip chip (rendered as a button with MethodTabButtonStyle).</summary>
public sealed class MethodOption : ViewModelBase
{
    private bool _isSelected;

    public MethodOption(string id, string label)
    {
        Id = id;
        Label = label;
    }

    public string Id { get; }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// One split payment line: cycle through the funded methods, type an amount.
/// Balance is recomputed on every change (zero-balance gate).
/// </summary>
public sealed class SplitLineDraft : ViewModelBase
{
    private static readonly PaymentMethod[] FundedMethods =
    [
        PaymentMethod.Cash, PaymentMethod.Gcash, PaymentMethod.Maya, PaymentMethod.BankTransfer,
    ];

    private readonly Action _changed;
    private int _methodIndex;
    private string _amountText = "";

    public SplitLineDraft(Action changed) => _changed = changed;

    public PaymentMethod Method => FundedMethods[_methodIndex];

    public string MethodLabel => Method.DisplayName();

    public string AmountText
    {
        get => _amountText;
        set
        {
            if (SetProperty(ref _amountText, value))
            {
                _changed();
            }
        }
    }

    /// <summary>Cycle method: Cash -&gt; GCash -&gt; Maya -&gt; Bank -&gt; Cash.</summary>
    public void CycleMethod()
    {
        _methodIndex = (_methodIndex + 1) % FundedMethods.Length;
        OnPropertyChanged(nameof(Method));
        OnPropertyChanged(nameof(MethodLabel));
        _changed();
    }
}