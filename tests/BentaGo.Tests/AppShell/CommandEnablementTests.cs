using BentaGo.App;
using BentaGo.App.Dialogs.Payment;
using BentaGo.App.Features.Login;
using BentaGo.App.Features.Reconciliation;
using BentaGo.App.Features.Sales;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Command-enablement tests (bg-004-synthesis §10, docs/05 "command
/// enablement"): empty cart disables CHECKOUT, Payment CanConfirm gates,
/// reconciliation Submit-Close gate, login gate extension, F12 parity.
/// Deterministic via RelayCommand.NotifyCanExecuteChanged.
/// </summary>
public sealed class CommandEnablementTests : IDisposable
{
    private readonly AppShellTestHost _host;

    public CommandEnablementTests()
    {
        _host = new AppShellTestHost(seedAdmin: true, extra: services => services.AddSingleton<IFocusService, RecordingFocusService>());
    }

    public void Dispose() => _host.Dispose();

    private SalesViewModel Sales() => _host.Services.GetRequiredService<SalesViewModel>();

    [Fact]
    public async Task EmptyCartDisablesCheckout()
    {
        SalesSeed.Seed(_host.Services);
        var sales = Sales();
        await sales.OnActivatedAsync();
        Assert.Empty(sales.Cart);
        Assert.False(sales.CheckoutCommand.CanExecute(null));

        sales.ScanBoxText = "Sardines";
        sales.ScanCommand.Execute(null);
        await Task.Delay(150);
        Assert.Single(sales.Cart);
        Assert.True(sales.CheckoutCommand.CanExecute(null));

        sales.ClearCartCommand.Execute(null);
        Assert.False(sales.CheckoutCommand.CanExecute(null));
    }

    [Fact]
    public void PaymentConfirmGateHoldsForCashSplitAndCredit()
    {
        var vm = _host.Services.GetRequiredService<PaymentDialogViewModel>();
        vm.Load(Money.FromPesos(1000m));

        // Cash: insufficient stays disabled, sufficient enables + change shown.
        Assert.False(vm.CanConfirm);
        vm.AmountReceivedText = "500";
        Assert.False(vm.CanConfirm);
        vm.AmountReceivedText = "1000";
        Assert.True(vm.CanConfirm);
        vm.AmountReceivedText = "1250";
        Assert.True(vm.CanConfirm);
        Assert.Equal("\u20B1250.00", vm.ChangeText);

        // Split: zero balance required.
        vm.SelectMethodCommand.Execute("split");
        Assert.False(vm.CanConfirm);
        vm.AddSplitLineCommand.Execute(null);
        vm.SplitLines[0].AmountText = "400";
        Assert.False(vm.CanConfirm);
        vm.AddSplitLineCommand.Execute(null);
        vm.SplitLines[1].AmountText = "600";
        Assert.True(vm.CanConfirm);

        // Credit: customer + reference required.
        vm.SelectMethodCommand.Execute("credit");
        Assert.False(vm.CanConfirm);
        vm.CustomerName = "Aling Nena";
        Assert.False(vm.CanConfirm);
        vm.Reference = "0917-000-0000";
        Assert.True(vm.CanConfirm);
    }

    [Fact]
    public void ReconciliationSubmitGateRequiresParseableCashAndOpenState()
    {
        var vm = _host.Services.GetRequiredService<ReconciliationViewModel>();
        Assert.False(vm.CanSubmitClose);
        Assert.False(vm.SubmitCloseCommand.CanExecute(null));

        vm.ActualCashText = "not-a-number";
        Assert.False(vm.CanSubmitClose);
        vm.ActualCashText = "";
        Assert.False(vm.CanSubmitClose);

        vm.ActualCashText = "180.00";
        Assert.True(vm.CanSubmitClose);
        Assert.True(vm.SubmitCloseCommand.CanExecute(null));

        vm.SubmitCloseCommand.Execute(null);
        Assert.True(vm.IsClosed);
        Assert.False(vm.CanSubmitClose, "a closed trip cannot be submitted again");
    }

    [Fact]
    public void LoginGateBlocksSubmitUntilFieldsAreFilled()
    {
        var login = _host.Services.GetRequiredService<LoginViewModel>();
        Assert.False(login.CanLogin);
        Assert.False(login.LoginCommand.CanExecute(null));

        login.Username = "admin";
        Assert.False(login.CanLogin);
        login.Password = "admin";
        Assert.True(login.CanLogin);
        Assert.True(login.LoginCommand.CanExecute(null));
    }

    [Fact]
    public void F12CheckoutCommandParityHolds()
    {
        var main = _host.Services.GetRequiredService<MainViewModel>();
        var mapping = main.CommandMapping("checkout");
        Assert.Same(main.CheckoutCommand, mapping.Command);
        Assert.Equal(App.Navigation.Shortcuts.ShortcutStatus.Active,
            App.Navigation.Shortcuts.ShortcutCatalog.All.Single(e => e.Id == "checkout").Status);
    }
}
