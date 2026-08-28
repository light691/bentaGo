namespace BentaGo.Infrastructure.Backup;

public interface IRestoreService
{
    Task<RestoreReceipt> RestoreAsync(string backupPath, CancellationToken cancellationToken = default);
}
