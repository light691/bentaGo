using System.IO;
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
        services.AddSingleton<BentaGo.Application.Contracts.ILoginService, LoginService>();
        services.AddSingleton<IAdminSeeder, AdminSeeder>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IRestoreService, RestoreService>();

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
