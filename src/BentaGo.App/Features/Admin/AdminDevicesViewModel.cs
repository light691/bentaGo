using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Receipts;
using BentaGo.Core.Domain.Trips;
using BentaGo.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Features.Admin;

/// <summary>
/// BG-007 devices screen: the printer profile switch (Fake now, USB ESC/POS
/// in the device pass), a test receipt, and the scanner diagnostics list
/// (raw untrimmed input + exact suffix + matched product). Admin-gated by
/// the shell's AdminNavigateCommand like the other admin screens.
/// </summary>
public sealed class AdminDevicesViewModel : ViewModelBase, IRetainableScreen
{
    private readonly IPrinterSettingsStore _settings;
    private readonly IReceiptPrinter _printer;
    private readonly IScannerDiagnostics _scanner;
    private PrinterProfile _selectedProfile = PrinterProfile.Fake;
    private string? _statusText;
    private bool _isBusy;
    private IReadOnlyList<ScanInputEntry> _diagnostics = Array.Empty<ScanInputEntry>();

    public AdminDevicesViewModel(IServiceProvider services)
    {
        _settings = services.GetRequiredService<IPrinterSettingsStore>();
        _printer = services.GetRequiredService<IReceiptPrinter>();
        _scanner = services.GetRequiredService<IScannerDiagnostics>();

        SaveCommand = new RelayCommand(async _ => await SaveAsync(), _ => !IsBusy);
        TestPrintCommand = new RelayCommand(async _ => await TestPrintAsync(), _ => !IsBusy);
        RefreshCommand = new RelayCommand(_ => RefreshDiagnostics());

        Profiles =
        [
            new ProfileOption(PrinterProfile.Fake, "Simulated printer (no hardware)"),
            new ProfileOption(PrinterProfile.UsbEscPos, "USB ESC/POS 80 mm (Epson TM-class)"),
        ];
        SelectedProfile = PrinterProfile.Fake;
    }

    public ICommand SaveCommand { get; }

    public ICommand TestPrintCommand { get; }

    public ICommand RefreshCommand { get; }

    public IReadOnlyList<ProfileOption> Profiles { get; }

    public PrinterProfile SelectedProfile
    {
        get => _selectedProfile;
        set => SetProperty(ref _selectedProfile, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommands();
            }
        }
    }

    public string? StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public IReadOnlyList<ScanInputEntry> Diagnostics
    {
        get => _diagnostics;
        private set => SetProperty(ref _diagnostics, value);
    }

    public async Task OnActivatedAsync()
    {
        SelectedProfile = await _settings.GetProfileAsync();
        RefreshDiagnostics();
    }

    public Task OnDeactivatedAsync() => Task.CompletedTask;

    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            await _settings.SetProfileAsync(SelectedProfile);
            StatusText = $"Printer profile: {SelectedProfile} (applies to the next print).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TestPrintAsync()
    {
        IsBusy = true;
        try
        {
            ReceiptPrintResult result = await _printer.PrintAsync(TestReceipt(), default);
            StatusText = result.Status == PrintJobStatus.Printed
                ? $"Test receipt sent to {SelectedProfile}."
                : $"Test receipt FAILED: {result.ErrorCode} (check power/cable).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshDiagnostics()
    {
        Diagnostics = _scanner.Recent;
        StatusText = Diagnostics.Count == 0
            ? "No scans received yet. Scan once on the Sales screen and return here."
            : $"{Diagnostics.Count} recent scan(s); the newest is first. Raw suffix is shown verbatim.";
    }

    private static ReceiptDocument TestReceipt()
    {
        var when = new DateTimeOffset(2026, 1, 15, 8, 30, 0, TimeSpan.Zero);
        return new ReceiptDocument(
            new ReceiptMeta("TEST-0000", when, new PeddlerId(Guid.NewGuid()), new TripId(Guid.NewGuid())),
            new[]
            {
                new ReceiptLine("SARDINES", Money.FromPesos(45m), Quantity.Of(1), Money.Zero),
                new ReceiptLine("KOPI 3IN1", Money.FromPesos(8m), Quantity.Of(2), Money.Zero),
            },
            Money.FromPesos(61m), Money.Zero, Money.FromPesos(61m), Money.FromPesos(9m),
            IsReprint: false, ReferenceNote: null);
    }

    private void NotifyCommands()
    {
        var cmds = new[] { SaveCommand, TestPrintCommand, RefreshCommand };
        foreach (ICommand command in cmds)
        {
            (command as RelayCommand)?.NotifyCanExecuteChanged();
        }
    }
}

/// <summary>Display option for the printer profile combo.</summary>
public sealed record ProfileOption(PrinterProfile Profile, string Label);
