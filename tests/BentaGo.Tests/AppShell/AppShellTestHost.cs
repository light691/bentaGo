using System.IO;
using BentaGo.App.Mvvm;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Shared headless host for the AppShell unit tier: a throwaway data root,
/// the full BG-004 app registrations, and optional admin seeding
/// (bg-004-synthesis §10).
/// </summary>
internal sealed class AppShellTestHost : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "bentago-appshell-" + Guid.NewGuid().ToString("N"));

    public AppShellTestHost(bool seedAdmin = false, Action<IServiceCollection>? extra = null)
    {
        Services = BentaGoHost.Build(
            development: true,
            dataRootOverride: _tempRoot,
            extra: services =>
            {
                ObservableObjectFactory.RegisterAppServices(services);
                extra?.Invoke(services);
            });

        // Production boots by migrating BEFORE the shell exists (App.OnStartup
        // boot contract), and the app data source (BG-P1d) reads the live
        // SQLite fold — so the schema must exist before any view model
        // constructs. Admin seeding stays opt-in.
        Services.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync().GetAwaiter().GetResult();
        if (seedAdmin)
        {
            Services.GetRequiredService<IAdminSeeder>().SeedAsync().GetAwaiter().GetResult();
        }
    }

    public ServiceProvider Services { get; }

    public void Dispose() => Services.Dispose();
}
