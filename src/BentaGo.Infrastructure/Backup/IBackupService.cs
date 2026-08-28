namespace BentaGo.Infrastructure.Backup;

public interface IBackupService
{
    Task<BackupReceipt> CreateAsync(CancellationToken cancellationToken = default);
}
