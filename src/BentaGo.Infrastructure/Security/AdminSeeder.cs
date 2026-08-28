namespace BentaGo.Infrastructure.Security;

public interface IAdminSeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}