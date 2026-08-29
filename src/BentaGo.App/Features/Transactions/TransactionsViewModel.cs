using System.Windows.Input;
using BentaGo.App.Data;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Display;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Features.Transactions;

/// <summary>
/// BG-006b: today's transactions for the signed-in peddler (read-only
/// history over ISaleQueryService). Rows are pure projections of
/// SaleHistoryRow — the service owns the fold, the screen only formats.
/// </summary>
public sealed class TransactionsViewModel : ViewModelBase, IRetainableScreen
{
    private readonly IActorContext _actors;
    private readonly IUserAccountStore _users;
    private readonly ISaleQueryService _sales;
    private readonly IFocusService _focus;
    private PeddlerId? _peddler;
    private string _filterText = "";
    private string? _stateMessage;
    private IReadOnlyList<TransactionRow> _rows = Array.Empty<TransactionRow>();

    public TransactionsViewModel(IServiceProvider services)
    {
        _actors = services.GetRequiredService<IActorContext>();
        _users = services.GetRequiredService<IUserAccountStore>();
        _sales = services.GetRequiredService<ISaleQueryService>();
        _focus = services.GetRequiredService<IFocusService>();
        RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
        FocusFilterCommand = new RelayCommand(_ => _focus.Request(AutomationIds.TransactionsFilter));
    }

    public ICommand RefreshCommand { get; }

    public ICommand FocusFilterCommand { get; }

    /// <summary>MainViewModel calls this when the screen is navigated to.</summary>
    public async Task OnActivatedAsync()
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _peddler = await ResolvePeddlerAsync();
        if (_peddler is null)
        {
            StateMessage = "No peddler link on this account - ask an admin.";
            Rows = Array.Empty<TransactionRow>();
            return;
        }

        StateMessage = null;
        Result<IReadOnlyList<SaleHistoryRow>> result =
            _sales.Query(_peddler.Value, new SaleHistoryFilter(ManilaToday(), null, null, null, null, null));
        if (result.IsFailure)
        {
            StateMessage = "Could not load transactions right now.";
            Rows = Array.Empty<TransactionRow>();
            return;
        }

        Rows = result.Value
            .Select(row => new TransactionRow(
                row.ReceiptNumber,
                ManilaDisplay.UtcAsManila(row.CreatedUtc).ToString("HH:mm"),
                MoneyDisplay.Peso(row.Total),
                PaymentsText(row),
                StatusText(row.Status),
                PrintText(row)))
            .ToArray();
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(RowCountText));
    }

    private async Task<PeddlerId?> ResolvePeddlerAsync()
    {
        UserId? id = _actors.CurrentUserId;
        if (id is null)
        {
            return null;
        }

        User? actor = await _users.FindByIdAsync(id.Value);
        return actor?.PeddlerLink;
    }

    public IReadOnlyList<TransactionRow> Rows
    {
        get => _rows;
        private set
        {
            _rows = value;
            OnPropertyChanged();
        }
    }

    public string? StateMessage
    {
        get => _stateMessage;
        private set
        {
            _stateMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStateMessage));
        }
    }

    public bool HasStateMessage => !string.IsNullOrEmpty(StateMessage);

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                OnPropertyChanged(nameof(FilteredRows));
                OnPropertyChanged(nameof(HasFilteredRows));
                OnPropertyChanged(nameof(RowCountText));
            }
        }
    }

    public IReadOnlyList<TransactionRow> FilteredRows
    {
        get
        {
            string query = FilterText.Trim();
            if (query.Length == 0)
            {
                return Rows;
            }

            return Rows
                .Where(row => row.ReceiptNumber.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }

    public bool HasFilteredRows => FilteredRows.Count > 0;

    public bool HasRows => Rows.Count > 0;

    public string RowCountText =>
        FilteredRows.Count == 1 ? "1 transaction" : $"{FilteredRows.Count} transactions";

    private static string PaymentsText(SaleHistoryRow row) =>
        string.Join("; ", row.Payments.Select(p =>
            $"{p.Method.DisplayName()} {MoneyDisplay.Peso(p.Amount)}"));

    private static string StatusText(SaleStatus status) =>
        status switch
        {
            SaleStatus.Committed => "Completed",
            SaleStatus.Voided => "Voided",
            SaleStatus.PartiallyReturned => "Partially returned",
            SaleStatus.FullyReturned => "Returned",
            _ => status.ToString(),
        };

    private static string PrintText(SaleHistoryRow row) =>
        row.PrintStatus == PrintStatus.Pending ? "Not printed" :
        row.PrintStatus == PrintStatus.Printed ? "Printed" : "Print failed";

    private static DateOnly ManilaToday() =>
        DateOnly.FromDateTime(ManilaDisplay.UtcAsManila(DateTimeOffset.UtcNow).DateTime);
}

/// <summary>One history row projection (display formatting lives here).</summary>
public sealed record TransactionRow(
    string ReceiptNumber, string TimeText, string TotalText,
    string PaymentsText, string StatusText, string PrintText);
