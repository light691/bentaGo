using System.Linq;
using System.Threading.Tasks;
using BentaGo.App.Data;
using BentaGo.App.Features.Reconciliation;
using BentaGo.App.Features.Sales;
using BentaGo.App.Mvvm;
using BentaGo.Application.Contracts;
using BentaGo.Application.Scenario;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Catalog;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Reconciliation;
using BentaGo.Core.Domain.Trips;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// BG-008 Slice B wiring tests (headless): the reconciliation view model over
/// the REAL use cases — Save Draft persists submitted counts with NO trip lock
/// and NO final audit event, Submit Close writes the DailyClose + TripClose
/// movement + close lock + close receipt, Reopen is approval-gated (reason +
/// actor + approver recorded, prior close intact), the variance label/sign
/// mapping matches the report's convention, and a closed trip surfaces the
/// shared TRIP_CLOSED_CORRECTION copy.
/// </summary>
public sealed class ReconciliationWiringTests : IDisposable
{
    private readonly AppShellTestHost _host;

    public ReconciliationWiringTests() => _host = new AppShellTestHost();

    public void Dispose() => _host.Dispose();

    private ServiceProvider Services => _host.Services;

    private SqliteLedgerStore Store => Services.GetRequiredService<SqliteLedgerStore>();

    private async Task<ReconciliationViewModel> ActivateAsync()
    {
        SalesSeed.Seed(Services);
        ReconciliationViewModel vm = Services.GetRequiredService<ReconciliationViewModel>();
        await vm.OnActivatedAsync();
        Assert.True(vm.HasTrip, vm.FatalMessage ?? "no trip");
        return vm;
    }

    private async Task<ReconciliationViewModel> ActivateWithSaleAsync()
    {
        ReconciliationViewModel vm = await ActivateAsync();
        await SellCash90Async();
        vm = Services.GetRequiredService<ReconciliationViewModel>();
        await vm.OnActivatedAsync();
        return vm;
    }

