using System.IO;
using BentaGo.Application.Contracts;
using BentaGo.Infrastructure.Backup;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Logging;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Printing;
using BentaGo.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BentaGo.Infrastructure.Hosting;

/// <summary>
/// Composition root for the BentaGo host. Builds the service provider used by
/// the WPF app, the tools CLI, and integration tests.
/// </summary>
public static class BentaGoHost
{
    public static ServiceProvider Build(
        bool development = false,
        string? dataRootOverride = null,
        Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();

        IConfiguration configuration = AppConfiguration.Load();
        services.AddSingleton(configuration);
        services.Configure<AppSettings>(configuration.GetSection("AppSettings"));

        var paths = new DataPathProvider(dataRootOverride);
        paths.EnsureCreated();
        services.AddSingleton<IDataPathProvider>(paths);
        services.AddSingleton(paths);

        LoggingFactory.Configure(services, paths, development);

        services.AddSingleton(new BentaGoDbContextFactory(
            $"Data Source={Path.Combine(paths.DataDir, "bentago.db")}"));
        services.AddSingleton<IDbContextFactory<BentaGoDbContext>>(sp =>
            sp.GetRequiredService<BentaGoDbContextFactory>());
        services.AddSingleton<DbInitializer>();

        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddSingleton<Func<DateTimeOffset>>(() => DateTimeOffset.UtcNow);
        services.AddSingleton<LoginService>();
        services.AddSingleton<BentaGo.Application.Contracts.ILoginService>(sp =>
            sp.GetRequiredService<LoginService>());
        services.AddSingleton<BentaGo.Application.Contracts.IAuthenticationService>(sp =>
            sp.GetRequiredService<LoginService>());
        // Session actor (BG-005d F8 wiring): the singleton IActorContext is
        // consumed by LoginService (set on login success only — see its class
        // doc for the lifetime semantics: failure leaves it unchanged, host
        // start begins cleared as a fresh singleton, sign-out is the future
        // BG-005d-ui Clear call) and by UserAccountService (actor binding:
        // cmd.By must equal CurrentUserId).
        services.AddSingleton<BentaGo.Application.Contracts.IActorContext, BentaGo.Application.Services.ActorContext>();
        services.AddSingleton<BentaGo.Application.Contracts.IUserAccountStore, UserAccountStore>();
        services.AddSingleton<BentaGo.Application.Contracts.IUserAccountService, BentaGo.Application.Services.UserAccountService>();
        services.AddSingleton<IAdminSeeder, AdminSeeder>();
        services.AddSingleton<IBackupService, BackupService>();

        // The one long-lived SQLite ledger connection must be closed before
        // the restore hook moves the -wal/-shm companions (BG-003 R1).
        services.AddSingleton<DebugOptions>();
        services.AddSingleton<SqliteLedgerStore>();
        services.AddSingleton<BentaGo.Application.Contracts.ILedgerStore>(sp =>
            sp.GetRequiredService<SqliteLedgerStore>());

        // BG-003 use cases (store-agnostic; the append's transaction IS the
        // use case's transaction) + print state outside the sale transaction.
        services.AddSingleton<BentaGo.Application.Contracts.ICheckoutService, BentaGo.Application.Services.CheckoutService>();
        services.AddSingleton<BentaGo.Application.Contracts.IVoidService, BentaGo.Application.Services.VoidService>();
        services.AddSingleton<BentaGo.Application.Contracts.IReturnService, BentaGo.Application.Services.ReturnService>();
        services.AddSingleton<BentaGo.Application.Contracts.IAdjustmentService, BentaGo.Application.Services.AdjustmentService>();
        services.AddSingleton<BentaGo.Application.Contracts.ICountService, BentaGo.Application.Services.CountService>();
        services.AddSingleton<BentaGo.Application.Contracts.ICloseService, BentaGo.Application.Services.CloseService>();
        services.AddSingleton<BentaGo.Application.Contracts.IReopenService, BentaGo.Application.Services.ReopenService>();
        services.AddSingleton<BentaGo.Application.Contracts.IPrintTicketStore, BentaGo.Infrastructure.PrintTickets.PrintTicketStore>();

        // BG-006 caller surface: catalog search, checkout session, receipt
        // numbers, and transaction history (all additive over the unchanged
        // seam; the WPF app additionally registers its App-layer services in
        // RegisterAppServices).
        services.AddSingleton<BentaGo.Application.Contracts.IProductStore, ProductStore>();
        services.AddSingleton<BentaGo.Application.Contracts.ISaleNumberSource, BentaGo.Application.Services.CommittedSaleNumberSource>();
        services.AddSingleton<BentaGo.Application.Contracts.ICheckoutSessionFactory, BentaGo.Application.Services.CheckoutSessionFactory>();
        services.AddSingleton<BentaGo.Application.Contracts.ISaleQueryService, BentaGo.Application.Services.SaleQueryService>();
        services.AddSingleton<BentaGo.Application.Contracts.IProductSearchService, BentaGo.Application.Services.ProductSearchService>();
        services.AddSingleton<BentaGo.Application.Contracts.IPrintJobStore, PrintJobStore>();
        services.AddSingleton<BentaGo.Core.Domain.Receipts.IReceiptPrinter>(new FakeReceiptPrinter());
        services.AddSingleton<BentaGo.Application.Contracts.IPrintService, BentaGo.Application.Services.PrintService>();
        services.AddSingleton<BentaGo.Application.Contracts.IPrinterSettingsStore, PrinterSettingsStore>();
        services.AddSingleton<BentaGo.Application.Contracts.IScannerDiagnostics, ScannerDiagnostics>();
        services.AddSingleton<BentaGo.Application.Contracts.IReconciliationReportService, BentaGo.Application.Services.ReconciliationReportService>();

        services.AddSingleton<IRestoreService>(sp => new RestoreService(
            sp.GetRequiredService<IDataPathProvider>(),
            sp.GetRequiredService<IDbContextFactory<BentaGoDbContext>>(),
            () =>
            {
                sp.GetRequiredService<SqliteLedgerStore>().Close();
                return Task.CompletedTask;
            }));

        extra?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds a host for integration tests against a throwaway data root.
    /// Uses an ordinary file-backed SQLite database under the temp root
    /// (no in-memory, no shared cache).
    /// </summary>
    public static ServiceProvider BuildForTests(string tempDataRoot)
    {
        return Build(development: true, dataRootOverride: tempDataRoot);
    }
}
