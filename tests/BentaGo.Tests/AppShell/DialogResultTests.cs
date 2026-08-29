using BentaGo.App.Dialogs;
using BentaGo.App.Dialogs.Confirm;
using BentaGo.App.Dialogs.Payment;
using BentaGo.App.Features.Sales;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Payments;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Dialog result tests (bg-004-synthesis §10, docs/05 "dialog results"):
/// confirm path returns the typed result, Esc/cancel returns null, result
/// routing drives the invoking view model, and completion gates block
/// confirm. All headless through the FakeDialogService; the real modal
/// round-trip (plus the top-dialog guard) is covered by the STA
/// DialogGuardAndFocusStaTests and lane L6.
/// </summary>
public sealed class DialogResultTests : IDisposable
{
    private readonly AppShellTestHost _host;
    private readonly FakeDialogService _dialogs;

    public DialogResultTests()
    {
        _host = new AppShellTestHost(extra: services =>
        {
            services.AddSingleton<IFocusService, RecordingFocusService>();
            services.AddSingleton<IDialogService, FakeDialogService>();
        });
        _dialogs = (FakeDialogService)_host.Services.GetRequiredService<IDialogService>();
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task PaymentConfirmRaisesTypedResultAndCompletesReceipt()
    {
        SalesSeed.Seed(_host.Services);
        var sales = _host.Services.GetRequiredService<SalesViewModel>();
        await sales.OnActivatedAsync();
        Assert.True(sales.IsSessionReady);

        sales.ScanBoxText = SalesSeed.SardinesSku;
        sales.ScanCommand.Execute(null);
        await Task.Delay(150);
        Assert.True(sales.HasCartItems, "scan should add the product");

        _dialogs.CannedResult = new PaymentDialogResult(
            new[] { new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(45m), null) },
            Money.Zero);

        sales.CheckoutCommand.Execute(null);
        await Task.Delay(300);

        var show = Assert.Single(_dialogs.Shows);
        Assert.Equal(typeof(PaymentDialogResult), show.ResultType);
        Assert.Equal(AutomationIds.SalesScanBox, show.FocusAfterClose);
        Assert.True(sales.HasReceipt);
        Assert.Empty(sales.Cart);
        Assert.Contains("complete", sales.ReceiptText);
    }

    [Fact]
    public async Task EscCancelReturnsNullAndLeavesCartUntouched()
    {
        SalesSeed.Seed(_host.Services);
        var sales = _host.Services.GetRequiredService<SalesViewModel>();
        await sales.OnActivatedAsync();

        sales.ScanBoxText = SalesSeed.SardinesSku;
        sales.ScanCommand.Execute(null);
        await Task.Delay(150);
        _dialogs.CannedResult = null; // Esc/cancel

        sales.CheckoutCommand.Execute(null);
        await Task.Delay(300);

        Assert.False(sales.HasReceipt);
        Assert.Single(sales.Cart);
    }

    [Fact]
    public void ConfirmDialogViewModelRaisesTypedResultOnConfirmAndNullOnCancel()
    {
        var vm = _host.Services.GetRequiredService<ConfirmDialogViewModel>();
        vm.Message = "Remove item?";
        var results = new List<DialogResultRequest<ConfirmDialogResult>?>();
        vm.CloseRequested += (_, e) => results.Add(e);

        vm.ConfirmCommand.Execute(null);
        vm.ConfirmCommand.Execute(null);
        Assert.Equal(2, results.Count);
        Assert.True(results.All(r => r!.Result!.Confirmed));

        var cancelResults = new List<DialogResultRequest<ConfirmDialogResult>?>();
        var vm2 = _host.Services.GetRequiredService<ConfirmDialogViewModel>();
        vm2.CloseRequested += (_, e) => cancelResults.Add(e);
        vm2.CancelCommand.Execute(null);
        Assert.Null(Assert.Single(cancelResults)!.Result);
    }
}
