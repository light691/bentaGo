using System.IO;
using BentaGo.Application.Contracts;
using BentaGo.Infrastructure.Backup;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Logging;
using BentaGo.Infrastructure.Persistence;
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
        services.AddSingleton<BentaGo.Application.Contracts.IOpenTripService, BentaGo.Application.Services.OpenTripService>();
        services.AddSingleton<BentaGo.Application.Contracts.ILoadService, BentaGo.Application.Services.LoadService>();
        services.AddSingleton<BentaGo.Application.Contracts.ICheckoutService, BentaGo.Application.Services.CheckoutService>();
        services.AddSingleton<BentaGo.Application.Contracts.IVoidService, BentaGo.Application.Services.VoidService>();
        services.AddSingleton<BentaGo.Application.Contracts.IReturnService, BentaGo.Application.Services.ReturnService>();
        services.AddSingleton<BentaGo.Application.Contracts.IAdjustmentService, BentaGo.Application.Services.AdjustmentService>();
        services.AddSingleton<BentaGo.Application.Contracts.ICountService, BentaGo.Application.Services.CountService>();
        services.AddSingleton<BentaGo.Application.Contracts.ICloseService, BentaGo.Application.Services.CloseService>();
        services.AddSingleton<BentaGo.Application.Contracts.IReopenService, BentaGo.Application.Services.ReopenService>();
        services.AddSingleton<BentaGo.Application.Contracts.IPrintTicketStore, BentaGo.Infrastructure.PrintTickets.PrintTicketStore>();

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
