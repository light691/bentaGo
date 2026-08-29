using System.Globalization;
using System.Windows.Input;
using BentaGo.App.Data;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Payments;
using Microsoft.Extensions.DependencyInjection;

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

    private readonly IFocusService _focus;
    private Money _total;
    private int _itemCount;
    private string _selectedMethodId = CashId;
    private string _amountReceivedText = "";
    private string _customerName = "";
    private string _reference = "";

    public PaymentDialogViewModel(IServiceProvider services)
    {
        _focus = services.GetRequiredService<IFocusService>();
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
        FocusMethodCommand = new RelayCommand(_ => _focus.Request(AutomationIds.PaymentMethodTabs));
    }

    public Money Total
    {
        get => _total;
        private set => SetProperty(ref _total, value);
    }

    public string TotalText => MoneyDisplay.Peso(Total);

    public int ItemCount
    {
        get => _itemCount;
        private set => SetProperty(ref _itemCount, value);
    }

    public string SaleSummaryText =>
        ItemCount > 0 ? $"{ItemCount} item(s) in this sale" : "Review the current sale total before confirming.";

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
                OnPropertyChanged(nameof(IsReferenceRequired));
                OnPropertyChanged(nameof(ReferenceLabel));
                OnPropertyChanged(nameof(IsReferenceEntryVisible));
                OnPropertyChanged(nameof(IsCustomerEntryVisible));
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
    /// complete / split balance zero with per-line reference gates. State
    /// flags only — the session re-validates on SetPayment (BG-006b).
    /// </summary>
    public bool CanConfirm =>
        SelectedMethodId switch
        {
            CashId => TryParseAmount(AmountReceivedText, out Money received) && received.Centavos >= Total.Centavos,
            GcashId => TryParseAmount(AmountReceivedText, out Money received) && received.Centavos == Total.Centavos,
            MayaId => TryParseAmount(AmountReceivedText, out Money received) && received.Centavos == Total.Centavos,
            BankId => TryParseAmount(AmountReceivedText, out Money received) && received.Centavos == Total.Centavos
                     && !string.IsNullOrWhiteSpace(Reference),
            CreditId => !string.IsNullOrWhiteSpace(CustomerName) && !string.IsNullOrWhiteSpace(Reference),
            SplitId => SplitBalanceZero(),
            _ => false,
        };

    /// <summary>True when the selected method requires a reference (Bank/Credit).</summary>
    public bool IsReferenceRequired => SelectedMethodId is BankId or CreditId;

    public string ReferenceLabel => SelectedMethodId switch
    {
        CreditId => "Customer reference",
        GcashId => "GCash reference (optional)",
        MayaId => "Maya reference (optional)",
        _ => "Bank reference",
    };

    /// <summary>Wallet/bank methods capture an optional-or-required reference.</summary>
    public bool IsReferenceEntryVisible => SelectedMethodId is BankId or GcashId or MayaId;

    /// <summary>Credit needs a customer name (and a reference).</summary>
    public bool IsCustomerEntryVisible => SelectedMethodId == CreditId;

    /// <summary>Loads the cart total before the dialog is shown.</summary>
    public void Load(Money total, int itemCount = 0)
    {
        Total = total;
        ItemCount = Math.Max(0, itemCount);
        OnPropertyChanged(nameof(SaleSummaryText));
        SelectedMethodId = CashId;
        // Cash is the default already, so the property setter is a no-op and
        // the option flags are never synced; hydrate them explicitly so the
        // selected method renders green-filled (round-6 visual contract).
        foreach (MethodOption option in MethodOptions)
        {
            option.IsSelected = option.Id == SelectedMethodId;
        }

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

            if (line.Method == PaymentMethod.BankTransfer && string.IsNullOrWhiteSpace(line.Reference))
            {
                return false; // a bank transfer line needs its reference
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
                    string? reference = line.Method == PaymentMethod.BankTransfer
                        ? line.Reference.Trim()
                        : string.IsNullOrWhiteSpace(line.Reference) ? null : line.Reference.Trim();
                    lines.Add(new PaymentLineDraft(line.Method, amount, reference));
                }
            }
        }
        else if (TryParseAmount(AmountReceivedText, out Money tender))
        {
            PaymentMethod method = MethodFor(SelectedMethodId);
            Money amount = method == PaymentMethod.Cash ? Total : tender;
            string? reference = SelectedMethodId is BankId or GcashId or MayaId
                ? (string.IsNullOrWhiteSpace(Reference) ? null : Reference.Trim())
                : SelectedMethodId == CreditId ? Reference : "";
            lines.Add(new PaymentLineDraft(method, amount, reference));
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
/// One split payment line: cycle through the funded methods, type an amount,
/// and (for wallet/bank) an optional reference — REQUIRED for BankTransfer.
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
    private string _reference = "";

    public SplitLineDraft(Action changed)
    {
        _changed = changed;
        CycleMethodCommand = new RelayCommand(_ => CycleMethod());
    }

    public System.Windows.Input.ICommand CycleMethodCommand { get; }

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

    public string Reference
    {
        get => _reference;
        set
        {
            if (SetProperty(ref _reference, value))
            {
                _changed();
            }
        }
    }

    public bool IsReferenceRequired => Method == PaymentMethod.BankTransfer;

    public string ReferenceLabel => IsReferenceRequired ? "Reference *" : "Reference";

    public bool IsReferenceEntryVisible => Method != PaymentMethod.Cash;

    /// <summary>Cycle method: Cash -&gt; GCash -&gt; Maya -&gt; Bank -&gt; Cash.</summary>
    public void CycleMethod()
    {
        _methodIndex = (_methodIndex + 1) % FundedMethods.Length;
        OnPropertyChanged(nameof(Method));
        OnPropertyChanged(nameof(MethodLabel));
        OnPropertyChanged(nameof(IsReferenceRequired));
        OnPropertyChanged(nameof(ReferenceLabel));
        OnPropertyChanged(nameof(IsReferenceEntryVisible));
        _changed();
    }
}
