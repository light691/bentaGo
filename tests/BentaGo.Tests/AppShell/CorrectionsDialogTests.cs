using System;
using System.Linq;
using System.Threading.Tasks;
using BentaGo.App;
using BentaGo.App.Dialogs.Corrections;
using BentaGo.App.Features.Sales;
using BentaGo.App.Navigation.Shortcuts;
using BentaGo.Application.Contracts;
using BentaGo.Application.Scenario;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Catalog;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// BG-007c2 corrections-panel tests (docs/05 BG-007 lanes 6-10, app slice):
/// the F8 panel over the REAL 007b services on a migrated SQLite root —
/// peddler approval gate, supervisor self-approval with the audited
/// approver, partial returns to peddler stock vs main store, damage/loss/
/// increase/decrease movement kinds, STOCK_INSUFFICIENT on over-decrease,
/// and TRIP_CLOSED_CORRECTION on a closed trip. Follows the SalesSeed +
/// AppShellTestHost pattern (headless; the VMs are WPF-free).
/// </summary>
public sealed class CorrectionsDialogTests : IDisposable
{
    // The fixed "now" the panel's day filter and every command carry. Same
    // Manila day as the SalesSeed movements (2026-01-15), so the seeded
    // committed sale is "today's trip sale".
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 8, 30, 0, TimeSpan.Zero);

    private readonly AppShellTestHost _host;
    private readonly SeedIds _ids;

    public CorrectionsDialogTests()
    {
        _host = new AppShellTestHost(extra: services =>
        {
            // Deterministic "today" for the corrections panel (last
            // registration of Func<DateTimeOffset> wins in MS.DI).
            services.AddSingleton<Func<DateTimeOffset>>(() => Now);
        });
        SalesSeed.Seed(_host.Services, signIn: false);
        _ids = SeedAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _host.Dispose();

    private SqliteLedgerStore Store() => _host.Services.GetRequiredService<SqliteLedgerStore>();

    private CorrectionsDialogViewModel NewDialog() =>
        _host.Services.GetRequiredService<CorrectionsDialogViewModel>();

    private void SignIn(UserId userId) =>
        _host.Services.GetRequiredService<IActorContext>().Set(userId);

    // ------- (a) peddler cannot void without an approver -------

    [Fact]
    public async Task PeddlerVoidWithoutApproverShowsMessageAndMovesNothing()
    {
        SignIn(_ids.Seller);
        var dialog = NewDialog();
        await dialog.LoadAsync();

        Assert.False(dialog.HasFatalState);
        Assert.False(dialog.IsSelfApproval);
        Assert.True(dialog.NeedsApproverPicker);
        Assert.DoesNotContain(dialog.Approvers, a => a.Id == _ids.Seller); // peddlers never approve

        dialog.SelectMode(CorrectionsDialogViewModel.ModeVoid);
        dialog.SelectedSale = dialog.Sales.Single(s => s.Id == _ids.Sale);
        dialog.Reason = "wrong item scanned";

        long auditsBefore = Store().Audits.Count;
        int movementsBefore = Store().Movements.Count;

        dialog.SubmitCommand.Execute(null);

        Assert.True(dialog.HasErrorBanner);
        Assert.Equal("A void needs an approver. Choose a supervisor or admin above.", dialog.ErrorBanner);
        Assert.Empty(Store().Context.Voids);
        Assert.Equal(movementsBefore, Store().Movements.Count);
        Assert.Equal(auditsBefore, Store().Audits.Count);
    }

    // ------- (b) supervisor self-approves; the audit names the approver -------

    [Fact]
    public async Task SupervisorVoidSelfApprovesAndAuditRecordsTheApprover()
    {
        SignIn(_ids.Supervisor);
        var dialog = NewDialog();
        await dialog.LoadAsync();

        Assert.True(dialog.IsSelfApproval);
        Assert.False(dialog.NeedsApproverPicker);

        dialog.SelectMode(CorrectionsDialogViewModel.ModeVoid);
        dialog.SelectedSale = dialog.Sales.Single(s => s.Id == _ids.Sale);
        dialog.Reason = "customer cancelled";

        dialog.SubmitCommand.Execute(null);

        Assert.False(dialog.HasErrorBanner);
        SaleVoid @void = Assert.Single(Store().Context.Voids);
        Assert.Equal(_ids.Sale, @void.OriginalSaleId);
        Assert.Equal(_ids.Supervisor, @void.By);
        Assert.Equal(_ids.Supervisor, @void.ApprovedBy);

        AuditEvent audit = Assert.Single(
            Store().Audits, a => a.Category == "VOID");
        Assert.Contains("approved by", audit.Message);
        Assert.Contains(_ids.Supervisor.Value.ToString(), audit.Message);
        Assert.Equal("customer cancelled", audit.Reason);
    }

    // ------- (c1) partial return to peddler stock -------

    [Fact]
    public async Task ReturnPartialToPeddlerStockRestoresAtSaleLocation()
    {
        SignIn(_ids.Seller);
        Quantity onHandBefore = Store().State.StockOnHand(_ids.Product, _ids.Route);

        var dialog = NewDialog();
        await dialog.LoadAsync();
        dialog.SelectMode(CorrectionsDialogViewModel.ModeReturn);
        dialog.SelectedSale = dialog.Sales.Single(s => s.Id == _ids.Sale);

        ReturnLineOption line = Assert.Single(dialog.SaleLines);
        Assert.Equal(3, line.RemainingUnits);
        dialog.SelectedLine = line;
        dialog.QuantityText = "1";
        dialog.SelectedDestination = dialog.Destinations.Single(d => d.Location is null);
        dialog.Reason = "changed mind";

        dialog.SubmitCommand.Execute(null);

        Assert.False(dialog.HasErrorBanner);
        Return @return = Assert.Single(Store().Context.Returns);
        Assert.Equal(_ids.Sale, @return.OriginalSale);

        ReturnToStock movement = Assert.Single(
            Store().Movements.OfType<ReturnToStock>());
        Assert.Equal(_ids.Route, movement.LocationId);
        Assert.Equal(1, movement.Quantity.Units);
        Assert.Equal(onHandBefore.Units + 1, Store().State.StockOnHand(_ids.Product, _ids.Route).Units);
        Assert.Equal(SaleStatus.PartiallyReturned, Store().State.Sale(_ids.Sale).Status);
    }

    // ------- (c2) partial return to the main store -------

    [Fact]
    public async Task ReturnPartialToMainStoreLandsAtTheLoadSource()
    {
        SignIn(_ids.Seller);
        int storeBefore = Store().State.StockOnHand(_ids.Product, _ids.Store).Units;

        var dialog = NewDialog();
        await dialog.LoadAsync();
        dialog.SelectMode(CorrectionsDialogViewModel.ModeReturn);
        dialog.SelectedSale = dialog.Sales.Single(s => s.Id == _ids.Sale);
        dialog.SelectedLine = Assert.Single(dialog.SaleLines);
        dialog.QuantityText = "2";
        DestinationOption destination =
            Assert.Single(dialog.Destinations, d => d.Location is not null);
        dialog.SelectedDestination = destination;
        dialog.Reason = "wrong flavor";

        dialog.SubmitCommand.Execute(null);

        Assert.False(dialog.HasErrorBanner);
        ReturnToStock movement = Assert.Single(
            Store().Movements.OfType<ReturnToStock>());
        Assert.Equal(_ids.Store, movement.LocationId);
        Assert.Equal(2, movement.Quantity.Units);
        Assert.Equal(storeBefore + 2, Store().State.StockOnHand(_ids.Product, _ids.Store).Units);
    }

    // ------- (d) damage / loss / increase / decrease land with the right kinds -------

    [Fact]
    public async Task DamageLossAndAdjustmentMovementsLandWithTheRightKinds()
    {
        SignIn(_ids.Supervisor);

        // DAMAGE (decrease-kind movement, distinct typed fact).
        var dialog = NewDialog();
        await dialog.LoadAsync();
        dialog.SelectMode(CorrectionsDialogViewModel.ModeDamageLoss);
        Assert.Equal(AdjustmentKind.Damage, dialog.SelectedKind!.Kind);
        dialog.SelectKind(AdjustmentKind.Loss);
        Assert.Equal(AdjustmentKind.Loss, dialog.SelectedKind.Kind);
        dialog.SelectKind(AdjustmentKind.Damage);
        dialog.SelectedProduct = dialog.Products.Single(p => p.Product == _ids.Product);
        dialog.QuantityText = "2";
        dialog.Reason = "cracked bottles";
        dialog.SubmitCommand.Execute(null);
        Assert.False(dialog.HasErrorBanner);

        Damage damage = Assert.Single(Store().Movements.OfType<Damage>());
        Assert.Equal(_ids.Product, damage.ProductId);
        Assert.Equal(_ids.Route, damage.LocationId);
        Assert.Equal(2, damage.Quantity.Units);
        Assert.Equal("cracked bottles", damage.Reason);

        // INCREASE / DECREASE through the adjustment form.
        dialog = NewDialog();
        await dialog.LoadAsync();
        dialog.SelectMode(CorrectionsDialogViewModel.ModeAdjustment);
        dialog.SelectedProduct = dialog.Products.Single(p => p.Product == _ids.Product);
        dialog.QuantityText = "5";
        dialog.Reason = "found a case";
        dialog.SubmitCommand.Execute(null);
        Assert.False(dialog.HasErrorBanner);

        AdjustmentIncrease increase = Assert.Single(Store().Movements.OfType<AdjustmentIncrease>());
        Assert.Equal(5, increase.Quantity.Units);

        dialog = NewDialog();
        await dialog.LoadAsync();
        dialog.SelectMode(CorrectionsDialogViewModel.ModeAdjustment);
        dialog.SelectedProduct = dialog.Products.Single(p => p.Product == _ids.Product);
        dialog.SelectKind(AdjustmentKind.Decrease);
        dialog.QuantityText = "1";
        dialog.Reason = "count correction";
        dialog.SubmitCommand.Execute(null);
        Assert.False(dialog.HasErrorBanner);

        AdjustmentDecrease decrease = Assert.Single(Store().Movements.OfType<AdjustmentDecrease>());
        Assert.Equal(1, decrease.Quantity.Units);

        // The audit trail carries the approval the builder demanded.
        Assert.Equal(3, Store().Audits.Count(a => a.Category == "ADJUSTMENT"));
        Assert.All(
            Store().Audits.Where(a => a.Category == "ADJUSTMENT"),
            a => Assert.Contains($"approved by {_ids.Supervisor}", a.Message));
    }

    // ------- (e) STOCK_INSUFFICIENT on over-decrease -------

    [Fact]
    public async Task OverDecreaseShowsStockInsufficientAndMovesNothing()
    {
        SignIn(_ids.Supervisor);
        var dialog = NewDialog();
        await dialog.LoadAsync();
        dialog.SelectMode(CorrectionsDialogViewModel.ModeAdjustment);
        dialog.SelectedProduct = dialog.Products.Single(p => p.Product == _ids.Product);
        dialog.SelectKind(AdjustmentKind.Decrease);
        dialog.QuantityText = "9999";
        dialog.Reason = "way off";

        int movementsBefore = Store().Movements.Count;
        long auditsBefore = Store().Audits.Count;

        dialog.SubmitCommand.Execute(null);

        Assert.True(dialog.HasErrorBanner);
        Assert.Equal("Not enough stock on hand for that quantity.", dialog.ErrorBanner);
        Assert.Equal(movementsBefore, Store().Movements.Count);
        Assert.Equal(auditsBefore, Store().Audits.Count);
    }

    // ------- (f) closed trip → TRIP_CLOSED_CORRECTION -------

    [Fact]
    public async Task CorrectionOnClosedTripShowsTripClosedCorrection()
    {
        SignIn(_ids.Supervisor);

        // Close the trip with the counted cash equal to its own receipts.
        Money expectedCash = Store().State.TripCash(_ids.Trip, Fund.PhysicalCash);
        Result<CloseOutcome> closed = _host.Services.GetRequiredService<ICloseService>().Close(
            new CloseCommand(
                new CloseId(Guid.NewGuid()), _ids.Trip, expectedCash, null,
                _ids.Supervisor, Now));
        Assert.True(closed.IsSuccess, closed.Errors.FirstOrDefault()?.Code ?? "close failed");
        Assert.Equal(TripStatus.Closed, Store().State.Trip(_ids.Trip).Status);

        var dialog = NewDialog();
        await dialog.LoadAsync();
        dialog.SelectMode(CorrectionsDialogViewModel.ModeVoid);
        dialog.SelectedSale = dialog.Sales.Single(s => s.Id == _ids.Sale);
        dialog.Reason = "late correction";

        dialog.SubmitCommand.Execute(null);

        Assert.True(dialog.HasErrorBanner);
        Assert.Equal(
            "This trip is closed - corrections need an authorized reopen.",
            dialog.ErrorBanner);
        Assert.Empty(Store().Context.Voids);
    }

    // ------- F8 shell wiring -------

    [Fact]
    public void F8EntryIsActiveAndMappedToTheShellCommand()
    {
        ShortcutEntry entry = Assert.Single(ShortcutCatalog.All, e => e.Id == "returns");
        Assert.Equal(ShortcutStatus.Active, entry.Status);
        Assert.Equal(System.Windows.Input.Key.F8, entry.Key);

        var main = _host.Services.GetRequiredService<MainViewModel>();
        (System.Windows.Input.ICommand command, _) = main.CommandMapping("returns");
        Assert.NotNull(command);
    }

    [Theory]
    [InlineData("VOID_APPROVAL_REQUIRED", "A void needs an approver. Choose a supervisor or admin.")]
    [InlineData("ADJUSTMENT_APPROVAL_REQUIRED", "An adjustment needs an approver. Choose a supervisor or admin.")]
    [InlineData("TRIP_CLOSED_CORRECTION", "This trip is closed - corrections need an authorized reopen.")]
    [InlineData("STOCK_INSUFFICIENT", "Not enough stock on hand for that quantity.")]
    public void FriendlyErrorSurfacesCorrectionCodes(string code, string expected) =>
        Assert.Equal(expected, SalesViewModel.FriendlyError(code));

    // ====================================================================
    // Seed
    // ====================================================================

    private sealed record SeedIds(
        UserId Seller, UserId Supervisor, UserId Admin,
        PeddlerId Peddler, TripId Trip, ProductId Product,
        LocationId Route, LocationId Store, SaleId Sale);

    /// <summary>Extends <see cref="SalesSeed"/> with the approver accounts and
    /// one committed cash sale (3 × sardines at ₱45) on the seeded trip.</summary>
    private async Task<SeedIds> SeedAsync()
    {
        var ids = new ScenarioIdSource("appshell-corrections-v1");
        SqliteLedgerStore store = Store();

        Trip trip = store.Context.Trips.Single();
        PeddlerId peddler = trip.Peddler;
        LocationId route = trip.Location;

        // The main store is where the seed's load left from.
        LocationId storeLocation = store.Movements.OfType<TransferOut>().First().LocationId;

        Product product = (await _host.Services.GetRequiredService<IProductStore>()
            .FindBySkuAsync(SalesSeed.SardinesSku))!;

        IPasswordHasher hasher = _host.Services.GetRequiredService<IPasswordHasher>();
        IUserAccountStore accounts = _host.Services.GetRequiredService<IUserAccountStore>();
        User seller = (await accounts.FindByUsernameAsync(SalesSeed.SellerUsername))!;

        var supervisorId = new UserId(ids.Next("supervisor"));
        var adminId = new UserId(ids.Next("admin"));
        await accounts.AddAsync(new User
        {
            Id = supervisorId,
            Username = "maysupervisor",
            PasswordHash = hasher.Hash("approver"),
            Role = Role.Supervisor,

            // The panel derives its trip context from the actor's peddler link
            // (the CheckoutSessionFactory chain): a supervisor working the POS
            // is linked to the peddler whose trip they correct. An unlinked
            // supervisor gets the NO_PEDDLER_LINK fatal state instead.
            PeddlerLink = peddler,
            CreatedBy = seller.Id,
            CreatedUtc = Now,
        });
        await accounts.AddAsync(new User
        {
            Id = adminId,
            Username = "theadmin",
            PasswordHash = hasher.Hash("approver"),
            Role = Role.Admin,
            CreatedBy = seller.Id,
            CreatedUtc = Now,
        });

        SaleId saleId = new(ids.Next("sale"));
        var lineId = new SaleLineId(ids.Next("line"));
        var paymentId = new PaymentLineId(ids.Next("payment"));
        Money price = Money.FromPesos(45m);
        Money lineTotal = price.Times(3);

        store.Bind();
        Result<SaleDraft> draft = SaleBuilder.Build(store.Context, new NewSaleCommand(
            saleId, trip.Id, "R-TEST-001", seller.Id, Now,
            new[]
            {
                new NewSaleLine(
                    lineId, product.Id, product.Sku, product.Name, price,
                    Quantity.Of(3), Money.Zero),
            },
            new[]
            {
                new NewPaymentLine(
                    paymentId, PaymentMethod.Cash, "", lineTotal, lineTotal, Money.Zero),
            }));
        Assert.True(draft.IsSuccess, draft.Errors.FirstOrDefault()?.Code ?? "sale build failed");
        store.RegisterSale(draft.Value.Document);
        store.Bind();
        Result<AppendReceipt> appended = store.Append(draft.Value.Movements);
        Assert.True(appended.IsSuccess, appended.Errors.FirstOrDefault()?.Code ?? "sale append failed");

        return new SeedIds(
            seller.Id, supervisorId, adminId, peddler, trip.Id, product.Id,
            route, storeLocation, saleId);
    }
}