    /// <summary>One cash sale of 2 × Sardines (₱45 each = ₱90.00) through the
    /// REAL session factory (the seller the seed signed in): the trip's
    /// expected cash becomes ₱90.00.</summary>
    private async Task SellCash90Async()
    {
        var factory = Services.GetRequiredService<ICheckoutSessionFactory>();
        Result<ICheckoutSession> created = await factory.CreateSessionAsync();
        Assert.True(created.IsSuccess, created.Errors.FirstOrDefault()?.Code ?? "session failed");
        ICheckoutSession session = created.Value;

        Result<IReadOnlyList<ProductHit>> hits =
            await Services.GetRequiredService<IProductSearchService>().SearchAsync(SalesSeed.SardinesSku);
        Assert.True(hits.IsSuccess);
        Assert.True(session.AddProduct(hits.Value[0].Product, Quantity.Of(2)).IsSuccess);
        Assert.True(session.SetPayment(
            new[] { new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(90m), null) }, Money.Zero).IsSuccess);
        Result<SavedSaleState> submitted = await session.SubmitAsync();
        Assert.True(submitted.IsSuccess, submitted.Errors.FirstOrDefault()?.Code ?? "submit failed");
    }

    private ReconciliationCountRow Row(ReconciliationViewModel vm, string sku) =>
        vm.CountRows.Single(r => r.Sku == sku);

    private void Close(ReconciliationViewModel vm, string counted, string? notes = null)
    {
        vm.ActualCashText = counted;
        if (notes is not null)
        {
            vm.CloseNotesText = notes;
        }

        vm.SubmitCloseCommand.Execute(null);
    }

    // ------- Save Draft: counts become facts, nothing locks -------

    [Fact]
    public async Task DraftRecordsCounts_NoTripLock_NoFinalAudit()
    {
        ReconciliationViewModel vm = await ActivateWithSaleAsync();
        Assert.False(vm.DraftSaved);

        Row(vm, SalesSeed.SardinesSku).CountedText = "47"; // expected 48 after the sale
        vm.SaveDraftCommand.Execute(null);

        Assert.True(vm.DraftSaved);
        Assert.Equal(ReconciliationViewModel.DraftSavedCopy, vm.DraftStatusText);
        Assert.False(vm.IsClosed);
        Assert.True(vm.CanSaveDraft); // a draft never locks anything
        Assert.Empty(Store.Movements.OfType<TripClose>());
        Assert.Empty(Store.Movements.OfType<TripReopen>());
        Assert.DoesNotContain(Store.Audits, a =>
            a.Category == TripAudit.TripCloseCategory || a.Category == TripAudit.TripReopenCategory);
        Assert.Equal(TripStatus.Open, Store.State.Trip(Store.Context.Trips.Single().Id).Status);
        // The draft IS a real fact: the count document + its COUNT audit.
        Assert.Single(Store.Context.Counts);
        Assert.Single(Store.Audits, a => a.Category == "COUNT");
        Assert.Equal(47, Store.State.ObservedUnits(
            Row(vm, SalesSeed.SardinesSku).Product, Store.Context.Trips.Single().Location).Units);
    }

    [Fact]
    public async Task DraftPersistsAcrossActivation()
    {
        ReconciliationViewModel vm = await ActivateWithSaleAsync();
        Row(vm, SalesSeed.SardinesSku).CountedText = "47";
        vm.SaveDraftCommand.Execute(null);

        // A fresh VM instance rehydrates the draft from the ledger fold.
        ReconciliationViewModel reactivated = Services.GetRequiredService<ReconciliationViewModel>();
        await reactivated.OnActivatedAsync();

        Assert.True(reactivated.DraftSaved);
        Assert.Equal(ReconciliationViewModel.DraftSavedCopy, reactivated.DraftStatusText);
        Assert.Equal("47", Row(reactivated, SalesSeed.SardinesSku).CountedText);
    }

    [Fact]
    public async Task DraftWithNoEntriesIsRefusedWithCopy()
    {
        ReconciliationViewModel vm = await ActivateAsync();
        int movementsBefore = Store.Movements.Count;

        vm.SaveDraftCommand.Execute(null);

        Assert.Equal(ReconciliationViewModel.DraftEmptyCopy, vm.ErrorBanner);
        Assert.Equal(movementsBefore, Store.Movements.Count);
        Assert.False(vm.DraftSaved);
    }

    // ------- Submit Close: lock + close receipt -------

    [Fact]
    public async Task BalancedCloseLocksTripAndWritesCloseFacts()
    {
        ReconciliationViewModel vm = await ActivateWithSaleAsync();
        Assert.Equal(Money.FromPesos(90m), Store.State.TripCash(Store.Context.Trips.Single().Id, Fund.PhysicalCash));

        Close(vm, "90.00");

        Assert.True(vm.IsClosed);
        Assert.False(vm.CanSubmitClose);
        Assert.Single(Store.Movements.OfType<TripClose>());
        AuditEvent closeAudit = Store.Audits.Single(a => a.Category == TripAudit.TripCloseCategory);
        Assert.Equal(Store.Context.Closes.Single().By, closeAudit.Actor);
        DailyClose close = Store.Context.Closes.Single();
        Assert.Equal(Money.FromPesos(90m), close.ExpectedCash);
        Assert.Equal(Money.FromPesos(90m), close.CountedCash);
        Assert.Equal(TripStatus.Closed, Store.State.Trip(close.Trip).Status);
        // The close receipt is on screen and BALANCED needs no note.
        Assert.True(vm.HasCloseReceipt,
            $"banner='{vm.ErrorBanner}' isClosed={vm.IsClosed} closes={Store.Context.Closes.Count}");
        Assert.Equal("BALANCED", vm.CloseReceipt!.VarianceLabelText);
        Assert.Equal(MoneyDisplayString(90m), vm.CloseReceipt.CountedCashText);
        Assert.Null(vm.CloseReceipt.Notes);
        // The report renders the counted cash.
        Assert.True(vm.HasReport);
        Assert.Equal(MoneyDisplayString(90m), vm.Report!.CountedCashText);
    }

    [Fact]
    public async Task ShortageNeedsNoteAndRecordsIt()
    {
        ReconciliationViewModel vm = await ActivateWithSaleAsync();
        int movementsBefore = Store.Movements.Count;

        Close(vm, "80.00");
        Assert.Equal(ReconciliationViewModel.NoteRequiredShortCopy, vm.ErrorBanner);
        Assert.Equal("SHORT \u20B110.00", vm.BannerText);
        Assert.True(vm.IsShort);
        Assert.Empty(Store.Movements.OfType<TripClose>());
        Assert.Equal(movementsBefore, Store.Movements.Count);

        Close(vm, "80.00", "P10 bill fell behind the drawer");
        Assert.True(vm.IsClosed);
        DailyClose close = Store.Context.Closes.Single();
        Assert.Equal(Money.FromPesos(90m), close.ExpectedCash);
        Assert.Equal(Money.FromPesos(80m), close.CountedCash);
        Assert.Equal("P10 bill fell behind the drawer", close.Notes);
        Assert.True(vm.HasCloseReceipt);
        Assert.Equal("SHORTAGE", vm.CloseReceipt!.VarianceLabelText);
        Assert.True(vm.HasReport);
        Assert.Equal("SHORTAGE", vm.Report!.CashLabelText);
    }

    [Fact]
    public async Task OverageNeedsNoteAndRecordsIt()
    {
        ReconciliationViewModel vm = await ActivateWithSaleAsync();

        Close(vm, "100.00");
        Assert.Equal(ReconciliationViewModel.NoteRequiredOverCopy, vm.ErrorBanner);
        Assert.Equal("OVER \u20B110.00", vm.BannerText);
        Assert.True(vm.IsOver);
        Assert.Empty(Store.Movements.OfType<TripClose>());

        Close(vm, "100.00", "customer overpaid and left");
        Assert.True(vm.IsClosed);
        DailyClose close = Store.Context.Closes.Single();
        Assert.Equal("customer overpaid and left", close.Notes);
        Assert.True(vm.HasCloseReceipt);
        Assert.Equal("OVERAGE", vm.CloseReceipt!.VarianceLabelText);
        Assert.True(vm.HasReport);
        Assert.Equal("OVERAGE", vm.Report!.CashLabelText);
    }

    // ------- Reopen: approval gate, reason + actor, prior close intact -------

    [Fact]
    public async Task ReopenNeedsReasonThenApproverThenRecordsBoth()
    {
        ReconciliationViewModel vm = await ActivateWithSaleAsync();
        Close(vm, "90.00");
        Assert.True(vm.IsClosed);
        Assert.Equal(ClosedTripCopy(), vm.CorrectionGateText);
        int movementsBefore = Store.Movements.Count;

        vm.ReopenCommand.Execute(null);
        Assert.Equal(ReconciliationViewModel.ReopenReasonRequiredCopy, vm.ErrorBanner);

        vm.ReopenReasonText = "counted the wrong drawer";
        Assert.True(vm.NeedsApproverPicker); // the seller is a peddler actor
        vm.ReopenCommand.Execute(null);
        Assert.Equal(ReconciliationViewModel.ReopenApproverRequiredCopy, vm.ErrorBanner);
        Assert.Empty(Store.Movements.OfType<TripReopen>());
        Assert.Equal(movementsBefore, Store.Movements.Count);

        (UserId _, UserId admin) = await AddApproversAsync();
        ReconciliationViewModel refreshed = Services.GetRequiredService<ReconciliationViewModel>();
        await refreshed.OnActivatedAsync();
        refreshed.SelectedApprover = refreshed.Approvers.Single(a => a.Id == admin);
        refreshed.ReopenReasonText = "counted the wrong drawer";
        refreshed.ReopenCommand.Execute(null);

        Assert.False(refreshed.IsClosed);
        Assert.Equal(TripStatus.Open, Store.State.Trip(Store.Context.Trips.Single().Id).Status);
        AuditEvent reopen = Store.Audits.Single(a => a.Category == TripAudit.TripReopenCategory);
        Assert.Equal(sellerId(), reopen.Actor);
        Assert.Contains("counted the wrong drawer", reopen.Message, StringComparison.Ordinal);
        Assert.Contains("(approved by theadmin)", reopen.Message, StringComparison.Ordinal);
        Assert.Null(refreshed.CorrectionGateText);
        // The prior close remains a visible fact.
        Assert.Single(Store.Context.Closes);
        Assert.True(refreshed.HasCloseReceipt);
        Assert.NotNull(refreshed.ReopenSummaryText);
    }

    [Fact]
    public async Task SupervisorSelfApprovesReopenWithoutPicker()
    {
        ReconciliationViewModel vm = await ActivateWithSaleAsync();
        Close(vm, "90.00");
        Assert.True(vm.IsClosed);

        (UserId supervisor, UserId _) = await AddApproversAsync();
        Services.GetRequiredService<IActorContext>().Set(supervisor);
        ReconciliationViewModel supervisorVm = Services.GetRequiredService<ReconciliationViewModel>();
        await supervisorVm.OnActivatedAsync();

        Assert.True(supervisorVm.IsSelfApproval);
        Assert.False(supervisorVm.NeedsApproverPicker);
        supervisorVm.ReopenReasonText = "recount needed";
        supervisorVm.ReopenCommand.Execute(null);

        Assert.False(supervisorVm.IsClosed);
        Assert.Equal(TripStatus.Open, Store.State.Trip(Store.Context.Trips.Single().Id).Status);
        AuditEvent reopen = Store.Audits.Single(a => a.Category == TripAudit.TripReopenCategory);
        Assert.Equal(supervisor, reopen.Actor);
        Assert.DoesNotContain("approved by", reopen.Message, StringComparison.Ordinal);
    }

    // ------- Variance label / sign mapping -------

    [Fact]
    public async Task CountRowsMapSignToLabelAndValue()
    {
        ReconciliationViewModel vm = await ActivateAsync(); // 50 loaded, nothing sold

        ReconciliationCountRow shortRow = Row(vm, SalesSeed.SardinesSku);
        Assert.Equal("50", shortRow.ExpectedText);
        Assert.Equal("", shortRow.VarianceLabel);
        shortRow.CountedText = "45"; // 5 missing, load cost 30 -> -150
        Assert.Equal("SHORTAGE", shortRow.VarianceLabel);
        Assert.Equal("-5", shortRow.QuantityVarianceText);
        Assert.Equal(MoneyDisplayString(-150m), shortRow.ValueVarianceText);

        ReconciliationCountRow overRow = Row(vm, SalesSeed.KopiSku);
        overRow.CountedText = "52";
        Assert.Equal("OVERAGE", overRow.VarianceLabel);
        Assert.Equal("+2", overRow.QuantityVarianceText);
        Assert.Equal(MoneyDisplayString(60m), overRow.ValueVarianceText);

        shortRow.CountedText = "50";
        Assert.Equal("BALANCED", shortRow.VarianceLabel);
        Assert.Equal("0", shortRow.QuantityVarianceText);

        // Unparseable input counts as not counted.
        shortRow.CountedText = "abc";
        Assert.Equal("", shortRow.VarianceLabel);
        Assert.Equal("—", shortRow.QuantityVarianceText);
    }

    [Fact]
    public async Task ReportStockRowsCarryExpectedCountedAndValue()
    {
        ReconciliationViewModel vm = await ActivateAsync();
        Row(vm, SalesSeed.SardinesSku).CountedText = "47";
        vm.SaveDraftCommand.Execute(null);

        Assert.True(vm.HasReport);
        ReportStockRowView reportRow = vm.Report!.StockRows.Single(r => r.Sku == SalesSeed.SardinesSku);
        Assert.Equal("50", reportRow.ExpectedText);
        Assert.Equal("47", reportRow.CountedText);
        Assert.Equal("-3", reportRow.QuantityVarianceText);
        Assert.Equal(MoneyDisplayString(-90m), reportRow.ValueVarianceText); // 3 × load cost 30
        Assert.Equal("—", reportRow.NotesText);

        Row(vm, SalesSeed.SardinesSku).Notes = "two broken bottles";
        vm.SaveDraftCommand.Execute(null);
        Assert.True(vm.HasReport);
        reportRow = vm.Report!.StockRows.Single(r => r.Sku == SalesSeed.SardinesSku);
        Assert.Equal("two broken bottles", reportRow.NotesText);
    }

    // ------- helpers -------

    private UserId sellerId() => Services.GetRequiredService<IActorContext>().CurrentUserId
        ?? throw new InvalidOperationException("seed did not sign the seller in");

    private static string MoneyDisplayString(decimal pesos) => MoneyDisplay.Pesos(pesos);

    private static string ClosedTripCopy() => "This trip is closed - corrections need an authorized reopen.";

    private async Task<(UserId Supervisor, UserId Admin)> AddApproversAsync()
    {
        var ids = new ScenarioIdSource("recon-wiring-tests-v1");
        PeddlerId peddler = Store.Context.Trips.Single().Peddler;
        IPasswordHasher hasher = Services.GetRequiredService<IPasswordHasher>();
        IUserAccountStore accounts = Services.GetRequiredService<IUserAccountStore>();
        UserId seller = sellerId();

        var supervisor = new UserId(ids.Next("supervisor"));
        var admin = new UserId(ids.Next("admin"));
        await accounts.AddAsync(new User
        {
            Id = supervisor,
            Username = "maysupervisor",
            PasswordHash = hasher.Hash("approver"),
            Role = Role.Supervisor,
            PeddlerLink = peddler,
            CreatedBy = seller,
            CreatedUtc = DateTimeOffset.Now,
        });
        await accounts.AddAsync(new User
        {
            Id = admin,
            Username = "theadmin",
            PasswordHash = hasher.Hash("approver"),
            Role = Role.Admin,
            CreatedBy = seller,
            CreatedUtc = DateTimeOffset.Now,
        });

        return (supervisor, admin);
    }
}
